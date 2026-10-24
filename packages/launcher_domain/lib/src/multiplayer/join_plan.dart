part of '../multiplayer.dart';

/// What a join does with one of the host's packages.
enum JoinPackageDisposition {
  /// Installed at the exact archive; enabled as it is.
  useInstalled,

  /// Downloaded from an enabled source at the exact archive, then installed.
  download,

  /// Developer mode: downloaded from the host itself. Its manifest is not
  /// trusted until the downloaded archive is checked, and consent shows it
  /// separately.
  downloadFromHost,

  /// Not enabled: a package whose trusted manifest makes it optional and
  /// that is not installed at the exact archive, or one that runs only on
  /// the host. A package without a trusted manifest is never skipped.
  skip,

  /// Blocks the join; see the plan's blockers.
  blocked;

  bool get enables =>
      this == JoinPackageDisposition.useInstalled ||
      this == JoinPackageDisposition.download ||
      this == JoinPackageDisposition.downloadFromHost;
}

/// One of the host's packages with the decision the plan made for it.
final class JoinPlanPackage {
  const JoinPlanPackage({
    required this.package,
    required this.resolution,
    required this.disposition,
    required this.trustedMode,
    this.note = '',
  });

  /// The host's claim: id, version, SHA-256 and role.
  final DescriptorPackage package;
  final JoinPackageResolution resolution;
  final JoinPackageDisposition disposition;

  /// The multiplayer mode the trusted manifest declares; null without a
  /// trusted manifest or when the manifest's mode is invalid.
  final ModMultiplayerEffectiveMode? trustedMode;

  /// Why the package is skipped or blocked, or where the host's claimed role
  /// disagrees with the trusted manifest.
  final String note;

  /// The trusted manifest, when there is one.
  ModManifest? get manifest => resolution.manifest;
}

/// Why a join cannot go ahead, in the order the failure card reports them.
enum JoinBlockerCode {
  /// The server runs another Robotopia build.
  gameBuildMismatch,

  /// The installed TopiaForge runtime is older than the server or a package
  /// needs.
  runtimeOutdated,

  /// A host package refuses multiplayer sessions.
  standaloneOnServer,

  /// A required package has no trusted, compatible copy for this install,
  /// including one only the host has when the launcher may not take it from
  /// the host ([JoinSessionState.mayContactHost]).
  contentUnavailable,

  /// The admission planner refuses the session for another reason.
  admissionBlocked,
}

final class JoinPlanBlocker implements Comparable<JoinPlanBlocker> {
  const JoinPlanBlocker(this.code, this.message, {this.modId = ''});

  final JoinBlockerCode code;

  /// The package concerned; empty for session-wide blockers.
  final String modId;
  final String message;

  @override
  int compareTo(JoinPlanBlocker other) {
    final byCode = code.index.compareTo(other.code.index);
    if (byCode != 0) return byCode;
    final byMod = modId.compareTo(other.modId);
    return byMod != 0 ? byMod : message.compareTo(other.message);
  }

  @override
  bool operator ==(Object other) =>
      other is JoinPlanBlocker &&
      other.code == code &&
      other.modId == modId &&
      other.message == message;

  @override
  int get hashCode => Object.hash(code, modId, message);

  @override
  String toString() =>
      '${code.name}${modId.isEmpty ? '' : '($modId)'}: $message';
}

/// An admission plan for joining one verified server: which exact packages
/// to use, download or skip, what blocks the join, the capabilities the
/// player consents to, and the derived profile the game restarts into.
final class JoinPlan {
  JoinPlan({
    required this.server,
    required List<JoinPlanPackage> packages,
    required List<JoinPlanBlocker> blockers,
    required this.admission,
    required List<String> capabilities,
    required this.derivedProfile,
  }) : packages = List.unmodifiable(packages),
       blockers = List.unmodifiable(blockers.toSet().toList()..sort()),
       capabilities = List.unmodifiable(capabilities.toSet().toList()..sort());

  final VerifiedServerDescriptor server;
  final List<JoinPlanPackage> packages;

  /// Each once, sorted by code, then mod id. Empty when the join can go
  /// ahead.
  final List<JoinPlanBlocker> blockers;

  /// The admission the session is expected to grant, predicted from trusted
  /// manifests at the host's exact archives.
  final MultiplayerAdmissionReport admission;

  /// The sorted union of the capabilities the enabled packages' trusted
  /// manifests declare. Host-served packages add none until checked; see
  /// [hasUnverifiedPackages].
  final List<String> capabilities;

  /// The profile the game restarts into: every enabled package pinned,
  /// nothing inherited from the manager's global state.
  final LauncherProfile derivedProfile;

  bool get isJoinable => blockers.isEmpty;

  bool get needsDownloads => packages.any(
    (item) =>
        item.disposition == JoinPackageDisposition.download ||
        item.disposition == JoinPackageDisposition.downloadFromHost,
  );

  /// Whether developer-mode host-served packages are part of the plan, which
  /// consent must acknowledge separately.
  bool get hasUnverifiedPackages => packages.any(
    (item) => item.disposition == JoinPackageDisposition.downloadFromHost,
  );

  /// The registry downloads, each pinned to its exact archive and the
  /// source's manifest for it.
  List<PackageInstallAction> get installActions => [
    for (final item in packages)
      if (item.disposition == JoinPackageDisposition.download)
        PackageInstallAction(
          modId: item.package.id,
          name: item.manifest!.name,
          version: item.package.version,
          expectedManifest: item.manifest!,
          packageUrl: item.resolution.downloadUrl,
          packageSha256: item.package.sha256,
          sourceId: item.resolution.sourceId,
          sourceName: item.resolution.sourceName,
        ),
  ];

  /// The packages the host itself serves, in developer mode.
  List<DescriptorPackage> get hostServedPackages => [
    for (final item in packages)
      if (item.disposition == JoinPackageDisposition.downloadFromHost)
        item.package,
  ];

  /// Identifies what a player consents to: the server and its name, voice,
  /// each enabled package's exact archive and whether the host serves it, and
  /// the capabilities. A re-plan with the same key needs no second consent.
  /// It is compared only within the launcher, so plain JSON in a fixed member
  /// order is enough, and no manifest text can make it throw.
  String get consentKey => jsonEncode({
    'serverId': server.serverId,
    'name': server.descriptor.name,
    'voice': server.descriptor.voice,
    'capabilities': capabilities,
    'packages': [
      for (final item in packages)
        if (item.disposition.enables)
          [
            item.package.pin,
            item.disposition == JoinPackageDisposition.downloadFromHost,
          ],
    ],
  });
}
