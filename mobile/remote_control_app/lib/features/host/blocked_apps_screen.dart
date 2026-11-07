import 'dart:convert';

import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../app/providers.dart';
import '../../host/host_input.dart';

/// The owner's block list: apps a viewer can neither see nor touch.
///
/// Every app with a launcher icon, each with a switch. The list is kept on this phone and read by
/// the host the moment it changes, so blocking an app in the middle of a session hides it from the
/// very next frame; the viewer sees a placeholder until another app is in front.
class BlockedAppsScreen extends ConsumerStatefulWidget {
  const BlockedAppsScreen({super.key});

  @override
  ConsumerState<BlockedAppsScreen> createState() => _BlockedAppsScreenState();
}

class _BlockedAppsScreenState extends ConsumerState<BlockedAppsScreen> {
  final _search = TextEditingController();
  List<InstalledApp>? _apps;
  String? _error;

  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void dispose() {
    _search.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    try {
      final apps = await ref.read(hostControllerProvider).listApps();
      if (mounted) setState(() => _apps = apps);
    } on Object catch (error) {
      if (mounted) setState(() => _error = 'The app list could not be read: $error');
    }
  }

  @override
  Widget build(BuildContext context) {
    final host = ref.watch(hostControllerProvider);
    final gate = host.gate;
    final query = _search.text.trim().toLowerCase();

    final apps = _apps;
    final shown = apps == null
        ? const <InstalledApp>[]
        : [
            for (final app in apps)
              if (query.isEmpty || app.name.toLowerCase().contains(query) || app.package.contains(query)) app,
          ]
          ..sort((a, b) {
            final ab = gate.isPackageBlocked(a.package);
            final bb = gate.isPackageBlocked(b.package);
            if (ab != bb) return ab ? -1 : 1;
            return a.name.toLowerCase().compareTo(b.name.toLowerCase());
          });

    return Scaffold(
      appBar: AppBar(title: const Text('Blocked apps')),
      body: Column(
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 12, 16, 4),
            child: Text(
              'Viewers never see a blocked app: while it is in front they get a placeholder and their '
              'touches are ignored. Blocked apps cannot be opened remotely either. Changes apply at '
              'once, even mid-session.',
              style: Theme.of(context).textTheme.bodySmall,
            ),
          ),
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
            child: TextField(
              controller: _search,
              onChanged: (_) => setState(() {}),
              decoration: InputDecoration(
                hintText: 'Search apps',
                prefixIcon: const Icon(Icons.search),
                isDense: true,
                border: const OutlineInputBorder(),
                suffixIcon: _search.text.isEmpty
                    ? null
                    : IconButton(
                        icon: const Icon(Icons.clear),
                        onPressed: () => setState(_search.clear),
                      ),
              ),
            ),
          ),
          if (gate.blocked.isNotEmpty)
            Padding(
              padding: const EdgeInsets.symmetric(horizontal: 16),
              child: Text(
                '${gate.blocked.length} blocked',
                style: Theme.of(context).textTheme.labelMedium,
              ),
            ),
          Expanded(
            child: _error != null
                ? Center(child: Padding(padding: const EdgeInsets.all(24), child: Text(_error!)))
                : apps == null
                    ? const Center(child: CircularProgressIndicator())
                    : shown.isEmpty
                        ? const Center(child: Text('No apps matched.'))
                        : ListView.builder(
                            itemCount: shown.length,
                            itemBuilder: (context, index) {
                              final app = shown[index];
                              final blocked = gate.isPackageBlocked(app.package);

                              return SwitchListTile(
                                secondary: _icon(app),
                                title: Text(app.name, maxLines: 1, overflow: TextOverflow.ellipsis),
                                subtitle: Text(
                                  blocked ? 'Blocked · hidden from viewers' : app.package,
                                  maxLines: 1,
                                  overflow: TextOverflow.ellipsis,
                                ),
                                value: blocked,
                                onChanged: (value) => host.setAppBlocked(app.package, value),
                              );
                            },
                          ),
          ),
        ],
      ),
    );
  }

  Widget _icon(InstalledApp app) {
    final icon = app.iconPng;
    if (icon == null || icon.isEmpty) return const Icon(Icons.apps);

    try {
      return Image.memory(
        base64Decode(icon),
        width: 32,
        height: 32,
        errorBuilder: (_, __, ___) => const Icon(Icons.apps),
      );
    } on FormatException {
      return const Icon(Icons.apps);
    }
  }
}
