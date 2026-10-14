part of '../multiplayer.dart';

/// The join flow as a pure reducer: resolving, fetching the descriptor,
/// checking, consent, downloading, installing, preparing the profile,
/// launching, connecting, synchronizing, the session, and the end.
///
/// - Each step advances only on its own completion event; any event a step
///   cannot use, including a failure it cannot produce, leaves the state as
///   it is. Stale events from abandoned work are therefore harmless.
/// - Cancelling stops at once wherever the work can be abandoned. While
///   installing, preparing the profile or launching it is held until the
///   step finishes, and the join then ends as cancelled instead of moving on;
///   if the step fails instead, its failure is what the join reports.
/// - When admission changes while connecting, the join fetches the
///   descriptor and plans once more on its own. Consent is asked again only
///   if the plan's [JoinPlan.consentKey] changed; a second change ends the
///   join.
abstract final class JoinSession {
  /// The failures each step can produce. Cancellation is separate: see
  /// [cancellableStages] and [deferredCancelStages].
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
    },
    JoinStage.checking: {
      JoinFailureCode.gameBuildMismatch,
      JoinFailureCode.runtimeOutdated,
      JoinFailureCode.contentUnavailable,
      JoinFailureCode.admissionBlocked,
      JoinFailureCode.standaloneOnServer,
    },
    JoinStage.awaitingConsent: {},
    JoinStage.downloading: {
      JoinFailureCode.downloadFailed,
      JoinFailureCode.integrityFailed,
      JoinFailureCode.manifestMismatch,
    },
    JoinStage.installing: {JoinFailureCode.installFailed},
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

  /// The automatic re-plans a join may make when admission changes.
  static const maximumReplans = 1;

  /// A new join of [target], resolving.
  static JoinSessionState start(JoinTarget target) =>
      JoinSessionState._(stage: JoinStage.resolving, target: target);

  /// The state after [event].
  static JoinSessionState reduce(JoinSessionState state, JoinEvent event) {
    if (state.isEnded) return state;
    final stage = state.stage;
    return switch (event) {
      JoinCancelRequested() => _cancel(state),
      JoinFailed() => _fail(state, event),
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

  static JoinSessionState _fail(JoinSessionState state, JoinFailed event) {
    if (!failuresByStage[state.stage]!.contains(event.code)) return state;
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

  static JoinSessionState _verified(
    JoinSessionState state,
    VerifiedServerDescriptor descriptor,
  ) {
    if (descriptor.serverId != state.serverId) {
      return state._failed(
        JoinFailureCode.descriptorInvalid,
        detail: 'The descriptor belongs to another server.',
      );
    }
    return state._copy(stage: JoinStage.checking, descriptor: descriptor);
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
    return _afterConsent(
      state._copy(consentKey: plan.consentKey, privacy: event.privacy),
    );
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
    JoinBlockerCode.admissionBlocked => JoinFailureCode.admissionBlocked,
  };
}
