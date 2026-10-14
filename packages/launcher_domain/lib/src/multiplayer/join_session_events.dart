part of '../multiplayer.dart';

/// What the player asked to join.
sealed class JoinTarget {
  const JoinTarget();

  /// The server the target names before anything is resolved: a server or
  /// direct link's server id, or a listing's. Null for an invite.
  String? get pinnedServerId;
}

/// A `topiaforge://join/…` link. A direct link holds an address.
final class JoinLinkTarget extends JoinTarget {
  const JoinLinkTarget(this.link);

  final SessionLink link;

  @override
  String? get pinnedServerId => link.serverId;
}

/// A row the player picked in the server browser.
final class JoinListingTarget extends JoinTarget {
  JoinListingTarget({required this.serverId, required this.source}) {
    if (!WireText.isHex(serverId, 32)) {
      throw ArgumentError.value(serverId, 'serverId', 'Not a server id.');
    }
  }

  final String serverId;
  final ServerListingSource source;

  @override
  String get pinnedServerId => serverId;
}

/// Something that happened while joining. [JoinSession.reduce] applies it.
sealed class JoinEvent {
  const JoinEvent();
}

/// Resolving found the server the target names.
final class JoinTargetResolved extends JoinEvent {
  JoinTargetResolved(this.serverId) {
    if (!WireText.isHex(serverId, 32)) {
      throw ArgumentError.value(serverId, 'serverId', 'Not a server id.');
    }
  }

  final String serverId;
}

/// The server's descriptor was fetched and verified.
final class JoinDescriptorVerified extends JoinEvent {
  const JoinDescriptorVerified(this.descriptor);

  final VerifiedServerDescriptor descriptor;
}

/// Checking produced a plan for the verified descriptor.
final class JoinPlanReady extends JoinEvent {
  const JoinPlanReady(this.plan);

  final JoinPlan plan;
}

/// The player accepted the consent sheet.
final class JoinConsentGiven extends JoinEvent {
  const JoinConsentGiven({
    required this.privacy,
    this.hostServedAcknowledged = false,
  });

  /// The privacy the player chose for this session.
  final MultiplayerAddressPrivacy privacy;

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

/// The current step failed. Cancellation is [JoinCancelRequested], and the
/// failures that carry detail have their own constructors.
final class JoinFailed extends JoinEvent {
  JoinFailed(this.code, {this.detail = ''})
    : rollbackComplete = null,
      gameOwnership = null {
    if (code == JoinFailureCode.cancelled ||
        code == JoinFailureCode.installFailed ||
        code == JoinFailureCode.gameRunning) {
      throw ArgumentError.value(
        code,
        'code',
        'Use JoinCancelRequested, JoinFailed.installFailed or '
            'JoinFailed.gameRunning.',
      );
    }
  }

  /// The install failed; [rollbackComplete] says whether the player's
  /// packages are as they were.
  const JoinFailed.installFailed({
    required bool this.rollbackComplete,
    this.detail = '',
  }) : code = JoinFailureCode.installFailed,
       gameOwnership = null;

  /// A game is already running.
  const JoinFailed.gameRunning({
    required JoinGameOwnership ownership,
    this.detail = '',
  }) : code = JoinFailureCode.gameRunning,
       rollbackComplete = null,
       gameOwnership = ownership;

  final JoinFailureCode code;
  final String detail;
  final bool? rollbackComplete;
  final JoinGameOwnership? gameOwnership;
}
