import 'package:flutter/material.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../app/providers.dart';
import '../files/files_screen.dart';
import '../system/system_screen.dart';
import '../windows/windows_screen.dart';
import 'apps_screen.dart';

enum _Section { apps, windows, files }

/// The fourth tab: what is on the PC — its apps, its open windows and its files.
///
/// Three related screens behind one destination, chosen with a switch at the top, because the
/// bottom bar is full at five and these belong together anyway: each is a list the PC reports
/// and the phone acts on. The switch is always visible, so nothing is hidden behind a menu.
class ManageScreen extends ConsumerStatefulWidget {
  const ManageScreen({super.key});

  @override
  ConsumerState<ManageScreen> createState() => _ManageScreenState();
}

class _ManageScreenState extends ConsumerState<ManageScreen> {
  _Section _section = _Section.apps;

  /// The sections opened so far. Each is built on first use and then kept, so going to look at
  /// the open windows does not lose the folder being browsed — and none of them asks the PC for
  /// anything until the user actually goes there.
  final _opened = <_Section>{_Section.apps};

  @override
  Widget build(BuildContext context) {
    final connected = ref.watch(connectionStatusProvider.select((s) => s.isConnected));

    if (!connected) {
      return const NotConnectedView();
    }

    return Column(
      children: [
        Material(
          color: Theme.of(context).colorScheme.surface,
          child: SafeArea(
            bottom: false,
            child: Padding(
              padding: const EdgeInsets.fromLTRB(12, 8, 12, 4),
              child: SegmentedButton<_Section>(
                // Fills the width, so the three choices are equal and easy to hit.
                expandedInsets: EdgeInsets.zero,
                showSelectedIcon: false,

                // Labels only: three with icons do not fit a narrow phone.
                segments: const [
                  ButtonSegment(value: _Section.apps, label: Text('Apps')),
                  ButtonSegment(value: _Section.windows, label: Text('Windows')),
                  ButtonSegment(value: _Section.files, label: Text('Files')),
                ],
                selected: {_section},
                onSelectionChanged: (selection) => setState(() {
                  _section = selection.first;
                  _opened.add(_section);
                }),
              ),
            ),
          ),
        ),
        Expanded(
          // The switch above already sits below the status bar, so the screens' own app bars
          // must not leave room for it a second time.
          child: MediaQuery.removePadding(
            context: context,
            removeTop: true,
            child: IndexedStack(
              index: _section.index,
              children: [
                const AppsScreen(),
                if (_opened.contains(_Section.windows))
                  const WindowsScreen()
                else
                  const SizedBox.shrink(),
                if (_opened.contains(_Section.files))
                  FilesScreen(active: _section == _Section.files)
                else
                  const SizedBox.shrink(),
              ],
            ),
          ),
        ),
      ],
    );
  }
}
