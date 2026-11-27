part of 'launcher_multiplayer_state.dart';

// Settings → Multiplayer and Diagnostics → Network. The bloc's handlers are
// in `launcher_multiplayer_settings_actions.dart` and
// `launcher_multiplayer_network_actions.dart`.

/// How far Settings → Multiplayer has read the player's preferences.
enum MultiplayerPreferencesPhase {
  /// The first read has not finished.
  loading,

  /// They read, possibly after recovering from damage.
  ready,

  /// They could not be read just now. Nothing shows in their place, nothing
  /// is changed, and Try again reads them once more.
  unreadable,
}

/// Why the preferences the game can change are read-only, if they are.
enum MultiplayerPreferencesLock {
  /// Whether the game runs is being checked.
  checking,

  /// They can be changed: no game runs from the install, or there is none.
  none,

  /// A game runs from the install. It records the changes the player makes
  /// in game, and nothing orders those against a change made here, so they
  /// can be changed once it is closed.
  gameRunning,

  /// The launcher could not tell whether the game runs, so it treats it as
  /// running.
  gameMaybeRunning,
}

/// What Settings → Multiplayer shows.
final class MultiplayerSettingsView {
  const MultiplayerSettingsView({
    this.phase = MultiplayerPreferencesPhase.loading,
    this.preferences,
    this.lock = MultiplayerPreferencesLock.checking,
    this.saving = false,
    this.syncing = false,
    this.problem,
  });

  final MultiplayerPreferencesPhase phase;

  /// The player's preferences once they read; null before, and while they
  /// cannot be read, so stand-ins never show.
  final MultiplayerPreferences? preferences;
  final MultiplayerPreferencesLock lock;

  /// A change is being saved.
  final bool saving;

  /// A round with the game is running: what changed in game is being taken
  /// in, and the game's copy brought up to date.
  final bool syncing;

  /// The last change or round that failed, in plain words, or null.
  final String? problem;

  /// Whether the preferences the game can change can be changed now: the
  /// display name, privacy, voice and the hosting defaults.
  bool get editable =>
      phase == MultiplayerPreferencesPhase.ready &&
      lock == MultiplayerPreferencesLock.none &&
      !saving &&
      !syncing;

  /// Whether link handling can be changed now. The game never changes it, so
  /// a running game leaves it editable.
  bool get linksEditable =>
      phase == MultiplayerPreferencesPhase.ready && !saving;

  MultiplayerSettingsView copyWith({
    MultiplayerPreferencesPhase? phase,
    MultiplayerPreferences? preferences,
    bool clearPreferences = false,
    MultiplayerPreferencesLock? lock,
    bool? saving,
    bool? syncing,
    String? problem,
    bool clearProblem = false,
  }) => MultiplayerSettingsView(
    phase: phase ?? this.phase,
    preferences: clearPreferences ? null : preferences ?? this.preferences,
    lock: lock ?? this.lock,
    saving: saving ?? this.saving,
    syncing: syncing ?? this.syncing,
    problem: clearProblem ? null : problem ?? this.problem,
  );
}

/// What Diagnostics → Network shows.
final class MultiplayerNetworkView {
  const MultiplayerNetworkView({
    this.report,
    this.checking = false,
    this.checkingPorts = false,
    this.lan,
    this.listening = false,
    this.noticeRead = false,
    this.problem,
  });

  /// The newest report, or null before the first check.
  final MultiplayerNetworkReport? report;

  /// A check is running.
  final bool checking;

  /// The check running binds the ports.
  final bool checkingPorts;

  /// LAN discovery as it stands now, newer than the report's.
  final LanBrowseSnapshot? lan;

  /// Whether this page asked LAN discovery to listen, so leaving it stops
  /// listening.
  final bool listening;

  /// Whether the player read the network-check explanation in this session.
  /// It counts even when the preferences could not keep that.
  final bool noticeRead;

  /// Why the last check did not finish, in plain words, or null.
  final String? problem;

  MultiplayerNetworkView copyWith({
    MultiplayerNetworkReport? report,
    bool? checking,
    bool? checkingPorts,
    LanBrowseSnapshot? lan,
    bool? listening,
    bool? noticeRead,
    String? problem,
    bool clearProblem = false,
  }) => MultiplayerNetworkView(
    report: report ?? this.report,
    checking: checking ?? this.checking,
    checkingPorts: checkingPorts ?? this.checkingPorts,
    lan: lan ?? this.lan,
    listening: listening ?? this.listening,
    noticeRead: noticeRead ?? this.noticeRead,
    problem: clearProblem ? null : problem ?? this.problem,
  );
}
