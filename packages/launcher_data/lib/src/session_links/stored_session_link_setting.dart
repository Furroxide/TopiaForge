import 'dart:async';

import 'package:launcher_domain/launcher_domain.dart';

import '../multiplayer/multiplayer_store.dart';

/// The link-handling choice as the multiplayer store keeps it,
/// `MultiplayerSettings.linkHandlingEnabled`, so a choice survives a restart
/// and the registration follows it wherever it is made.
///
/// [changes] carries every value the store reads or writes that differs from
/// the last one this setting reported, so a change saved from anywhere, and
/// settings that read again after they could not be read, reach the
/// registration. Settings that cannot be read never reach it: [read] throws
/// then, and the store reports no stand-ins, so the registration is left
/// exactly as it is.
final class StoredSessionLinkSetting implements SessionLinkSetting {
  StoredSessionLinkSetting(this._store) {
    _subscription = _store.settingsChanges.listen(
      (settings) => _report(settings.linkHandlingEnabled),
    );
  }

  final MultiplayerStore _store;
  final _changes = StreamController<bool>.broadcast();
  late final StreamSubscription<MultiplayerSettings> _subscription;

  /// The value last read, written or reported; null before any.
  bool? _known;

  /// Throws [MultiplayerStoreException] while the settings cannot be read,
  /// since they may hold the player's "off".
  @override
  Future<bool> read() async {
    final load = await _store.loadSettings();
    if (load.recovery == MultiplayerStoreRecovery.unreadable) {
      throw const MultiplayerStoreException(
        'The multiplayer settings could not be read just now, so the link '
        'registration was left as it is.',
        unreadable: true,
      );
    }
    final enabled = load.settings.linkHandlingEnabled;
    _known ??= enabled;
    return enabled;
  }

  @override
  Stream<bool> get changes => _changes.stream;

  /// Saves [enabled] with the other multiplayer settings; it then arrives on
  /// [changes]. Throws [MultiplayerStoreException] when it cannot be saved,
  /// and while the settings cannot be read.
  @override
  Future<void> write(bool enabled) async {
    await _store.updateSettings(
      (settings) => settings.withLinkHandling(enabled),
    );
  }

  void _report(bool enabled) {
    if (_known == enabled) return;
    _known = enabled;
    if (!_changes.isClosed) _changes.add(enabled);
  }

  /// Stops following the store.
  Future<void> close() async {
    await _subscription.cancel();
    await _changes.close();
  }
}
