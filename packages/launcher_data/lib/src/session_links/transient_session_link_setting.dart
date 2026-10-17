import 'dart:async';

import 'package:launcher_domain/launcher_domain.dart';

/// The link-handling choice, held only while the launcher runs and on at
/// every start, the default the plan gives `MultiplayerSettings`.
///
/// It stands in for the multiplayer settings store, which persists
/// `MultiplayerSettings.linkHandlingEnabled`. Once that store is in place it
/// backs [SessionLinkSetting] instead, and a choice survives a restart.
final class TransientSessionLinkSetting implements SessionLinkSetting {
  TransientSessionLinkSetting({bool enabled = true}) : _enabled = enabled;

  bool _enabled;
  final _changes = StreamController<bool>.broadcast();

  @override
  Future<bool> read() async => _enabled;

  @override
  Stream<bool> get changes => _changes.stream;

  @override
  Future<void> write(bool enabled) async {
    if (enabled == _enabled) return;
    _enabled = enabled;
    if (!_changes.isClosed) _changes.add(enabled);
  }

  Future<void> close() => _changes.close();
}
