import 'dart:convert';

import 'package:launcher_domain/launcher_domain.dart';

import 'control_plane.dart';

/// Hosting orchestration shared by the facade and its tests. Every refusal
/// uses fixed text: exceptions, service addresses and process paths stay here.
final class HostingService {
  HostingService({
    required this.launcher,
    required this.multiplayer,
    required this.serviceStatus,
    this.serviceOrigin,
  });
  final LauncherRepository launcher;
  final MultiplayerGateway multiplayer;
  final Future<MultiplayerServiceCheck> Function(CancellationToken)
  serviceStatus;
  final String? serviceOrigin;

  Future<HostingPreparation> prepare(
    GameInstall install,
    LauncherProfile profile,
    HostingChoices choices, {
    required bool developerMode,
    required CancellationToken cancellation,
  }) async {
    try {
      cancellation.throwIfCancelled();
      if (launcher is! MultiplayerHostingHost) {
        return const HostingRefused('Hosting is unavailable in this launcher.');
      }
      if (await multiplayer.gameRunState(install) != GameRunState.notRunning) {
        return const HostingRefused(
          'Close Robotopia before hosting. Its process must be stopped and verifiable.',
        );
      }
      final round = await multiplayer.syncWithGame(
        install,
        cancellation: cancellation,
      );
      cancellation.throwIfCancelled();
      if (round is! MultiplayerPreferencesSynced ||
          !round.preferences.readable) {
        return const HostingRefused(
          'Multiplayer preferences could not be synchronized. Close programs using the data folder and retry.',
        );
      }
      final preferences = round.preferences;
      if (choices.servePackages && !developerMode) {
        return const HostingRefused(
          'Serving packages requires developer mode. Turn that choice off or enable developer mode in Settings.',
        );
      }
      if (choices.defaults.port == LanBeacon.discoveryPort) {
        return const HostingRefused(
          'That port is reserved for LAN discovery. Choose a different hosting port.',
        );
      }
      if (profile.launchSettings.safeMode) {
        return const HostingRefused(
          'Safe Mode cannot host. Turn it off in Setup, then try again.',
        );
      }
      if ((choices.defaults.visibility == ServerVisibility.lan ||
              choices.defaults.visibility == ServerVisibility.public) &&
          !preferences.settings.lanDiscoveryEnabled) {
        return const HostingRefused(
          'LAN discovery is off. Enable it in Multiplayer settings, or choose invite visibility.',
        );
      }
      if (choices.defaults.voice && !preferences.settings.voiceActive) {
        return const HostingRefused(
          'Voice needs your microphone consent in Multiplayer settings. Keep voice off here or enable it there first.',
        );
      }
      if (choices.needsService) {
        if (serviceOrigin == null) {
          return const HostingRefused(
            'This build has no Internet multiplayer service. Choose LAN visibility.',
          );
        }
        final status = await serviceStatus(cancellation);
        cancellation.throwIfCancelled();
        final features = status.features;
        if (features == null ||
            !features.hosting ||
            !features.signaling ||
            !features.invites) {
          return const HostingRefused(
            'Internet hosting or invites are unavailable. Try again later or choose LAN visibility.',
          );
        }
        if (!features.relayAvailable && !choices.allowDirect) {
          return const HostingRefused(
            'The relay is unavailable. Keep Hide my IP on with LAN visibility, or allow a direct connection for this session.',
            canAllowDirect: true,
          );
        }
      }
      final preview = await launcher.previewLaunch(install, profile);
      cancellation.throwIfCancelled();
      if (!preview.canLaunch ||
          preview.profileId != profile.id ||
          preview.profileRevision != profile.revision) {
        return const HostingRefused(
          'This profile cannot launch as reviewed. Refresh it and check Setup for repairs.',
        );
      }
      return HostingPlan(
        profile: profile,
        preferences: preferences,
        choices: choices,
        packageDigest: preview.packageDigest,
        joinability: switch (choices.defaults.visibility) {
          ServerVisibility.public =>
            'Listed publicly and on the local network.',
          ServerVisibility.invite =>
            'Only people with your invite can find this session.',
          ServerVisibility.lan =>
            'Listed on the local network only; no Internet invite.',
        },
      );
    } on OperationCancelledException {
      return const HostingRefused(
        'Hosting preparation cancelled. Nothing was launched.',
      );
    } on Object {
      return const HostingRefused(
        'Hosting could not be prepared. Check Multiplayer settings and Setup, then try again.',
      );
    }
  }

  Future<LaunchResult> start(
    GameInstall install,
    HostingPlan plan, {
    required String intentId,
    required bool acknowledgeDisclosure,
    required bool developerMode,
    required CancellationToken cancellation,
  }) async {
    LaunchResult refused(String message) =>
        LaunchResult(started: false, message: message);
    try {
      final checked = await prepare(
        install,
        plan.profile,
        plan.choices,
        developerMode: developerMode,
        cancellation: cancellation,
      );
      if (checked is HostingRefused) return refused(checked.message);
      final current = checked as HostingPlan;
      if (current.packageDigest != plan.packageDigest ||
          _fingerprint(current.preferences) != _fingerprint(plan.preferences)) {
        return refused(
          'The profile or multiplayer preferences changed. Review hosting again; nothing was launched.',
        );
      }
      if (current.preferences.settings.needsPrivacyDisclosure &&
          !acknowledgeDisclosure) {
        return refused(
          'Read the address disclosure before hosting. Nothing was launched.',
        );
      }
      cancellation.throwIfCancelled();
      if (await multiplayer.gameRunState(install) != GameRunState.notRunning) {
        return refused(
          'Robotopia started while hosting was prepared. Close it and try again.',
        );
      }
      cancellation.throwIfCancelled();
      final saved = await multiplayer.changePreferences(
        MultiplayerPreferencesChange(
          acknowledgeDisclosure: acknowledgeDisclosure,
          hostingDefaults: plan.choices.defaults,
          expected: plan.preferences,
        ),
      );
      cancellation.throwIfCancelled();
      final round = await multiplayer.syncWithGame(
        install,
        cancellation: cancellation,
      );
      cancellation.throwIfCancelled();
      if (round is! MultiplayerPreferencesSynced ||
          _fingerprint(round.preferences) != _fingerprint(saved)) {
        return refused(
          'The reviewed preferences could not be synchronized. Review hosting again; nothing was launched.',
        );
      }
      final choices = plan.choices;
      final request = LaunchSessionRequest.host(
        intentId: intentId,
        settings: saved.settings,
        privacy: saved.settings.privacyForSession(
          allowDirect: choices.allowDirect,
        ),
        host: LaunchHostOptions(
          name: choices.defaults.name,
          maxPlayers: choices.defaults.maxPlayers,
          visibility: choices.defaults.visibility,
          voice: choices.defaults.voice && saved.settings.voiceActive,
          passwordRequired: choices.passwordRequired,
          servePackages: choices.servePackages,
        ),
        port: choices.defaults.port,
        serviceOrigin: serviceOrigin,
        developerMode: developerMode,
      );
      cancellation.throwIfCancelled();
      return await (launcher as MultiplayerHostingHost).launchSession(
        install,
        plan.profile,
        request,
        cancellation: cancellation,
        expectedPackageDigest: plan.packageDigest,
      );
    } on OperationCancelledException {
      return refused(
        'Hosting cancelled before process creation. Nothing was launched.',
      );
    } on Object {
      return refused(
        'Hosting could not start. Check Multiplayer settings and Setup, then try again.',
      );
    }
  }

  String _fingerprint(MultiplayerPreferences preferences) =>
      jsonEncode([preferences.settings.toJson(), preferences.hosting.toJson()]);
}
