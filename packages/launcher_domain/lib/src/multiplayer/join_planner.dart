part of '../multiplayer.dart';

/// Builds the admission plan for joining a verified server.
///
/// Every decision rests on trusted manifests: the player's own install
/// receipts and registry sources, looked up by the exact SHA-256 the
/// descriptor pins. The host's claimed roles never make a package required,
/// optional or safe; they only decide whether an unavailable package can be
/// left out, and a wrong claim then fails the join at admission, not before.
///
/// - Implicitly shared and required session packages must be used at the
///   exact archive: installed, or downloaded from an enabled source.
/// - Optional session packages are used only when already installed at the
///   exact archive.
/// - Client-local and server-only packages run on the host alone.
/// - A package only the host offers blocks the join outside developer mode.
///   In developer mode, when the descriptor says the host serves packages,
///   it is downloaded from the host and flagged for separate consent.
/// - Admission is predicted with the shared [MultiplayerAdmissionPlanner],
///   with the host as a listen server.
final class JoinPlanner {
  const JoinPlanner();

  /// The prefix of every derived server profile id.
  static const derivedProfilePrefix = 'mp-';

  /// The derived profile id for [serverId]: `mp-` followed by the first 16
  /// hexadecimal digits of the SHA-256 of the server id's ASCII text.
  static String derivedProfileId(String serverId) {
    if (!WireText.isHex(serverId, 32)) {
      throw ArgumentError.value(serverId, 'serverId', 'Not a server id.');
    }
    return '$derivedProfilePrefix'
        '${PureSha256.hex(ascii.encode(serverId)).substring(0, 16)}';
  }

  /// The derived profile name: `Server · <name>`.
  static String derivedProfileName(String serverName) => 'Server · $serverName';

  /// Plans joining [server]. [resolutions] maps package SHA-256 values to
  /// what the launcher found for them; a package without an entry counts as
  /// not in any source.
  JoinPlan plan({
    required VerifiedServerDescriptor server,
    required Map<String, JoinPackageResolution> resolutions,
    required JoinEnvironment environment,
  }) {
    final descriptor = server.descriptor;
    final blockers = <JoinPlanBlocker>[];
    final decisions = [
      for (final package in descriptor.packages)
        _decide(
          package,
          resolutions[package.sha256] ??
              JoinPackageResolution.unavailable(
                JoinPackageAvailability.notInSources,
              ),
          descriptor,
          environment,
          blockers,
        ),
    ];
    final admission = _predictAdmission(descriptor, decisions, environment);
    final blockedIds = {
      for (final blocker in blockers)
        if (blocker.modId.isNotEmpty) blocker.modId.toLowerCase(),
    };
    blockers.addAll(
      _admissionBlockers(admission, descriptor, environment, blockedIds),
    );
    final packages = [for (final decision in decisions) decision.package];
    final enabled = packages.where((item) => item.disposition.enables);
    return JoinPlan(
      server: server,
      packages: packages,
      blockers: blockers,
      admission: admission,
      capabilities: [
        for (final item in enabled) ...?item.manifest?.capabilities,
      ],
      derivedProfile: LauncherProfile(
        id: derivedProfileId(server.serverId),
        name: derivedProfileName(descriptor.name),
        inheritManagerModState: false,
        enabledMods: {for (final item in enabled) item.package.id},
        selectedVersions: {
          for (final item in enabled) item.package.id: item.package.version,
        },
      ),
    );
  }

  _Decision _decide(
    DescriptorPackage package,
    JoinPackageResolution resolution,
    ServerDescriptorV1 descriptor,
    JoinEnvironment environment,
    List<JoinPlanBlocker> blockers,
  ) {
    JoinPlanPackage decided(
      JoinPackageDisposition disposition, {
      ModMultiplayerEffectiveMode? mode,
      String note = '',
    }) => JoinPlanPackage(
      package: package,
      resolution: resolution,
      disposition: disposition,
      trustedMode: mode,
      note: note,
    );

    final manifest = resolution.manifest;
    if (manifest == null) {
      if (resolution.availability == JoinPackageAvailability.hostOffered &&
          environment.developerMode &&
          descriptor.hostServesPackages &&
          package.role != DescriptorPackageRole.sessionOptional) {
        return _Decision(
          decided(
            JoinPackageDisposition.downloadFromHost,
            note:
                'Served by the host in developer mode; checked after '
                'download.',
          ),
        );
      }
      final message = _unavailableMessage(resolution, environment, descriptor);
      if (package.role == DescriptorPackageRole.sessionOptional) {
        return _Decision(
          decided(JoinPackageDisposition.skip, note: 'Optional. $message'),
        );
      }
      blockers.add(
        JoinPlanBlocker(
          JoinBlockerCode.contentUnavailable,
          message,
          modId: package.id,
        ),
      );
      return _Decision(decided(JoinPackageDisposition.blocked, note: message));
    }

    if (manifest.id.toLowerCase() != package.id.toLowerCase() ||
        manifest.version != package.version) {
      final message =
          'The archive with this SHA-256 is ${manifest.id}@${manifest.version} '
          'in your sources, not ${package.id}@${package.version}.';
      blockers.add(
        JoinPlanBlocker(
          JoinBlockerCode.contentUnavailable,
          message,
          modId: package.id,
        ),
      );
      return _Decision(decided(JoinPackageDisposition.blocked, note: message));
    }

    final mode = manifest.effectiveMultiplayerMode;
    final roleNote = _roleNote(package.role, mode, manifest);
    switch (mode) {
      case null:
        return _Decision.admitted(
          decided(
            JoinPackageDisposition.blocked,
            note: 'Its manifest is not valid for multiplayer.',
          ),
          onClient: false,
        );
      case ModMultiplayerEffectiveMode.standalone:
        return _Decision.admitted(
          decided(
            JoinPackageDisposition.blocked,
            mode: mode,
            note: 'It refuses multiplayer sessions.',
          ),
          onClient: false,
        );
      case ModMultiplayerEffectiveMode.clientLocal ||
          ModMultiplayerEffectiveMode.serverOnly:
        return _Decision.admitted(
          decided(
            JoinPackageDisposition.skip,
            mode: mode,
            note: _joined('It runs only on the host.', roleNote),
          ),
          onClient: false,
        );
      case ModMultiplayerEffectiveMode.implicitShared ||
          ModMultiplayerEffectiveMode.session:
        break;
    }

    final optional =
        mode == ModMultiplayerEffectiveMode.session &&
        manifest.multiplayer?.presence == ModMultiplayerPresence.optional;
    final installed =
        resolution.availability == JoinPackageAvailability.installed;
    if (optional && !installed) {
      return _Decision.admitted(
        decided(
          JoinPackageDisposition.skip,
          mode: mode,
          note: _joined(
            'Optional, and used only when installed at the exact archive.',
            roleNote,
          ),
        ),
        onClient: false,
      );
    }
    final incompatibility = _runtimeIncompatibility(manifest, environment);
    if (incompatibility != null) {
      if (optional) {
        return _Decision.admitted(
          decided(
            JoinPackageDisposition.skip,
            mode: mode,
            note: _joined('Optional. ${incompatibility.message}', roleNote),
          ),
          onClient: false,
        );
      }
      blockers.add(
        JoinPlanBlocker(
          incompatibility.code,
          incompatibility.message,
          modId: package.id,
        ),
      );
      return _Decision.admitted(
        decided(
          JoinPackageDisposition.blocked,
          mode: mode,
          note: _joined(incompatibility.message, roleNote),
        ),
        onClient: true,
      );
    }
    return _Decision.admitted(
      decided(
        installed
            ? JoinPackageDisposition.useInstalled
            : JoinPackageDisposition.download,
        mode: mode,
        note: roleNote,
      ),
      onClient: true,
    );
  }
}

/// A package decision and where its trusted manifest takes part in the
/// admission prediction.
final class _Decision {
  const _Decision(this.package) : onServer = false, onClient = false;

  const _Decision.admitted(this.package, {required this.onClient})
    : onServer = true;

  final JoinPlanPackage package;

  /// Whether the host's profile includes the trusted manifest.
  final bool onServer;

  /// Whether the joining player's profile includes it too.
  final bool onClient;
}

String _joined(String first, String second) =>
    second.isEmpty ? first : '$first $second';
