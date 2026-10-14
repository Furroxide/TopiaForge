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

const _keep = Object();

/// Where a join stands. Only [JoinSession] creates and advances it.
final class JoinSessionState {
  const JoinSessionState._({
    required this.stage,
    required this.target,
    required this.privacy,
    required this.trustChangedIdentity,
    this.serverId,
    this.descriptor,
    this.plan,
    this.consentKey,
    this.progress,
    this.replans = 0,
    this.cancelRequested = false,
    this.outcome,
  });

  final JoinStage stage;
  final JoinTarget target;

  /// The privacy the player chose for this session before it started. It
  /// governs every contact, from fetching the descriptor onwards.
  final MultiplayerAddressPrivacy privacy;

  /// Whether the player chose to trust a typed address whose server changed.
  final bool trustChangedIdentity;

  /// The server: named by the target, found by resolving, or, for a typed
  /// address, learned from its first descriptor.
  final String? serverId;

  /// The verified descriptor, from checking onwards.
  final VerifiedServerDescriptor? descriptor;

  /// The plan, from awaiting consent onwards.
  final JoinPlan? plan;

  /// The [JoinPlan.consentKey] the player consented to.
  final String? consentKey;

  /// Download progress while downloading.
  final JoinTransferProgress? progress;

  /// Automatic re-plans so far; at most one.
  final int replans;

  /// Whether the player asked to stop during a step that finishes before it
  /// can stop: installing, preparing the profile, or launching.
  final bool cancelRequested;

  /// How the join ended, once [stage] is [JoinStage.ended].
  final JoinOutcome? outcome;

  bool get isEnded => stage == JoinStage.ended;

  /// What to fetch, and whom the fetch may contact, while fetching the
  /// descriptor; null in every other stage.
  JoinDescriptorRequest? get descriptorRequest =>
      stage == JoinStage.fetchingDescriptor
      ? JoinDescriptorRequest._(
          target: target,
          expectedServerId: serverId,
          privacy: privacy,
          trustChangedIdentity: trustChangedIdentity,
        )
      : null;

  JoinSessionState _copy({
    JoinStage? stage,
    Object? serverId = _keep,
    Object? descriptor = _keep,
    Object? plan = _keep,
    Object? consentKey = _keep,
    Object? progress = _keep,
    int? replans,
    bool? cancelRequested,
    Object? outcome = _keep,
  }) => JoinSessionState._(
    stage: stage ?? this.stage,
    target: target,
    privacy: privacy,
    trustChangedIdentity: trustChangedIdentity,
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
    replans: replans ?? this.replans,
    cancelRequested: cancelRequested ?? this.cancelRequested,
    outcome: identical(outcome, _keep) ? this.outcome : outcome as JoinOutcome?,
  );

  JoinSessionState _ended(JoinOutcome outcome) => _copy(
    stage: JoinStage.ended,
    progress: null,
    cancelRequested: false,
    outcome: outcome,
  );

  JoinSessionState _failed(
    JoinFailureCode code, {
    String detail = '',
    List<JoinPlanBlocker> blockers = const [],
    bool? rollbackComplete,
    JoinGameOwnership? gameOwnership,
  }) => _ended(
    JoinOutcome.failed(
      JoinFailure._(
        code,
        stage,
        detail: detail,
        blockers: blockers,
        rollbackComplete: rollbackComplete,
        gameOwnership: gameOwnership,
      ),
    ),
  );
}
