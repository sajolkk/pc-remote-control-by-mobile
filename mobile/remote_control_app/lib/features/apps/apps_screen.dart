import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../app/providers.dart';
import '../../protocol/protocol.dart';
import '../system/system_screen.dart';

/// One application as the PC reported it.
class _App {
  const _App({
    required this.id,
    required this.name,
    required this.running,
    required this.hasWindow,
    this.publisher,
    this.iconPng,
  });

  factory _App.fromJson(Map<String, dynamic> json) => _App(
        id: json['id'] as String? ?? '',
        name: json['name'] as String? ?? 'Unknown',
        running: json['running'] as bool? ?? false,
        hasWindow: json['hasWindow'] as bool? ?? false,
        publisher: json['publisher'] as String?,
        iconPng: json['iconPng'] as String?,
      );

  final String id;
  final String name;
  final bool running;
  final bool hasWindow;
  final String? publisher;
  final String? iconPng;
}

/// Applications and browser control.
///
/// The list comes entirely from the PC: this screen has no idea what software exists until it
/// asks, and it can only ever act on an id the PC itself produced. That is the same property
/// the PC side relies on — there is no field here through which a path or command line could
/// be sent.
class AppsScreen extends ConsumerStatefulWidget {
  const AppsScreen({super.key});

  @override
  ConsumerState<AppsScreen> createState() => _AppsScreenState();
}

class _AppsScreenState extends ConsumerState<AppsScreen> {
  final _searchController = TextEditingController();

  List<_App> _apps = const [];
  List<Map<String, dynamic>> _browsers = const [];
  bool _loading = false;
  bool _runningOnly = false;
  String? _error;

  @override
  void dispose() {
    _searchController.dispose();
    super.dispose();
  }

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addPostFrameCallback((_) => _load());
  }

  @override
  Widget build(BuildContext context) {
    final status = ref.watch(connectionProvider).status;

    if (!status.isConnected) {
      return const NotConnectedView();
    }

    if (!status.granted(Permissions.launchApps)) {
      return Scaffold(
        appBar: AppBar(title: const Text('Apps')),
        body: const Center(
          child: Padding(
            padding: EdgeInsets.all(32),
            child: Text(
              'This device does not have permission to open or close apps.\n\n'
              'Open PC-Remote on the PC, find this device under Paired Devices, and turn on '
              '"Open and close apps".',
              textAlign: TextAlign.center,
            ),
          ),
        ),
      );
    }

    if (!status.has(Capabilities.apps) && !status.has(Capabilities.browser)) {
      return Scaffold(
        appBar: AppBar(title: const Text('Apps')),
        body: Center(
          child: Padding(
            padding: const EdgeInsets.all(32),
            child: Text(
              status.isLoggedOut
                  ? 'Nobody is signed in to the PC, so its applications cannot be listed.'
                  : 'This PC did not report any applications it can launch.',
              textAlign: TextAlign.center,
            ),
          ),
        ),
      );
    }

    return Scaffold(
      appBar: AppBar(
        title: const Text('Apps'),
        actions: [
          IconButton(
            tooltip: _runningOnly ? 'Show all apps' : 'Show only running apps',
            onPressed: () {
              setState(() => _runningOnly = !_runningOnly);
              _load();
            },
            icon: Icon(_runningOnly ? Icons.filter_alt : Icons.filter_alt_outlined),
          ),
          IconButton(
            tooltip: 'Refresh',
            onPressed: _loading ? null : _load,
            icon: const Icon(Icons.refresh),
          ),
        ],
      ),
      body: Column(
        children: [
          if (status.has(Capabilities.browser)) _BrowserBar(
            browsers: _browsers,
            onOpenUrl: _openUrl,
            onOpenBrowser: _openBrowser,
          ),

          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
            child: TextField(
              controller: _searchController,
              onSubmitted: (_) => _load(),
              textInputAction: TextInputAction.search,
              decoration: InputDecoration(
                hintText: 'Search installed apps',
                prefixIcon: const Icon(Icons.search),
                isDense: true,
                border: const OutlineInputBorder(),
                suffixIcon: _searchController.text.isEmpty
                    ? null
                    : IconButton(
                        icon: const Icon(Icons.clear),
                        onPressed: () {
                          _searchController.clear();
                          _load();
                        },
                      ),
              ),
            ),
          ),

          if (_error != null)
            Padding(
              padding: const EdgeInsets.all(16),
              child: Text(
                _error!,
                style: TextStyle(color: Theme.of(context).colorScheme.error),
              ),
            ),

          if (_loading) const LinearProgressIndicator(),

          Expanded(
            child: _apps.isEmpty && !_loading
                ? const Center(child: Text('No apps matched.'))
                : ListView.builder(
                    itemCount: _apps.length,
                    itemBuilder: (context, index) => _AppTile(
                      app: _apps[index],
                      onLaunch: () => _act(Commands.appLaunch, _apps[index], 'Launched'),
                      onFocus: () => _act(Commands.appFocus, _apps[index], 'Brought to front'),
                      onClose: () => _close(_apps[index]),
                    ),
                  ),
          ),
        ],
      ),
    );
  }

  Future<void> _load() async {
    setState(() {
      _loading = true;
      _error = null;
    });

    try {
      final connection = ref.read(connectionProvider);

      final response = await connection.send(Commands.appList, args: {
        'runningOnly': _runningOnly,
        if (_searchController.text.trim().isNotEmpty) 'query': _searchController.text.trim(),

        // Icons are opt-in on the PC because extraction costs a file read per app. Requested
        // here because a launcher without icons is markedly harder to scan visually.
        'includeIcons': true,
        'limit': 300,
      });

      if (!mounted) return;

      if (response == null) {
        setState(() => _error = 'Not connected to the PC.');
        return;
      }

      if (!response.ok) {
        setState(() => _error = _explain(response.error));
        return;
      }

      final apps = (response.map['apps'] as List<dynamic>? ?? const [])
          .whereType<Map<String, dynamic>>()
          .map(_App.fromJson)
          .toList();

      final browserResponse = await connection.send(Commands.browserList);

      final browsers = browserResponse != null && browserResponse.ok
          ? (browserResponse.map['browsers'] as List<dynamic>? ?? const [])
              .whereType<Map<String, dynamic>>()
              .toList()
          : const <Map<String, dynamic>>[];

      if (!mounted) return;

      setState(() {
        _apps = apps;
        _browsers = browsers;
      });
    } finally {
      if (mounted) setState(() => _loading = false);
    }
  }

  Future<void> _act(String command, _App app, String verb) async {
    final response = await ref.read(connectionProvider).send(command, args: {'appId': app.id});
    if (!mounted) return;

    if (response == null) {
      _toast('Not connected.');
      return;
    }

    if (!response.ok) {
      _toast(_explain(response.error));
      return;
    }

    // The PC reports partial success honestly — for instance when Windows refuses a focus
    // change from a background process — so show its detail rather than a blanket "done".
    final detail = response.map['detail'] as String?;
    final succeeded = response.map['succeeded'] as bool? ?? true;

    _toast(detail ?? (succeeded ? '$verb ${app.name}.' : 'Could not $verb ${app.name}.'));
    await _load();
  }

  Future<void> _close(_App app) async {
    // Graceful first. The PC will not force-terminate unless asked, so an app with unsaved
    // work gets to put its own prompt on screen.
    final response = await ref
        .read(connectionProvider)
        .send(Commands.appClose, args: {'appId': app.id, 'force': false});

    if (!mounted) return;

    if (response == null) {
      _toast('Not connected.');
      return;
    }

    if (!response.ok) {
      _toast(_explain(response.error));
      return;
    }

    final stillRunning = response.map['running'] as bool? ?? false;

    if (!stillRunning) {
      _toast('Closed ${app.name}.');
      await _load();
      return;
    }

    // Forcing is offered only after a graceful attempt failed, and it says what it costs.
    final force = await showDialog<bool>(
      context: context,
      builder: (dialogContext) => AlertDialog(
        title: Text('${app.name} did not close'),
        content: Text(
          response.map['detail'] as String? ??
              'The app is still running. It may be showing a prompt on the PC.\n\n'
                  'Forcing it to close will discard unsaved work.',
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.of(dialogContext).pop(false),
            child: const Text('Leave it'),
          ),
          FilledButton(
            onPressed: () => Navigator.of(dialogContext).pop(true),
            child: const Text('Force close'),
          ),
        ],
      ),
    );

    if (force != true) return;

    final forced = await ref
        .read(connectionProvider)
        .send(Commands.appClose, args: {'appId': app.id, 'force': true});

    if (!mounted) return;

    _toast(forced != null && forced.ok ? 'Terminated ${app.name}.' : 'Could not close ${app.name}.');
    await _load();
  }

  Future<void> _openUrl(String url, String? browserId) async {
    final response = await ref.read(connectionProvider).send(
          Commands.browserOpenUrl,
          args: {'url': url, if (browserId != null) 'browserId': browserId},
        );

    if (!mounted) return;

    if (response == null) {
      _toast('Not connected.');
      return;
    }

    _toast(response.ok ? 'Opened on the PC.' : _explain(response.error));
  }

  Future<void> _openBrowser(String? browserId) async {
    final response = await ref.read(connectionProvider).send(
          Commands.browserOpen,
          args: {if (browserId != null) 'browserId': browserId},
        );

    if (!mounted) return;
    _toast(response != null && response.ok ? 'Opened.' : _explain(response?.error));
  }

  String _explain(ProtocolError? error) => switch (error?.code) {
        ErrorCodes.permissionDenied => 'This device cannot open or close apps. Enable it on the PC.',
        ErrorCodes.sessionUnavailable =>
          'Nobody is signed in to the PC, so its apps are unavailable.',
        ErrorCodes.invalidArguments => error?.message ?? 'That request was not valid.',
        ErrorCodes.notFound => error?.message ?? 'That app is no longer there.',
        _ => error?.message ?? 'The command failed.',
      };

  void _toast(String message) {
    ScaffoldMessenger.of(context)
      ..clearSnackBars()
      ..showSnackBar(SnackBar(content: Text(message)));
  }
}

class _AppTile extends StatelessWidget {
  const _AppTile({
    required this.app,
    required this.onLaunch,
    required this.onFocus,
    required this.onClose,
  });

  final _App app;
  final VoidCallback onLaunch;
  final VoidCallback onFocus;
  final VoidCallback onClose;

  @override
  Widget build(BuildContext context) {
    Widget leading = const Icon(Icons.apps);

    final icon = app.iconPng;
    if (icon != null && icon.isNotEmpty) {
      try {
        leading = Image.memory(
          base64Decode(icon),
          width: 28,
          height: 28,
          filterQuality: FilterQuality.medium,

          // A malformed icon must not take the list down with it.
          errorBuilder: (_, __, ___) => const Icon(Icons.apps),
        );
      } on FormatException {
        leading = const Icon(Icons.apps);
      }
    }

    return ListTile(
      leading: SizedBox(width: 32, child: Center(child: leading)),
      title: Text(app.name, maxLines: 1, overflow: TextOverflow.ellipsis),
      subtitle: Text(
        app.running
            ? (app.hasWindow ? 'Running' : 'Running in the background')
            : (app.publisher ?? ''),
        maxLines: 1,
        overflow: TextOverflow.ellipsis,
      ),
      trailing: Row(
        mainAxisSize: MainAxisSize.min,
        children: [
          if (app.running && app.hasWindow)
            IconButton(
              tooltip: 'Bring to front',
              onPressed: onFocus,
              icon: const Icon(Icons.open_in_new),
            ),
          if (app.running)
            IconButton(
              tooltip: 'Close',
              onPressed: onClose,
              icon: const Icon(Icons.close),
            )
          else
            IconButton(
              tooltip: 'Launch',
              onPressed: onLaunch,
              icon: const Icon(Icons.play_arrow),
            ),
        ],
      ),
    );
  }
}

class _BrowserBar extends StatefulWidget {
  const _BrowserBar({
    required this.browsers,
    required this.onOpenUrl,
    required this.onOpenBrowser,
  });

  final List<Map<String, dynamic>> browsers;
  final Future<void> Function(String url, String? browserId) onOpenUrl;
  final Future<void> Function(String? browserId) onOpenBrowser;

  @override
  State<_BrowserBar> createState() => _BrowserBarState();
}

class _BrowserBarState extends State<_BrowserBar> {
  final _urlController = TextEditingController();
  String? _browserId;

  @override
  void dispose() {
    _urlController.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    return Card(
      margin: const EdgeInsets.fromLTRB(12, 12, 12, 0),
      child: Padding(
        padding: const EdgeInsets.all(12),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                const Icon(Icons.language, size: 18),
                const SizedBox(width: 8),
                Text('Open a web page', style: Theme.of(context).textTheme.titleSmall),
              ],
            ),
            const SizedBox(height: 10),
            Row(
              children: [
                Expanded(
                  child: TextField(
                    controller: _urlController,
                    keyboardType: TextInputType.url,
                    textInputAction: TextInputAction.go,
                    onSubmitted: (_) => _submit(),
                    decoration: const InputDecoration(
                      hintText: 'example.com',
                      isDense: true,
                      border: OutlineInputBorder(),
                    ),
                  ),
                ),
                const SizedBox(width: 8),
                FilledButton(onPressed: _submit, child: const Text('Open')),
              ],
            ),
            if (widget.browsers.isNotEmpty) ...[
              const SizedBox(height: 10),
              Wrap(
                spacing: 8,
                runSpacing: 4,
                children: [
                  ChoiceChip(
                    label: const Text('Default'),
                    selected: _browserId == null,
                    onSelected: (_) => setState(() => _browserId = null),
                  ),
                  for (final browser in widget.browsers)
                    ChoiceChip(
                      label: Text(browser['name'] as String? ?? 'Browser'),
                      selected: _browserId == browser['id'],
                      onSelected: (_) => setState(() => _browserId = browser['id'] as String?),
                    ),
                ],
              ),
              const SizedBox(height: 4),
              Align(
                alignment: Alignment.centerLeft,
                child: TextButton.icon(
                  onPressed: () => widget.onOpenBrowser(_browserId),
                  icon: const Icon(Icons.launch, size: 16),
                  label: const Text('Just open the browser'),
                ),
              ),
            ],
          ],
        ),
      ),
    );
  }

  void _submit() {
    final raw = _urlController.text.trim();
    if (raw.isEmpty) return;

    // A convenience, not a validation: the PC enforces http/https regardless, and refuses
    // anything else. Prefixing here just means the user does not have to type "https://".
    final url = raw.contains('://') ? raw : 'https://$raw';

    widget.onOpenUrl(url, _browserId);
  }
}
