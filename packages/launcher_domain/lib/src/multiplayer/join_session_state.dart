part of '../multiplayer.dart';

/// Bytes downloaded so far while joining.
final class JoinTransferProgress {
  const JoinTransferProgress({
    required this.bytesDone,
    required this.bytesTotal,
  });

  final int bytesDone;
  final int bytesTotal;

  double get fraction => bytesTotal == 0 ? 0 : bytesDone / bytesTotal;
}

/// Packages prepared so far while installing.
final class JoinInstallProgress {
  const JoinInstallProgress({
    required this.packagesDone,
    required this.packagesTotal,
  });

  final int packagesDone;
  final int packagesTotal;

  double get fraction => packagesTotal == 0 ? 0 : packagesDone / packagesTotal;
}

const _keep = Object();

/// Where a join stands. Only [JoinSession] creates and advances it.
final class JoinSessionState {
  const JoinSessionState._({
    required this.stage,
    required this.target,
    required this.privacy,
    this.serverId,
    this.descriptor,
    this.plan,
    this.consentKey,
    this.progress,
    this.installProgress,
    this.replans = 0,
    this.cancelRequested = false,
    this.outcome,
  });

  final JoinStage stage;
  final JoinTarget target;

  /// The privacy the player chose for this session before it started. It
  /// governs every contact the join makes: [descriptorRequest] and
  /// [downloadRequest] carry it to the data layer as [JoinContactRules].
  final MultiplayerAddressPrivacy privacy;

  /// The server: named by the target, found by resolving, trusted by the
  /// player after an identity change, or, for a typed address, learned from
  /// its first descriptor.
  final String? serverId;

  /// The verified descriptor, from checking onwards.
  final VerifiedServerDescriptor? descriptor;

  /// The plan, from awaiting consent onwards.
  final JoinPlan? plan;

  /// The [JoinPlan.consentKey] the player consented to.
  final String? consentKey;

  /// Download progress while downloading.
  final JoinTransferProgress? progress;

  /// Install progress while installing, once the install has counted the
  /// packages it installs.
  final JoinInstallProgress? installProgress;

  /// Automatic re-plans so far; at most one.
  final int replans;

  /// Whether the player asked to stop during a step that finishes before it
  /// can stop: installing, preparing the profile, or launching.
  final bool cancelRequested;

  /// How the join ended, once [stage] is [JoinStage.ended].
  final JoinOutcome? outcome;

  bool get isEnded => stage == JoinStage.ended;

  /// Whether the join may contact the host itself rather than only the
  /// control plane: always with direct paths allowed, and under relay-only
  /// only when the host is on the local network. That holds for every
  /// direct link, typed address or LAN listing that got past [JoinSession.start]
  /// and the [JoinContactRules]; the host of an invite, server link or
  /// Internet listing could be anywhere.
  bool get mayContactHost =>
      privacy == MultiplayerAddressPrivacy.direct ||
      !target.resolvesThroughService;

  /// What to fetch, and whom the fetch may contact, while fetching the
  /// descriptor; null in every other stage.
  JoinDescriptorRequest? get descriptorRequest =>
      stage == JoinStage.fetchingDescriptor
      ? JoinDescriptorRequest._(
          target: target,
          privacy: privacy,
          expectedServerId: serverId,
        )
      : null;

  /// What to download, and whom the downloads may contact, while
  /// downloading; null in every other stage.
  JoinDownloadRequest? get downloadRequest => stage == JoinStage.downloading
      ? JoinDownloadRequest._(
          target: target,
          privacy: privacy,
          serverId: serverId!,
          registryDownloads: plan!.installActions,
          hostDownloads: plan!.hostServedPackages,
        )
      : null;

  JoinSessionState _copy({
    JoinStage? stage,
    Object? serverId = _keep,
    Object? descriptor = _keep,
    Object? plan = _keep,
    Object? consentKey = _keep,
    Object? progress = _keep,
    Object? installProgress = _keep,
    int? replans,
    bool? cancelRequested,
    Object? outcome = _keep,
  }) => JoinSessionState._(
    stage: stage ?? this.stage,
    target: target,
    privacy: privacy,
    serverId: identical(serverId, _keep) ? this.serverId : serverId as String?,
    descriptor: identical(descriptor, _keep)
        ? this.descriptor
        : descriptor as VerifiedServerDescriptor?,
    plan: identical(plan, _keep) ? this.plan : plan as JoinPlan?,
    consentKey: identical(consentKey, _keep)
        ? this.consentKey
        : consentKey as String?,
    progress: identical(progress, _keep)
        ? this.progress
        : progress as JoinTransferProgress?,
    installProgress: identical(installProgress, _keep)
        ? this.installProgress
        : installProgress as JoinInstallProgress?,
    replans: replans ?? this.replans,
    cancelRequested: cancelRequested ?? this.cancelRequested,
    outcome: identical(outcome, _keep) ? this.outcome : outcome as JoinOutcome?,
  );

  JoinSessionState _ended(JoinOutcome outcome) => _copy(
    stage: JoinStage.ended,
    progress: null,
    installProgress: null,
    cancelRequested: false,
    outcome: outcome,
  );

  JoinSessionState _failed(
    JoinFailureCode code, {
    String detail = '',
    List<JoinPlanBlocker> blockers = const [],
    bool? rollbackComplete,
    JoinGameOwnership? gameOwnership,
    String? servedServerId,
  }) => _ended(
    JoinOutcome.failed(
      JoinFailure._(
        code,
        stage,
        detail: detail,
        blockers: blockers,
        rollbackComplete: rollbackComplete,
        gameOwnership: gameOwnership,
        servedServerId: servedServerId,
      ),
    ),
  );
}
