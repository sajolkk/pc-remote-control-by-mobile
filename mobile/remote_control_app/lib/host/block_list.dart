import 'package:flutter/foundation.dart';

/// Where the owner's block list is kept between runs.
abstract class BlockListStore {
  Future<Set<String>> load();
  Future<void> save(Set<String> packages);
}

class InMemoryBlockListStore implements BlockListStore {
  Set<String> packages = {};

  @override
  Future<Set<String>> load() async => {...packages};

  @override
  Future<void> save(Set<String> packages) async => this.packages = {...packages};
}

/// The owner's block list, and whether the app in front right now is on it.
///
/// Two inputs, one answer. The list is the owner's and changes whenever they toggle an app, in or
/// out of a session; the foreground package is reported by the accessibility service as the
/// phone's windows change. [isBlocked] is true exactly while a blocked app is in front, and
/// everything that gates on it (the picture, every input, screenshots) asks this one object, so
/// there is no second copy of the rule to drift.
class BlockListGate extends ChangeNotifier {
  BlockListGate({Set<String>? blocked, String? foregroundPackage})
      : _blocked = {...?blocked},
        _foreground = foregroundPackage;

  final Set<String> _blocked;
  String? _foreground;

  Set<String> get blocked => Set.unmodifiable(_blocked);

  /// The package of the app in front, as last reported. Null until the first report.
  String? get foregroundPackage => _foreground;

  /// Whether viewers must see the placeholder and have their input dropped right now.
  bool get isBlocked {
    final package = _foreground;
    return package != null && _blocked.contains(package);
  }

  bool isPackageBlocked(String package) => _blocked.contains(package);

  /// Adds or removes one app. Takes effect at once, mid-session included.
  void setBlocked(String package, bool blocked) {
    final changed = blocked ? _blocked.add(package) : _blocked.remove(package);
    if (changed) notifyListeners();
  }

  /// Replaces the whole list, e.g. with what was persisted.
  void replaceAll(Set<String> packages) {
    if (setEquals(packages, _blocked)) return;
    _blocked
      ..clear()
      ..addAll(packages);
    notifyListeners();
  }

  /// Records which app is in front. Notifies only when the answer to [isBlocked] could change.
  void setForeground(String? package) {
    if (package == _foreground) return;

    final wasBlocked = isBlocked;
    _foreground = package;
    if (wasBlocked != isBlocked || package != null) notifyListeners();
  }
}
