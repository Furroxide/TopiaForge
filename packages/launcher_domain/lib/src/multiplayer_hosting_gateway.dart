import 'cancellation.dart';
import 'launch_resolution.dart';
import 'models.dart';
import 'multiplayer.dart';
import 'multiplayer_gateway.dart';

/// Choices for one hosted session. Passwords are entered only in the game;
/// allowing direct play is never saved as a preference.
final class HostingChoices {
  const HostingChoices({
    required this.defaults,
    this.passwordRequired = false,
    this.allowDirect = false,
    this.servePackages = false,
  });
  final MultiplayerHostingDefaults defaults;
  final bool passwordRequired;
  final bool allowDirect;
  final bool servePackages;

  bool get needsService =>
      defaults.visibility == ServerVisibility.public ||
      defaults.visibility == ServerVisibility.invite;
}

sealed class HostingPreparation {
  const HostingPreparation();
}

/// An immutable review of exactly the profile, preferences and choices the
/// player will approve. A launch checks these again before starting anything.
final class HostingPlan extends HostingPreparation {
  const HostingPlan({
    required this.profile,
    required this.preferences,
    required this.choices,
    required this.packageDigest,
    required this.joinability,
  });
  final LauncherProfile profile;
  final MultiplayerPreferences preferences;
  final HostingChoices choices;
  final String packageDigest;
  final String joinability;
}

final class HostingRefused extends HostingPreparation {
  const HostingRefused(this.message, {this.canAllowDirect = false});
  final String message;
  final bool canAllowDirect;
}

/// Optional capability; the existing repository interface and V4 launches
/// remain unchanged. Cancellation is checked immediately before process
/// creation, after all asynchronous staging, under the install's launch lease.
abstract interface class MultiplayerHostingHost {
  Future<LaunchResult> launchSession(
    GameInstall install,
    LauncherProfile profile,
    LaunchSessionRequest session, {
    LaunchSelection? selectionOverride,
    bool restart = false,
    CancellationToken? cancellation,
    String? expectedPackageDigest,
  });
}

abstract interface class MultiplayerHostingGateway
    implements MultiplayerGateway {
  Future<HostingPreparation> prepareHosting(
    GameInstall install,
    LauncherProfile profile,
    HostingChoices choices, {
    required bool developerMode,
    required CancellationToken cancellation,
  });

  /// Starts only the reviewed plan, with the player's address-disclosure
  /// acknowledgement. Never persists a password or a direct-play choice.
  Future<LaunchResult> startHosting(
    GameInstall install,
    HostingPlan plan, {
    required String intentId,
    required bool acknowledgeDisclosure,
    required bool developerMode,
    required CancellationToken cancellation,
  });
}
