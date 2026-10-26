/// What the launcher's multiplayer screens ask of the data layer: the
/// player's multiplayer preferences, keeping the game's copy of them in step,
/// and network diagnostics.
///
/// The launcher's bloc depends on this interface, so it holds domain types
/// only; `launcher_data` implements it over the multiplayer store, the game's
/// staging folder and its network clients. Nothing it returns names a
/// network address.
library;

import 'dart:convert';

import 'cancellation.dart';
import 'launch_resolution.dart';
import 'models.dart';
import 'multiplayer.dart';
import 'session_link_handling.dart';

/// How the stored multiplayer preferences read.
enum MultiplayerPreferencesHealth {
  /// They read cleanly, or none were saved yet.
  sound,

  /// Part of them was damaged: a copy was kept aside, and what was valid is
  /// in use.
  salvaged,

  /// Nothing in them could be used: a copy was kept aside, and the defaults
  /// are in use.
  reset,

  /// They could not be read just now, as when another program holds the
  /// file. Stand-in values come back meanwhile: nothing is written over the
  /// file, and nothing on this computer changes to match them.
  unreadable,
}

/// The player's multiplayer preferences as the launcher keeps them: the
/// settings, the hosting defaults, and the version of the network-check
/// explanation the player has read.
final class MultiplayerPreferences {
  MultiplayerPreferences({
    required this.settings,
    MultiplayerHostingDefaults? hosting,
    bool? hasHostingDefaults,
    this.networkNotice = 0,
    this.health = MultiplayerPreferencesHealth.sound,
  }) : hasHostingDefaults = hasHostingDefaults ?? (hosting != null),
       hosting =
           hosting ??
           MultiplayerHostingDefaults.forPlayer(settings.displayName) {
    if (networkNotice < 0) {
      throw ArgumentError.value(
        networkNotice,
        'networkNotice',
        'A notice version is not negative.',
      );
    }
  }

  /// The version of the explanation shown before the first LAN browse or
  /// port check: the firewall prompt, the ports, the off switch and what a
  /// port check means. Raising it after the explanation changes shows it to
  /// every player again.
  static const currentNetworkNotice = 1;

  final MultiplayerSettings settings;

  /// The defaults a session the launcher hosts starts from; the plan's own
  /// ([MultiplayerHostingDefaults.forPlayer]) until any are stored.
  final MultiplayerHostingDefaults hosting;

  /// Whether hosting defaults have been saved or read from the game.
  /// A port-only edit must not replace an unread game's other defaults.
  final bool hasHostingDefaults;

  /// The newest network-check explanation the player read; 0 when never.
  final int networkNotice;
  final MultiplayerPreferencesHealth health;

  /// Whether these are the player's own preferences rather than stand-ins.
  bool get readable => health != MultiplayerPreferencesHealth.unreadable;

  /// Whether the network-check explanation must show before the next LAN
  /// browse or port check.
  bool get needsNetworkNotice => networkNotice < currentNetworkNotice;
}

/// A change the player makes in Settings → Multiplayer. Each member that is
/// set changes one preference; the rest stay as they are.
///
/// Link handling is not here: it changes through [SessionLinkHandlingGateway],
/// so the system registration follows it.
final class MultiplayerPreferencesChange {
  const MultiplayerPreferencesChange({
    this.displayName,
    this.acknowledgeDisclosure = false,
    this.hostingPort,
    this.hostingDefaults,
    this.expected,
    this.upnp,
    this.lanDiscovery,
    this.voiceConsent,
    this.voice,
    this.acknowledgeNetworkNotice = false,
  });

  /// A new display name; [MultiplayerSettings.isValidDisplayName] decides.
  final String? displayName;

  /// The player read the address disclosure and pressed its button.
  final bool acknowledgeDisclosure;

  /// The port a hosted session uses; 0 picks any free one.
  final int? hostingPort;
  final MultiplayerHostingDefaults? hostingDefaults;

  /// Optional compare-and-set guard for a reviewed hosting confirmation.
  final MultiplayerPreferences? expected;
  final bool? upnp;
  final bool? lanDiscovery;

  /// True gives consent to the current voice wording and turns voice on;
  /// false withdraws consent, which turns voice off too.
  final bool? voiceConsent;

  /// Switches voice; switching it on needs consent.
  final bool? voice;

  /// The player read the network-check explanation.
  final bool acknowledgeNetworkNotice;

  /// Whether this changes a preference the game can change too: the display
  /// name, privacy, voice or the hosting defaults. Those stay read-only while
  /// a game that writes the preferences journal runs.
  bool get sharedWithGame =>
      displayName != null ||
      acknowledgeDisclosure ||
      hostingPort != null ||
      hostingDefaults != null ||
      upnp != null ||
      lanDiscovery != null ||
      voiceConsent != null ||
      voice != null;

  /// [current] with this change made. Throws [ArgumentError] or
  /// [FormatException] for a value the preferences refuse, such as a display
  /// name outside the rule or a port outside 0 to 65535.
  MultiplayerPreferences applyTo(MultiplayerPreferences current) {
    final reviewed = expected;
    if (reviewed != null &&
        (jsonEncode(reviewed.settings.toJson()) !=
                jsonEncode(current.settings.toJson()) ||
            jsonEncode(reviewed.hosting.toJson()) !=
                jsonEncode(current.hosting.toJson()))) {
      throw const FormatException(
        'The reviewed preferences changed. Review hosting again.',
      );
    }
    var settings = current.settings;
    final name = displayName;
    if (name != null) settings = settings.withDisplayName(name);
    if (acknowledgeDisclosure) {
      settings = settings.acknowledgePrivacyDisclosure();
    }
    if (upnp case final enabled?) settings = settings.withUpnp(enabled);
    if (lanDiscovery case final enabled?) {
      settings = settings.withLanDiscovery(enabled);
    }
    settings = switch (voiceConsent) {
      true => settings.giveVoiceConsent().withVoice(true),
      false => settings.withdrawVoiceConsent(),
      null => settings,
    };
    if (voice case final enabled?) settings = settings.withVoice(enabled);
    if (hostingPort != null &&
        !current.hasHostingDefaults &&
        hostingDefaults == null) {
      throw const FormatException(
        'Read the game hosting defaults before changing only the port.',
      );
    }
    final hosting = hostingDefaults ?? current.hosting;
    final port = hostingPort;
    return MultiplayerPreferences(
      settings: settings,
      hosting: port == null
          ? hosting
          : MultiplayerHostingDefaults(
              name: hosting.name,
              maxPlayers: hosting.maxPlayers,
              visibility: hosting.visibility,
              port: port,
              voice: hosting.voice,
            ),
      networkNotice:
          acknowledgeNetworkNotice &&
              current.networkNotice <
                  MultiplayerPreferences.currentNetworkNotice
          ? MultiplayerPreferences.currentNetworkNotice
          : current.networkNotice,
      health: current.health,
      hasHostingDefaults: current.hasHostingDefaults || hostingDefaults != null,
    );
  }
}

/// Whether Robotopia runs from an install, as far as the launcher can tell.
enum GameRunState {
  notRunning,
  running,

  /// The launcher could not tell, as when the system's process list could
  /// not be read.
  unknown,
}

/// What one round with the game did ([MultiplayerGateway.syncWithGame]).
sealed class MultiplayerPreferencesRound {
  const MultiplayerPreferencesRound(this.preferences);

  /// The preferences after the round.
  final MultiplayerPreferences preferences;
}

/// The round ran: the game's changes are in [preferences], and the copy the
/// game reads matches them.
final class MultiplayerPreferencesSynced extends MultiplayerPreferencesRound {
  const MultiplayerPreferencesSynced(
    super.preferences, {
    required this.changedInGame,
  });

  /// Whether the player had changed any preference in game.
  final bool changedInGame;
}

/// The round changed nothing: the stored preferences could not be read, or
/// its cancellation stopped it before anything was written.
final class MultiplayerPreferencesRoundSkipped
    extends MultiplayerPreferencesRound {
  const MultiplayerPreferencesRoundSkipped(super.preferences);
}

/// Why a round failed.
enum MultiplayerRoundProblem {
  /// The preferences could not be saved, so nothing was taken in and the
  /// game's copy was not written.
  preferencesNotSaved,

  /// The preferences were saved, but the game's copy could not be written;
  /// the next round writes it from them.
  gameCopyNotWritten,
}

/// The round failed, for [problem]. The screens say what that means in their
/// own words.
final class MultiplayerPreferencesRoundFailed
    extends MultiplayerPreferencesRound {
  const MultiplayerPreferencesRoundFailed(super.preferences, this.problem);

  final MultiplayerRoundProblem problem;
}

/// A change that was not made. [message] says what happened and what to do
/// next, in plain words; it names no file and no address.
final class MultiplayerPreferencesException implements Exception {
  const MultiplayerPreferencesException(
    this.message, {
    this.unreadable = false,
  });

  final String message;

  /// Whether the stored preferences could not be read just now, so nothing
  /// was written over them.
  final bool unreadable;

  @override
  String toString() => 'MultiplayerPreferencesException: $message';
}

/// What the launcher's multiplayer screens ask of the data layer.
abstract interface class MultiplayerGateway {
  /// Reads the preferences. Never throws: while they cannot be read,
  /// stand-ins come back with [MultiplayerPreferencesHealth.unreadable].
  Future<MultiplayerPreferences> loadPreferences();

  /// Makes [change] to the stored preferences, with no other change in
  /// between, and returns them. Turning LAN discovery off stops LAN browsing
  /// at once, even when the save then fails. Throws
  /// [MultiplayerPreferencesException] while they cannot be read, and when
  /// they cannot be written; nothing changes then.
  Future<MultiplayerPreferences> changePreferences(
    MultiplayerPreferencesChange change,
  );

  /// One round with the game at [install]: takes in what the player changed
  /// in game since the last round, and brings the copy of the preferences
  /// the game reads up to date, with no change to the preferences in
  /// between. Run it only while that game is not running. It changes nothing
  /// while the preferences cannot be read, and writes nothing once
  /// [cancellation] is cancelled. LAN discovery turned off in game stops LAN
  /// browsing once it is taken in. Never throws.
  Future<MultiplayerPreferencesRound> syncWithGame(
    GameInstall install, {
    CancellationToken? cancellation,
  });

  /// Whether Robotopia runs from [install]. Never throws.
  Future<GameRunState> gameRunState(GameInstall install);

  /// Whether this build names a multiplayer service.
  MultiplayerServiceStatus get serviceStatus;

  LanBrowseSnapshot get lanSnapshot;

  /// Every change to LAN browsing.
  Stream<LanBrowseSnapshot> get lanSnapshots;

  /// Starts LAN browsing unless the player turned it off. While the
  /// preferences cannot be read it stays off, as
  /// [LanDiscoveryProblem.settingsUnreadable]. Never throws.
  Future<LanBrowseSnapshot> startLanBrowsing();

  Future<LanBrowseSnapshot> stopLanBrowsing();

  /// Reports on the service, the ports and LAN discovery. Port checks bind
  /// sockets, so they run only with [checkPorts]. [gamePort] is the port the
  /// player's hosting defaults name, and the game's default port when null.
  /// A session hosted on port 0 takes any free port, which no check can speak
  /// for, so the game port is then not checked.
  Future<MultiplayerNetworkReport> checkNetwork({
    bool checkPorts = false,
    int? gamePort,
    bool developerMode = false,
    CancellationToken? cancellation,
  });

  /// Stops LAN browsing and lets go of everything the gateway holds.
  Future<void> dispose();
}
