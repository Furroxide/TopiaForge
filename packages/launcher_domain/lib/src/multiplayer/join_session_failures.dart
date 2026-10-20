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

  /// Reaching the host would show it the player's address, and the player
  /// did not allow a direct path for this session. The launcher reports it
  /// before any contact; the game reports it while connecting, when it
  /// resolves the target again and the network has changed since.
  directNotAllowed,

  /// A typed address served a different server than it did before: its
  /// identity changed, or something is impersonating it.
  identityChanged,
  gameBuildMismatch,
  runtimeOutdated,

  /// Per mod: see the failure's blockers.
  contentUnavailable,
  admissionBlocked,
  standaloneOnServer,

  /// The player stopped the join.
  cancelled,
  downloadFailed,

  /// The plan needs packages only the host serves, and this launcher does not
  /// download packages from a host. Nothing was downloaded.
  hostDownloadUnsupported,

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

  /// The game process exited before the session began; see [JoinGameExited].
  gameExited,
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

/// Who owns a game process that blocks a launch or an install. An install
/// never runs beside a game: a running game writes its manager state when it
/// quits, over anything written while it ran.
enum JoinGameOwnership {
  /// The launcher started it, so it can restart it into the join. An install
  /// closes it when the consent covered that
  /// ([JoinConsentGiven.closeOwnedGameAcknowledged]); it blocks when it did
  /// not, or when closing it failed.
  owned,

  /// Something else started it; the player must close it.
  foreign,
}

/// Why a session that began has ended, mirroring the game's
/// `MultiplayerSessionEndReason`, plus [gameExited], which only the launcher
/// can see.
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

  /// The game process exited.
  gameExited,
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
    this.servedServerId,
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

  /// For [JoinFailureCode.identityChanged]: the server the typed address
  /// served instead. Trusting it means starting again with it as the
  /// `trustedServerId`, so only that server is accepted.
  final String? servedServerId;
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
