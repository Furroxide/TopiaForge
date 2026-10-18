part of '../multiplayer.dart';

/// Predicts the admission the host will grant, from trusted manifests only.
/// Builds are compared in their version form, so `2545` and `0.0.2545` name
/// the same build.
MultiplayerAdmissionReport _predictAdmission(
  ServerDescriptorV1 descriptor,
  List<_Decision> decisions,
  JoinEnvironment environment,
) {
  MultiplayerAdmissionMod admitted(_Decision decision) =>
      MultiplayerAdmissionMod(
        manifest: decision.package.manifest!,
        packageSha256: decision.package.package.sha256,
      );

  final peerRange = environment.protocolPeerVersionRange;
  return const MultiplayerAdmissionPlanner().evaluate(
    server: MultiplayerAdmissionProfile(
      peerId: 'host',
      gameBuild: _buildVersion(descriptor.gameBuild),
      topiaForgeProtocolVersion: descriptor.protocolVersion,
      mods: [
        for (final decision in decisions)
          if (decision.onServer) admitted(decision),
      ],
    ),
    client: MultiplayerAdmissionProfile(
      peerId: 'player',
      gameBuild: _buildVersion(environment.gameBuild),
      topiaForgeProtocolVersion: environment.protocolVersion,
      topiaForgePeerVersionRange: peerRange,
      mods: [
        for (final decision in decisions)
          if (decision.onClient) admitted(decision),
      ],
    ),
    serverRole: MultiplayerAdmissionServerRole.listenHost,
  );
}

/// Turns admission mismatches into blockers. A package that already has a
/// blocker of its own is not reported twice. Both profiles hold the same
/// trusted manifests, so a package's own problem is worded without a side
/// and reported once ([JoinPlan] drops the duplicate).
List<JoinPlanBlocker> _admissionBlockers(
  MultiplayerAdmissionReport admission,
  ServerDescriptorV1 descriptor,
  JoinEnvironment environment,
  Set<String> blockedIds,
) {
  final hostRuntime = SemanticVersion.tryParse(descriptor.runtimeVersion);
  final localRuntime = SemanticVersion.tryParse(environment.runtimeVersion);
  final hostIsNewer =
      hostRuntime != null &&
      localRuntime != null &&
      hostRuntime.compareTo(localRuntime) > 0;
  return [
    for (final mismatch in admission.mismatches)
      if (mismatch.modId.isEmpty || !blockedIds.contains(mismatch.modId))
        switch (mismatch.code) {
          MultiplayerAdmissionMismatchCode.gameBuildMismatch => JoinPlanBlocker(
            JoinBlockerCode.gameBuildMismatch,
            'The server runs Robotopia build ${descriptor.gameBuild}; this '
            'install has build ${environment.gameBuild}.',
          ),
          MultiplayerAdmissionMismatchCode.topiaForgeProtocolMismatch =>
            JoinPlanBlocker(
              hostIsNewer
                  ? JoinBlockerCode.runtimeOutdated
                  : JoinBlockerCode.admissionBlocked,
              hostIsNewer
                  ? 'The server runs TopiaForge ${descriptor.runtimeVersion}; '
                        'update TopiaForge from ${environment.runtimeVersion} '
                        'to join.'
                  : 'The server runs TopiaForge ${descriptor.runtimeVersion}, '
                        'whose multiplayer protocol this install cannot use.',
            ),
          MultiplayerAdmissionMismatchCode.standaloneOnlyMod => JoinPlanBlocker(
            JoinBlockerCode.standaloneOnServer,
            'The server runs a mod that refuses multiplayer sessions.',
            modId: mismatch.modId,
          ),
          MultiplayerAdmissionMismatchCode.invalidProfile
              when mismatch.modId.isNotEmpty =>
            JoinPlanBlocker(
              JoinBlockerCode.admissionBlocked,
              mismatch.message.replaceFirst(_profileSide, 'The '),
              modId: mismatch.modId,
            ),
          _ => JoinPlanBlocker(
            JoinBlockerCode.admissionBlocked,
            mismatch.message,
            modId: mismatch.modId,
          ),
        },
  ];
}

/// Why a package cannot run here, or null when it can.
({JoinBlockerCode code, String message})? _runtimeIncompatibility(
  ModManifest manifest,
  JoinEnvironment environment,
) {
  final issues = const DependencyPlanner().runtimeCompatibilityIssues(
    manifest,
    gameVersion: _buildVersion(environment.gameBuild),
    loaderVersion: environment.runtimeVersion,
    sdkVersion: environment.sdkVersion,
    platform: environment.platform,
    architecture: environment.architecture,
  );
  if (issues.isEmpty) return null;
  bool needsNewer(VersionRange range, String version) =>
      !range.isAny && !range.allows(version) && !range.isAboveMaximum(version);
  final outdated =
      needsNewer(manifest.loaderVersionRange, environment.runtimeVersion) ||
      needsNewer(manifest.sdkVersionRange, environment.sdkVersion);
  return (
    code: outdated
        ? JoinBlockerCode.runtimeOutdated
        : JoinBlockerCode.contentUnavailable,
    message: issues.map((issue) => issue.message).join(' '),
  );
}

/// Why a package without a trusted manifest blocks the join. The role the
/// host claims only changes the wording: a claim of optional cannot be
/// checked without the manifest, so it does not let the package be left out.
String _unavailableMessage(
  JoinPackageResolution resolution,
  JoinEnvironment environment,
  ServerDescriptorV1 descriptor,
  DescriptorPackageRole claimed,
) {
  final source = resolution.sourceName.isEmpty
      ? 'a source'
      : resolution.sourceName;
  final reason = switch (resolution.availability) {
    JoinPackageAvailability.inDisabledSource =>
      'Only the disabled source $source offers it. Enable it to join.',
    JoinPackageAvailability.installedAsOtherArchive =>
      'You have this version installed, but as a different build than the '
          'server runs, and joining never replaces your copy. Uninstall it '
          'in Mods, then join again.',
    JoinPackageAvailability.installedForAnotherServer =>
      'Another server you joined runs a different build of this version, '
          'and joining never replaces it. Remove that server\'s content in '
          'Settings, then join again.',
    JoinPackageAvailability.hashMismatch =>
      'Your sources offer this version as a different archive than the '
          'server runs.',
    JoinPackageAvailability.versionUnavailable =>
      'Your sources do not offer the version the server runs.',
    JoinPackageAvailability.hostOffered when !environment.developerMode =>
      'Only the server offers it. Host-served packages need developer mode.',
    JoinPackageAvailability.hostOffered when !descriptor.hostServesPackages =>
      'The server does not serve its packages.',
    _ => 'None of your enabled sources offer it.',
  };
  return claimed == DescriptorPackageRole.sessionOptional
      ? '$reason The server calls it optional, but only its manifest could '
            'show that.'
      : reason;
}

/// Notes where the host's claimed role disagrees with the trusted manifest.
String _roleNote(
  DescriptorPackageRole claimed,
  ModMultiplayerEffectiveMode? mode,
  ModManifest manifest,
) {
  final DescriptorPackageRole? actual = switch (mode) {
    ModMultiplayerEffectiveMode.implicitShared =>
      DescriptorPackageRole.implicit,
    ModMultiplayerEffectiveMode.session =>
      manifest.multiplayer?.presence == ModMultiplayerPresence.optional
          ? DescriptorPackageRole.sessionOptional
          : DescriptorPackageRole.sessionRequired,
    _ => null,
  };
  if (actual == claimed) return '';
  return actual == null
      ? 'The server lists it as ${claimed.wireName}, but its manifest does '
            'not share it with players.'
      : 'The server lists it as ${claimed.wireName}; its manifest makes it '
            '${actual.wireName}.';
}

final _profileSide = RegExp('^The (server|client) ');

/// A build id such as `2545` in the `0.0.<build>` version form manifests
/// use; anything else is returned as it is.
String _buildVersion(String build) =>
    RobotopiaGameVersion.tryFromBuildId(build) ?? build;
