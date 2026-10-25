part of 'launcher_bloc.dart';

/// The bloc's bookkeeping for multiplayer work that runs beside its event
/// lane. Each kind of work counts its generations: an answer that is not
/// from the newest one of its kind is stale and dropped, and a cancelled
/// token stops work that has not written anything yet.
final class _MultiplayerWork {
  /// The newest read, change or round of the preferences.
  int preferences = 0;

  /// The [preferences] generation of the newest read. Until anything shows,
  /// its answer shows even after a round has started since: that round may
  /// fail or be cancelled, and show nothing.
  int read = 0;

  /// The newest check of whether the game runs.
  int game = 0;

  /// The newest network check, or the latest time Diagnostics was left.
  int network = 0;

  /// The round with the game that runs now, if any.
  CancellationToken? round;

  /// The network check that runs now, if any.
  CancellationToken? check;

  /// Whether the next check that finds the game closed runs a round: at
  /// start, after the install changed, after the game ran and after a change
  /// the game reads.
  bool roundDue = true;

  /// The link choice saved last, and the newest [preferences] generation
  /// then: a read, change or round of that generation or older may have read
  /// the store before the choice was written.
  bool? linkChoice;
  int linkChosenAt = -1;

  /// The join flow's own bookkeeping.
  final join = _JoinWork();

  void cancelAll() {
    round?.cancel();
    check?.cancel();
    join.stop();
    join.handoffs?.cancel().ignore();
    join.handoffs = null;
  }
}

/// Whether a game check's answer leaves the preferences the game can change
/// editable.
MultiplayerPreferencesLock _lockFor(GameRunState state) => switch (state) {
  GameRunState.notRunning => MultiplayerPreferencesLock.none,
  GameRunState.running => MultiplayerPreferencesLock.gameRunning,
  GameRunState.unknown => MultiplayerPreferencesLock.gameMaybeRunning,
};

String _lockedStatus(MultiplayerPreferencesLock lock) => switch (lock) {
  MultiplayerPreferencesLock.gameMaybeRunning =>
    'TopiaForge could not tell whether Robotopia is running, so nothing was '
        'changed. Close the game, then try again.',
  MultiplayerPreferencesLock.checking =>
    'TopiaForge is still checking whether Robotopia is running, so nothing '
        'was changed. Try again in a moment.',
  _ =>
    'Robotopia is running, so nothing was changed. These settings can be '
        'changed once the game is closed.',
};

MultiplayerPreferences _withLinkHandling(
  MultiplayerPreferences preferences,
  bool enabled,
) => MultiplayerPreferences(
  settings: preferences.settings.withLinkHandling(enabled),
  hosting: preferences.hosting,
  networkNotice: preferences.networkNotice,
  health: preferences.health,
);

MultiplayerPreferences _unreadablePreferences() => MultiplayerPreferences(
  settings: MultiplayerSettings(),
  health: MultiplayerPreferencesHealth.unreadable,
);

const _unreadableStatus =
    'Your multiplayer settings could not be read just now, so nothing was '
    'changed.';
const _notSaved =
    'Your multiplayer settings could not be saved. Check that TopiaForge can '
    'write its data folder, then try again.';
const _gameCopy =
    "Robotopia's copy of your multiplayer settings could not be updated, so "
    "the game still has the earlier ones. Check that TopiaForge can write to "
    "the game's folder; it tries again the next time it checks.";
