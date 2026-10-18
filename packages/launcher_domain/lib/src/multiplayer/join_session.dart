part of '../multiplayer.dart';

/// The join flow as a pure reducer: resolving, fetching the descriptor,
/// checking, consent, downloading, installing, preparing the profile,
/// launching, connecting, synchronizing, the session, and the end.
///
/// - The player chooses the session's privacy before the join starts
///   ([start]), so it governs the very first contact and every one after
///   ([JoinContactRules]). Targets the control plane resolves start
///   resolving; a direct link, a typed address and a LAN listing start
///   fetching the descriptor. Under relay-only, a target whose host is an IP
///   literal beyond the local network, or a name other than `localhost`,
///   `.localhost` and `.local` names, ends at once, before anything is
///   contacted or looked up. A plan that would download from a host whose
///   location is unknown ends the join while checking, and a download that
///   finds the host off the local network ends it while downloading.
/// - Only a typed address can change identity, since only it learns its
///   server. Once that server is known, because the join learned it or the
///   player trusted it, another server answering ends the join as an
///   identity change. Every other target names its server, so another one
///   is an invalid descriptor and a reported identity change is ignored.
/// - Each step advances only on its own completion event; any event a step
///   cannot use, including a failure it cannot produce, leaves the state as
///   it is. Stale events from abandoned work are therefore harmless.
/// - Cancelling stops at once wherever the work can be abandoned. While
///   installing, preparing the profile or launching it is held until the
///   step finishes, and the join then ends as cancelled instead of moving on;
///   if the step fails instead, its failure is what the join reports. An
///   install that stops for the request before it changes anything the game
///   reads reports [JoinInstallUndone], which ends the join as cancelled too.
/// - Nothing installs beside a running game: a running game writes its
///   manager state when it quits, over what the install wrote. A game the
///   launcher started is closed first, as the join's restart would close it;
///   one it did not start ends the install as [JoinFailureCode.gameRunning],
///   as it ends a launch.
/// - Packages only the host serves are not downloaded by this launcher: a
///   plan that needs one ends while downloading as
///   [JoinFailureCode.hostDownloadUnsupported], before anything is fetched.
/// - A game that exits ends the join from launching onwards, and ends the
///   session once it began.
/// - When admission changes while connecting, the join fetches the
///   descriptor and plans once more on its own. Consent is asked again only
///   if the plan's [JoinPlan.consentKey] changed; a second change ends the
///   join.
abstract final class JoinSession {
  /// The failures each step can produce through [JoinFailed]. Cancellation
  /// and game exits have their own events: see [cancellableStages],
  /// [deferredCancelStages] and [gameExitStages]. Only the join of a
  /// [JoinAddressTarget] can produce [JoinFailureCode.identityChanged].
  static const failuresByStage = <JoinStage, Set<JoinFailureCode>>{
    JoinStage.resolving: {
      JoinFailureCode.invalidLink,
      JoinFailureCode.inviteExpired,
      JoinFailureCode.serviceUnavailable,
      JoinFailureCode.rateLimited,
      JoinFailureCode.offline,
    },
    JoinStage.fetchingDescriptor: {
      JoinFailureCode.unreachable,
      JoinFailureCode.offline,
      JoinFailureCode.descriptorInvalid,
      JoinFailureCode.serviceUnavailable,
      JoinFailureCode.rateLimited,
      JoinFailureCode.directNotAllowed,
      JoinFailureCode.identityChanged,
    },
    JoinStage.checking: {
      JoinFailureCode.gameBuildMismatch,
      JoinFailureCode.runtimeOutdated,
      JoinFailureCode.contentUnavailable,
      JoinFailureCode.admissionBlocked,
      JoinFailureCode.standaloneOnServer,
      JoinFailureCode.directNotAllowed,
    },
    JoinStage.awaitingConsent: {},
    JoinStage.downloading: {
      JoinFailureCode.downloadFailed,
      JoinFailureCode.hostDownloadUnsupported,
      JoinFailureCode.integrityFailed,
      JoinFailureCode.manifestMismatch,
      JoinFailureCode.directNotAllowed,
    },
    JoinStage.installing: {
      JoinFailureCode.installFailed,
      JoinFailureCode.gameRunning,
    },
    JoinStage.preparingProfile: {JoinFailureCode.profileSaveFailed},
    JoinStage.launching: {
      JoinFailureCode.repairNeeded,
      JoinFailureCode.gameRunning,
      JoinFailureCode.launchBlocked,
      JoinFailureCode.startFailed,
    },
    JoinStage.connecting: {
      JoinFailureCode.timeout,
      JoinFailureCode.natTraversalFailed,
      JoinFailureCode.relayUnavailable,
      JoinFailureCode.serverFull,
      JoinFailureCode.passwordRejected,
      JoinFailureCode.kicked,
      JoinFailureCode.protocolMismatch,
      JoinFailureCode.admissionChanged,
      JoinFailureCode.disconnected,
      JoinFailureCode.directNotAllowed,
    },
    JoinStage.synchronizing: {
      JoinFailureCode.snapshotFailed,
      JoinFailureCode.disconnected,
    },
    JoinStage.inSession: {},
    JoinStage.ended: {},
  };

  /// Steps a cancel request ends at once.
  static const cancellableStages = {
    JoinStage.resolving,
    JoinStage.fetchingDescriptor,
    JoinStage.checking,
    JoinStage.awaitingConsent,
    JoinStage.downloading,
    JoinStage.connecting,
    JoinStage.synchronizing,
  };

  /// Steps that finish before a cancel request takes effect.
  static const deferredCancelStages = {
    JoinStage.installing,
    JoinStage.preparingProfile,
    JoinStage.launching,
  };

  /// Steps a [JoinGameExited] ends as [JoinFailureCode.gameExited].
  static const gameExitStages = {
    JoinStage.launching,
    JoinStage.connecting,
    JoinStage.synchronizing,
  };

  /// The automatic re-plans a join may make when admission changes.
  static const maximumReplans = 1;

  /// A new join of [target] under the [privacy] the player chose for this
  /// session. A direct [SessionPrivacy] only comes from
  /// `MultiplayerSettings.privacyForSession`, after the disclosure.
  ///
  /// [trustedServerId] is for a [JoinAddressTarget] only: the server the
  /// player chose to trust after an identity change
  /// ([JoinFailure.servedServerId]). The join then accepts that server and
  /// no other.
  static JoinSessionState start(
    JoinTarget target, {
    required SessionPrivacy privacy,
    String? trustedServerId,
  }) {
    if (trustedServerId != null &&
        (target is! JoinAddressTarget ||
            !WireText.isHex(trustedServerId, 32))) {
      throw ArgumentError(
        'Only a typed address trusts a server, named by its server id.',
        'trustedServerId',
      );
    }
    final resolving = target.resolvesThroughService;
    final state = JoinSessionState._(
      stage: resolving ? JoinStage.resolving : JoinStage.fetchingDescriptor,
      target: target,
      privacy: privacy.addressPrivacy,
      serverId: resolving ? null : trustedServerId ?? target.pinnedServerId,
    );
    final host = target.directHost;
    if (privacy.allowsDirect || host == null) return state;
    final kind = NetworkHost.pathKindOf(host);
    if (kind == NetworkPathKind.direct) {
      return state._failed(
        JoinFailureCode.directNotAllowed,
        detail:
            'Joining this host directly would show it your address. Allow a '
            'direct path for this session to continue.',
      );
    }
    if (kind == null && !NetworkHost.isLocalName(host)) {
      return state._failed(
        JoinFailureCode.directNotAllowed,
        detail:
            'Looking up this name would reach servers its author chose. Allow '
            'a direct path for this session to use it.',
      );
    }
    return state;
  }

  /// The state after [event].
  static JoinSessionState reduce(JoinSessionState state, JoinEvent event) {
    if (state.isEnded) return state;
    final stage = state.stage;
    return switch (event) {
      JoinCancelRequested() => _cancel(state),
      JoinFailed() => _fail(state, event),
      JoinGameExited() => _gameExited(state),
      JoinTargetResolved() when stage == JoinStage.resolving => _resolved(
        state,
        event.serverId,
      ),
      JoinDescriptorVerified() when stage == JoinStage.fetchingDescriptor =>
        _verified(state, event.descriptor),
      JoinPlanReady() when stage == JoinStage.checking => _planned(
        state,
        event.plan,
      ),
      JoinConsentGiven() when stage == JoinStage.awaitingConsent => _consented(
        state,
        event,
      ),
      JoinDownloadProgressed() when stage == JoinStage.downloading =>
        state._copy(
          progress: JoinTransferProgress(
            bytesDone: event.bytesDone,
            bytesTotal: event.bytesTotal,
          ),
        ),
      JoinDownloadsCompleted() when stage == JoinStage.downloading =>
        state._copy(stage: JoinStage.installing, progress: null),
      JoinInstallCompleted() when stage == JoinStage.installing => _finished(
        state,
        JoinStage.preparingProfile,
      ),
      JoinInstallUndone() when stage == JoinStage.installing => _undone(state),
      JoinProfilePrepared() when stage == JoinStage.preparingProfile =>
        _finished(state, JoinStage.launching),
      JoinGameStarted() when stage == JoinStage.launching => _finished(
        state,
        JoinStage.connecting,
      ),
      JoinConnected() when stage == JoinStage.connecting => state._copy(
        stage: JoinStage.synchronizing,
      ),
      JoinSynchronized() when stage == JoinStage.synchronizing => state._copy(
        stage: JoinStage.inSession,
      ),
      JoinSessionEnded() when stage == JoinStage.inSession => state._ended(
        JoinOutcome.sessionEnded(event.reason),
      ),
      _ => state,
    };
  }

  static JoinSessionState _cancel(JoinSessionState state) {
    if (cancellableStages.contains(state.stage)) {
      return state._failed(JoinFailureCode.cancelled);
    }
    if (deferredCancelStages.contains(state.stage)) {
      return state._copy(cancelRequested: true);
    }
    return state;
  }

  /// An install that stopped and removed what it staged installed nothing:
  /// the join ends as the cancel it answered, or, unasked, as an install
  /// failure whose rollback completed. It never moves on.
  static JoinSessionState _undone(JoinSessionState state) =>
      state.cancelRequested
      ? state._failed(JoinFailureCode.cancelled)
      : state._failed(
          JoinFailureCode.installFailed,
          detail:
              'The install stopped before it finished. Nothing was '
              'installed; join again to install the server\'s mods.',
          rollbackComplete: true,
        );

  static JoinSessionState _gameExited(JoinSessionState state) {
    if (gameExitStages.contains(state.stage)) {
      return state._failed(JoinFailureCode.gameExited);
    }
    if (state.stage == JoinStage.inSession) {
      return state._ended(
        const JoinOutcome.sessionEnded(JoinSessionEndReason.gameExited),
      );
    }
    return state;
  }

  static JoinSessionState _fail(JoinSessionState state, JoinFailed event) {
    if (!failuresByStage[state.stage]!.contains(event.code)) return state;
    // Only a typed address can change identity: a target that names its
    // server treats another one as an invalid descriptor ([_verified]), and
    // [start] refuses to trust a server for it.
    if (event.code == JoinFailureCode.identityChanged &&
        state.target is! JoinAddressTarget) {
      return state;
    }
    if (event.code == JoinFailureCode.admissionChanged &&
        state.replans < maximumReplans) {
      return state._copy(
        stage: JoinStage.fetchingDescriptor,
        descriptor: null,
        plan: null,
        replans: state.replans + 1,
      );
    }
    return state._failed(
      event.code,
      detail: event.detail,
      rollbackComplete: event.rollbackComplete,
      gameOwnership: event.gameOwnership,
      servedServerId: event.servedServerId,
    );
  }

  static JoinSessionState _resolved(JoinSessionState state, String serverId) {
    final pinned = state.target.pinnedServerId;
    if (pinned != null && pinned != serverId) {
      return state._failed(
        JoinFailureCode.invalidLink,
        detail: 'The target resolved to another server.',
      );
    }
    return state._copy(stage: JoinStage.fetchingDescriptor, serverId: serverId);
  }

  /// A typed address learns its server from the first descriptor; every
  /// other target, and a typed address after that, must get the server it
  /// already names. Another server at a typed address is an identity
  /// change, which the player may choose to trust; anywhere else it is an
  /// invalid descriptor.
  static JoinSessionState _verified(
    JoinSessionState state,
    VerifiedServerDescriptor descriptor,
  ) {
    final expected = state.serverId;
    final address = state.target is JoinAddressTarget;
    if (expected != null && descriptor.serverId != expected) {
      return address
          ? state._failed(
              JoinFailureCode.identityChanged,
              detail: 'This address now serves a different server.',
              servedServerId: descriptor.serverId,
            )
          : state._failed(
              JoinFailureCode.descriptorInvalid,
              detail: 'The descriptor belongs to another server.',
            );
    }
    if (expected == null && !address) {
      return state._failed(
        JoinFailureCode.descriptorInvalid,
        detail: 'No server was expected for this descriptor.',
      );
    }
    return state._copy(
      stage: JoinStage.checking,
      serverId: descriptor.serverId,
      descriptor: descriptor,
    );
  }

  static JoinSessionState _planned(JoinSessionState state, JoinPlan plan) {
    // A plan for an older descriptor is stale.
    if (plan.server.descriptorSha256 != state.descriptor?.descriptorSha256) {
      return state;
    }
    if (!plan.isJoinable) {
      return state._failed(
        _failureOf(plan.blockers.first.code),
        detail: plan.blockers.first.message,
        blockers: plan.blockers,
      );
    }
    // Host-served archives come from the host itself, so a plan made as if
    // the host could be contacted cannot pass when it cannot.
    if (plan.hasUnverifiedPackages && !state.mayContactHost) {
      return state._failed(
        JoinFailureCode.directNotAllowed,
        detail:
            'Only the host serves some packages, and reaching it directly '
            'would show it your address.',
      );
    }
    final planned = state._copy(plan: plan);
    if (state.consentKey != null && state.consentKey == plan.consentKey) {
      return _afterConsent(planned);
    }
    return planned._copy(stage: JoinStage.awaitingConsent);
  }

  static JoinSessionState _consented(
    JoinSessionState state,
    JoinConsentGiven event,
  ) {
    final plan = state.plan!;
    if (plan.hasUnverifiedPackages && !event.hostServedAcknowledged) {
      return state;
    }
    return _afterConsent(state._copy(consentKey: plan.consentKey));
  }

  static JoinSessionState _afterConsent(JoinSessionState state) =>
      state.plan!.needsDownloads
      ? state._copy(
          stage: JoinStage.downloading,
          progress: const JoinTransferProgress(bytesDone: 0, bytesTotal: 0),
        )
      : state._copy(stage: JoinStage.preparingProfile);

  /// Finishes a step that holds cancel requests until it completes.
  static JoinSessionState _finished(JoinSessionState state, JoinStage next) =>
      state.cancelRequested
      ? state._failed(JoinFailureCode.cancelled)
      : state._copy(stage: next);

  static JoinFailureCode _failureOf(JoinBlockerCode code) => switch (code) {
    JoinBlockerCode.gameBuildMismatch => JoinFailureCode.gameBuildMismatch,
    JoinBlockerCode.runtimeOutdated => JoinFailureCode.runtimeOutdated,
    JoinBlockerCode.standaloneOnServer => JoinFailureCode.standaloneOnServer,
    JoinBlockerCode.contentUnavailable => JoinFailureCode.contentUnavailable,
    JoinBlockerCode.directNotAllowed => JoinFailureCode.directNotAllowed,
    JoinBlockerCode.admissionBlocked => JoinFailureCode.admissionBlocked,
  };
}
