part of '../multiplayer.dart';

/// The steps of joining a server, in order. [ended] is terminal.
enum JoinStage {
  resolving,
  fetchingDescriptor,
  checking,
  awaitingConsent,
  downloading,
  installing,
  preparingProfile,
  launching,
  connecting,
  synchronizing,
  inSession,
  ended,
}

/// Why a join stopped before the session began.
enum JoinFailureCode {
  /// The link or target is not one the launcher can join.
  invalidLink,
  inviteExpired,
  serviceUnavailable,
  rateLimited,
  offline,

  /// The host's descriptor endpoint could not be reached.
  unreachable,

  /// The descriptor was malformed, for another server, or badly signed.
  descriptorInvalid,
  gameBuildMismatch,
  runtimeOutdated,

  /// Per mod: see the failure's blockers.
  contentUnavailable,
  admissionBlocked,
  standaloneOnServer,

  /// The player stopped the join.
  cancelled,
  downloadFailed,

  /// A downloaded archive's SHA-256 differs from the pinned one.
  integrityFailed,

  /// A downloaded archive's manifest differs from the trusted one.
  manifestMismatch,

  /// See [JoinFailure.rollbackComplete].
  installFailed,
  profileSaveFailed,
  repairNeeded,

  /// See [JoinFailure.gameOwnership].
  gameRunning,
  launchBlocked,
  startFailed,
  timeout,
  natTraversalFailed,
  relayUnavailable,
  serverFull,

  /// The server password was missing or wrong.
  passwordRejected,
  kicked,
  protocolMismatch,

  /// The server's admission changed after planning, and the one automatic
  /// re-plan did not settle it.
  admissionChanged,
  snapshotFailed,
  disconnected,
}

/// Who owns a game process that blocks a launch.
enum JoinGameOwnership {
  /// The launcher started it, so it can restart it into the join.
  owned,

  /// Something else started it; the player must close it.
  foreign,
}

/// Why a session that began has ended, mirroring the game's
/// `MultiplayerSessionEndReason`.
enum JoinSessionEndReason {
  left,
  hostEnded,
  kicked,
  banned,
  timedOut,
  replaced,
  admissionRejected,
  protocolMismatch,
  transportFailed,
}

/// A join that stopped at [stage] for [code]. [detail] is for people and
/// never names an address.
final class JoinFailure {
  JoinFailure._(
    this.code,
    this.stage, {
    this.detail = '',
    List<JoinPlanBlocker> blockers = const [],
    this.rollbackComplete,
    this.gameOwnership,
  }) : blockers = List.unmodifiable(blockers);

  final JoinFailureCode code;

  /// The step that failed, for the failure card's step rail.
  final JoinStage stage;
  final String detail;

  /// The plan's blockers, for failures found while checking.
  final List<JoinPlanBlocker> blockers;

  /// For [JoinFailureCode.installFailed]: whether the install rolled back
  /// completely, leaving the player's packages as they were.
  final bool? rollbackComplete;

  /// For [JoinFailureCode.gameRunning]: who owns the running game.
  final JoinGameOwnership? gameOwnership;
}

/// How a join ended: a failure before the session began, or the end of the
/// session itself.
final class JoinOutcome {
  const JoinOutcome.failed(JoinFailure this.failure) : sessionEnd = null;

  const JoinOutcome.sessionEnded(JoinSessionEndReason this.sessionEnd)
    : failure = null;

  final JoinFailure? failure;
  final JoinSessionEndReason? sessionEnd;

  bool get wasCancelled => failure?.code == JoinFailureCode.cancelled;
}
