part of 'launcher_event.dart';

// Settings → Multiplayer and Diagnostics → Network. The bloc dispatches them
// in `launcher_multiplayer_settings_actions.dart` and
// `launcher_multiplayer_network_actions.dart`. Long work runs beside the
// event lane and reports back as an answer event carrying the generation it
// started in; an answer from an older generation is stale and is dropped.

/// An event of Settings → Multiplayer.
sealed class MultiplayerSettingsEvent extends MultiplayerEvent {
  const MultiplayerSettingsEvent();
}

/// Reads the preferences again, and with [checkGame] checks whether the game
/// runs: when the player presses Try again or Check again. At start the
/// first snapshot of the launcher state brings the check instead.
final class MultiplayerPreferencesRefreshRequested
    extends MultiplayerSettingsEvent {
  const MultiplayerPreferencesRefreshRequested({this.checkGame = true});

  final bool checkGame;
}

/// The player changed preferences in Settings → Multiplayer.
final class MultiplayerPreferencesEdited extends MultiplayerSettingsEvent {
  const MultiplayerPreferencesEdited(this.change);

  final MultiplayerPreferencesChange change;
}

/// Whether the game runs may have changed: the install changed, a launch
/// started or its game exited, Settings opened, or the launcher's window
/// came back to the front.
final class MultiplayerGameCheckRequested extends MultiplayerSettingsEvent {
  const MultiplayerGameCheckRequested();
}

/// A read of the preferences finished.
final class MultiplayerPreferencesLoaded extends MultiplayerSettingsEvent {
  const MultiplayerPreferencesLoaded(this.generation, this.preferences);

  final int generation;
  final MultiplayerPreferences preferences;
}

/// What became of a change.
sealed class MultiplayerChangeOutcome {
  const MultiplayerChangeOutcome();
}

/// The change was saved; [preferences] hold it.
final class MultiplayerChangeSaved extends MultiplayerChangeOutcome {
  const MultiplayerChangeSaved(this.preferences);

  final MultiplayerPreferences preferences;
}

/// The change was not made, because the game runs from the install, or the
/// launcher could not tell whether it does.
final class MultiplayerChangeLocked extends MultiplayerChangeOutcome {
  const MultiplayerChangeLocked(this.state);

  final GameRunState state;
}

/// The change was not saved. [message] says why in plain words.
final class MultiplayerChangeFailed extends MultiplayerChangeOutcome {
  const MultiplayerChangeFailed(this.message, {this.unreadable = false});

  final String message;

  /// The preferences could not be read just now.
  final bool unreadable;
}

/// A change finished. [gameCheck] is the newest check of whether the game
/// runs when the change asked: its own, for a change the game could also
/// make.
final class MultiplayerPreferencesChanged extends MultiplayerSettingsEvent {
  const MultiplayerPreferencesChanged(
    this.generation,
    this.change,
    this.outcome, {
    this.gameCheck = 0,
  });

  final int generation;
  final MultiplayerPreferencesChange change;
  final MultiplayerChangeOutcome outcome;
  final int gameCheck;
}

/// A check of whether the game runs from [installPath] finished.
final class MultiplayerGameChecked extends MultiplayerSettingsEvent {
  const MultiplayerGameChecked(this.generation, this.installPath, this.state);

  final int generation;
  final String installPath;
  final GameRunState state;
}

/// A round with the game finished. [token] is the one it ran with.
final class MultiplayerPreferencesRoundFinished
    extends MultiplayerSettingsEvent {
  const MultiplayerPreferencesRoundFinished(
    this.generation,
    this.token,
    this.round,
  );

  final int generation;
  final CancellationToken token;
  final MultiplayerPreferencesRound round;
}

/// An event of Diagnostics → Network.
sealed class MultiplayerNetworkEvent extends MultiplayerEvent {
  const MultiplayerNetworkEvent();
}

/// Reports on the service, LAN discovery and, with [checkPorts], the ports.
/// Port checks open ports, so they run only once the player has read what
/// they do.
final class MultiplayerNetworkCheckRequested extends MultiplayerNetworkEvent {
  const MultiplayerNetworkCheckRequested({this.checkPorts = false});

  final bool checkPorts;
}

/// A network check finished: its [report], or null when it failed.
final class MultiplayerNetworkChecked extends MultiplayerNetworkEvent {
  const MultiplayerNetworkChecked(this.generation, this.report);

  final int generation;
  final MultiplayerNetworkReport? report;
}

/// The player asked LAN discovery to listen, or to stop. Listening opens a
/// port, so it starts only once the player has read what it does.
final class MultiplayerLanListeningRequested extends MultiplayerNetworkEvent {
  const MultiplayerLanListeningRequested(this.listen);

  final bool listen;
}

/// LAN discovery changed.
final class MultiplayerLanSnapshotChanged extends MultiplayerNetworkEvent {
  const MultiplayerLanSnapshotChanged(this.snapshot);

  final LanBrowseSnapshot snapshot;
}

/// The player read the network-check explanation and chose to go on.
final class MultiplayerNetworkNoticeRead extends MultiplayerNetworkEvent {
  const MultiplayerNetworkNoticeRead();
}

/// The player left Diagnostics: a check still running is dropped, and LAN
/// discovery this page started stops.
final class MultiplayerNetworkLeft extends MultiplayerNetworkEvent {
  const MultiplayerNetworkLeft();
}
