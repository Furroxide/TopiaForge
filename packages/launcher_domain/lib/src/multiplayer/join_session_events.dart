part of '../multiplayer.dart';

/// Something that happened while joining. [JoinSession.reduce] applies it.
sealed class JoinEvent {
  const JoinEvent();
}

/// Resolving found the server the target names.
final class JoinTargetResolved extends JoinEvent {
  JoinTargetResolved(this.serverId) {
    if (!WireText.isHex(serverId, 32)) {
      throw ArgumentError('Not a server id.', 'serverId');
    }
  }

  final String serverId;
}

/// The server's descriptor was fetched and verified as
/// [JoinSessionState.descriptorRequest] asked. For a typed address it
/// names the server the join is with from then on.
final class JoinDescriptorVerified extends JoinEvent {
  const JoinDescriptorVerified(this.descriptor);

  final VerifiedServerDescriptor descriptor;
}

/// Checking produced a plan for the verified descriptor.
final class JoinPlanReady extends JoinEvent {
  const JoinPlanReady(this.plan);

  final JoinPlan plan;
}

/// The player accepted the consent sheet. The session's privacy was chosen
/// before the join started ([JoinSession.start]) and does not change here.
final class JoinConsentGiven extends JoinEvent {
  const JoinConsentGiven({this.hostServedAcknowledged = false});

  /// Whether the player ticked the developer-mode host-served box. A plan
  /// with host-served packages needs it.
  final bool hostServedAcknowledged;
}

final class JoinDownloadProgressed extends JoinEvent {
  JoinDownloadProgressed({required this.bytesDone, required this.bytesTotal}) {
    if (bytesTotal < 0 || bytesDone < 0 || bytesDone > bytesTotal) {
      throw ArgumentError('Progress is 0 to bytesTotal bytes.');
    }
  }

  final int bytesDone;
  final int bytesTotal;
}

final class JoinDownloadsCompleted extends JoinEvent {
  const JoinDownloadsCompleted();
}

final class JoinInstallCompleted extends JoinEvent {
  const JoinInstallCompleted();
}

final class JoinProfilePrepared extends JoinEvent {
  const JoinProfilePrepared();
}

/// The game started with the join request.
final class JoinGameStarted extends JoinEvent {
  const JoinGameStarted();
}

/// The game reached the server and the handshake completed.
final class JoinConnected extends JoinEvent {
  const JoinConnected();
}

/// The game applied the server's snapshot; the session is playable.
final class JoinSynchronized extends JoinEvent {
  const JoinSynchronized();
}

/// The session that began has ended.
final class JoinSessionEnded extends JoinEvent {
  const JoinSessionEnded(this.reason);

  final JoinSessionEndReason reason;
}

/// The player asked to stop joining.
final class JoinCancelRequested extends JoinEvent {
  const JoinCancelRequested();
}

/// The game process the join launched has exited. Before the session began
/// that ends the join as [JoinFailureCode.gameExited]; during the session it
/// ends the session as [JoinSessionEndReason.gameExited].
final class JoinGameExited extends JoinEvent {
  const JoinGameExited();
}

/// The current step failed. Cancellation is [JoinCancelRequested], a game
/// exit is [JoinGameExited], and the failures that carry detail have their
/// own constructors.
final class JoinFailed extends JoinEvent {
  JoinFailed(this.code, {this.detail = ''})
    : rollbackComplete = null,
      gameOwnership = null,
      servedServerId = null {
    if (code == JoinFailureCode.cancelled ||
        code == JoinFailureCode.gameExited ||
        code == JoinFailureCode.installFailed ||
        code == JoinFailureCode.gameRunning ||
        code == JoinFailureCode.identityChanged) {
      throw ArgumentError.value(
        code,
        'code',
        'Use JoinCancelRequested, JoinGameExited, JoinFailed.installFailed, '
            'JoinFailed.gameRunning or JoinFailed.identityChanged.',
      );
    }
  }

  /// The install failed; [rollbackComplete] says whether the player's
  /// packages are as they were.
  const JoinFailed.installFailed({
    required bool this.rollbackComplete,
    this.detail = '',
  }) : code = JoinFailureCode.installFailed,
       gameOwnership = null,
       servedServerId = null;

  /// A game is already running.
  const JoinFailed.gameRunning({
    required JoinGameOwnership ownership,
    this.detail = '',
  }) : code = JoinFailureCode.gameRunning,
       rollbackComplete = null,
       gameOwnership = ownership,
       servedServerId = null;

  /// A typed address served [servedServerId], another server than the one
  /// the known-hosts record or the join expected. Only the join of a
  /// [JoinAddressTarget] acts on it: every other target names its server.
  JoinFailed.identityChanged({
    required String this.servedServerId,
    this.detail = '',
  }) : code = JoinFailureCode.identityChanged,
       rollbackComplete = null,
       gameOwnership = null {
    if (!WireText.isHex(servedServerId, 32)) {
      throw ArgumentError('Not a server id.', 'servedServerId');
    }
  }

  final JoinFailureCode code;
  final String detail;
  final bool? rollbackComplete;
  final JoinGameOwnership? gameOwnership;
  final String? servedServerId;
}
