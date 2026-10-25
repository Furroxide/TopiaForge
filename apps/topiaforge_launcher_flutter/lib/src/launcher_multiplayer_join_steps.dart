part of 'launcher_bloc.dart';

/// The join flow's bookkeeping for work beside the event lane.
///
/// Each start of a join is an attempt; every step's answer names its attempt,
/// so an answer from an attempt that was cancelled, retried or replaced is
/// dropped. One step's work runs at a time, with the attempt's token.
final class _JoinWork {
  /// Counts flows and attempts.
  int flows = 0;
  int attempts = 0;

  /// The attempt whose answers count.
  int attempt = 0;

  /// Stops the running step's work.
  CancellationToken? token;

  /// The stage whose work runs now, if any.
  JoinStage? answering;

  /// The launch intent of the attempt: one join, kept across the restarts
  /// and the re-plan it makes.
  String intentId = '';

  /// The preferences the flow started from.
  MultiplayerPreferences? preferences;

  /// The target each started join was for, by the request id the game
  /// reports it under, so a game that hands back its own join is planned
  /// again.
  final launchedTargets = <String, JoinTarget>{};

  /// The game's handoffs, while the launcher listens.
  StreamSubscription<JoinHandoffArrival>? handoffs;

  /// A new attempt: earlier work stops, and its answers no longer count.
  int begin() {
    stop();
    token = CancellationToken();
    intentId = 'join-${_randomHex(12)}';
    return attempt = ++attempts;
  }

  void stop() {
    token?.cancel();
    token = null;
    answering = null;
  }
}

String _randomHex(int bytes) {
  final random = math.Random.secure();
  return [
    for (var index = 0; index < bytes; index++)
      random.nextInt(256).toRadixString(16).padLeft(2, '0'),
  ].join();
}

/// Moves a join through its steps: each step's work runs beside the event
/// lane and reports back, the reducer takes each answer, and the game's
/// records move the join from launching to the session and its end.
extension LauncherJoinSteps on LauncherBloc {
  MultiplayerJoinGateway? get _joinGateway {
    final gateway = _multiplayer;
    return gateway is MultiplayerJoinGateway ? gateway : null;
  }

  JoinFlow? get _flow => state.multiplayer.join.flow;

  /// Whether [flow] names the flow that runs now.
  bool _isCurrentFlow(int flow) => _flow?.id == flow;

  /// Shows [flow], and says what it does in the status bar.
  void _showFlow(
    Emitter<LauncherState> emit,
    JoinFlow? flow, {
    String? status,
    IssueSeverity? severity,
    LaunchActivity? activity,
  }) {
    final join = flow?.join;
    final failed = join?.outcome?.failure;
    emit(
      state.copyWith(
        multiplayer: state.multiplayer.copyWith(
          join: state.multiplayer.join.copyWith(
            flow: flow,
            clearFlow: flow == null,
          ),
        ),
        launchActivity: activity,
        statusMessage:
            status ??
            (join == null ? null : joinStatusOf(join, flow!.serverName)),
        statusSeverity:
            severity ??
            (failed != null && failed.code != JoinFailureCode.cancelled
                ? IssueSeverity.error
                : null),
      ),
    );
  }

  /// Starts [flow]'s join under [privacy], a new attempt.
  void _startJoin(Emitter<LauncherState> emit, JoinFlow flow) {
    final preferences = _multiplayerWork.join.preferences!;
    _multiplayerWork.join.begin();
    final join = JoinSession.start(
      flow.target,
      privacy: flow.privacy == MultiplayerAddressPrivacy.direct
          ? preferences.settings.privacyForSession(allowDirect: true)
          : SessionPrivacy.relayOnly,
      trustedServerId: flow.trustedServerId,
    );
    final next = flow.copyWith(
      join: join,
      voice: preferences.settings.voiceActive,
      clearQuestion: true,
      clearProblem: true,
      clearGameRunning: true,
      clearLaunch: true,
      waitingLong: false,
    );
    _showFlow(emit, next);
    _advanceJoin(emit);
  }

  /// Reduces [event] into the running join, and moves on.
  void _reduceJoin(Emitter<LauncherState> emit, JoinEvent event) {
    final flow = _flow;
    final join = flow?.join;
    if (flow == null || join == null) return;
    final plan = event is JoinPlanReady ? event.plan : null;
    final next = JoinSession.reduce(join, event);
    if (identical(next, join) && plan == null) return;
    _showFlow(
      emit,
      flow.copyWith(
        join: next,
        plan: plan,
        clearLaunch: _replanned(join, next),
        // The developer-mode box counts only on the consent it was ticked on.
        hostServedTicked:
            flow.hostServedTicked && next.stage == JoinStage.awaitingConsent,
      ),
    );
    if (next.stage != join.stage) _advanceJoin(emit);
  }

  /// Whether the join went back from the game to plan once more, so the
  /// launch it had no longer speaks for it.
  bool _replanned(JoinSessionState before, JoinSessionState after) =>
      before.stage.index >= JoinStage.launching.index &&
      after.stage.index < JoinStage.launching.index;

  /// Starts the work [_flow]'s join waits on, if it is not running yet.
  void _advanceJoin(Emitter<LauncherState> emit) {
    final flow = _flow;
    final join = flow?.join;
    final gateway = _joinGateway;
    if (flow == null || join == null || gateway == null) return;
    final work = _multiplayerWork.join;
    switch (join.stage) {
      case JoinStage.ended:
        _joinEnded(emit, flow);
      case JoinStage.inSession:
        work.stop();
        _joinEntered(emit, flow, gateway);
      case JoinStage.awaitingConsent:
        work.answering = null;
        if (flow.gameRunning == null) _checkJoinGame(flow, gateway);
      case JoinStage.connecting || JoinStage.synchronizing:
        // The game's records move these on.
        work.answering = null;
      case JoinStage.launching:
        if (work.answering != JoinStage.launching) {
          _launchJoin(emit, flow, join, gateway);
        }
      case _:
        if (work.answering != join.stage) _answerStep(flow, join, gateway);
    }
  }

  void _answerStep(
    JoinFlow flow,
    JoinSessionState join,
    MultiplayerJoinGateway gateway,
  ) {
    final work = _multiplayerWork.join;
    final attempt = work.attempt;
    final token = work.token;
    work.answering = join.stage;
    final install = state.gameInstall;
    final developerMode = state.developerMode;
    _beside<JoinEvent?>(
      Future.sync(
        () => gateway.answerJoin(
          join,
          cancellation: token,
          install: install,
          developerMode: developerMode,
          onProgress: (progress) {
            if (!isClosed) {
              add(
                MultiplayerJoinProgressed(
                  flow: flow.id,
                  attempt: attempt,
                  progress: progress,
                ),
              );
            }
          },
        ),
      ),
      (answer) => MultiplayerJoinStepAnswered(
        flow: flow.id,
        attempt: attempt,
        answer:
            answer ?? (token?.isCancelled ?? false ? null : _stepLost(join)),
      ),
      () => MultiplayerJoinStepAnswered(
        flow: flow.id,
        attempt: attempt,
        answer: _stepLost(join),
      ),
    );
  }

  /// A step whose work could not answer, worded for its stage, so the join
  /// never waits for an answer that is not coming.
  JoinEvent? _stepLost(JoinSessionState join) {
    const detail =
        'The launcher could not finish this step. Try again; if it keeps '
        'failing, open Diagnostics.';
    return switch (join.stage) {
      JoinStage.resolving => JoinFailed(
        JoinFailureCode.serviceUnavailable,
        detail: detail,
      ),
      JoinStage.fetchingDescriptor => JoinFailed(
        JoinFailureCode.unreachable,
        detail: detail,
      ),
      JoinStage.checking => JoinFailed(
        JoinFailureCode.contentUnavailable,
        detail: detail,
      ),
      JoinStage.downloading => JoinFailed(
        JoinFailureCode.downloadFailed,
        detail: detail,
      ),
      JoinStage.installing => const JoinFailed.installFailed(
        rollbackComplete: true,
        detail: detail,
      ),
      JoinStage.preparingProfile => JoinFailed(
        JoinFailureCode.profileSaveFailed,
        detail: detail,
      ),
      _ => null,
    };
  }

  void _checkJoinGame(JoinFlow flow, MultiplayerJoinGateway gateway) {
    final install = state.gameInstall;
    if (install == null) return;
    _beside<JoinGameRunning>(
      Future.sync(() => gateway.joinGameRunning(install)),
      (running) => MultiplayerJoinGameChecked(flow.id, running),
      () => MultiplayerJoinGameChecked(flow.id, JoinGameRunning.foreign),
    );
  }

  void _launchJoin(
    Emitter<LauncherState> emit,
    JoinFlow flow,
    JoinSessionState join,
    MultiplayerJoinGateway gateway,
  ) {
    final work = _multiplayerWork.join;
    final install = state.gameInstall;
    final attempt = work.attempt;
    work.answering = JoinStage.launching;
    if (install == null) {
      _reduceJoin(
        emit,
        JoinFailed(JoinFailureCode.launchBlocked, detail: _noInstall),
      );
      return;
    }
    final intentId = work.intentId;
    final developerMode = state.developerMode;
    _beside<JoinLaunch>(
      Future.sync(
        () => gateway.launchJoin(
          join,
          install: install,
          intentId: intentId,
          developerMode: developerMode,
        ),
      ),
      (launch) => MultiplayerJoinLaunched(
        flow: flow.id,
        attempt: attempt,
        launch: launch,
      ),
      () => MultiplayerJoinLaunched(
        flow: flow.id,
        attempt: attempt,
        launch: JoinLaunchRefused(
          JoinFailed(
            JoinFailureCode.startFailed,
            detail:
                'Robotopia could not be started for the join. Try again; if '
                'it keeps failing, open Diagnostics.',
          ),
        ),
      ),
    );
  }

  Future<void> _onJoinStepAnswered(
    MultiplayerJoinStepAnswered event,
    Emitter<LauncherState> emit,
  ) async {
    final work = _multiplayerWork.join;
    if (!_isCurrentFlow(event.flow) || event.attempt != work.attempt) return;
    work.answering = null;
    final answer = event.answer;
    if (answer == null) return;
    final before = _flow?.join;
    _reduceJoin(emit, answer);
    // An answer the reducer does not take at this step would leave the join
    // waiting on nothing, so the step ends with its own failure instead.
    if (before != null && identical(_flow?.join, before)) {
      final lost = _stepLost(before);
      if (lost != null) _reduceJoin(emit, lost);
    }
  }

  Future<void> _onJoinProgressed(
    MultiplayerJoinProgressed event,
    Emitter<LauncherState> emit,
  ) async {
    if (!_isCurrentFlow(event.flow) ||
        event.attempt != _multiplayerWork.join.attempt) {
      return;
    }
    _reduceJoin(emit, event.progress);
  }

  Future<void> _onJoinGameChecked(
    MultiplayerJoinGameChecked event,
    Emitter<LauncherState> emit,
  ) async {
    final flow = _flow;
    if (flow == null ||
        flow.id != event.flow ||
        flow.join?.stage != JoinStage.awaitingConsent) {
      return;
    }
    _showFlow(emit, flow.copyWith(gameRunning: event.running));
  }

  Future<void> _onJoinLaunched(
    MultiplayerJoinLaunched event,
    Emitter<LauncherState> emit,
  ) async {
    final work = _multiplayerWork.join;
    final flow = _flow;
    final launch = event.launch;
    final gateway = _joinGateway;
    if (flow == null ||
        flow.id != event.flow ||
        event.attempt != work.attempt ||
        flow.join?.stage != JoinStage.launching) {
      return;
    }
    switch (launch) {
      case JoinLaunchRefused(:final failure):
        _reduceJoin(emit, failure);
      case JoinLaunchStopped():
        _reduceJoin(
          emit,
          JoinFailed(
            JoinFailureCode.startFailed,
            detail: 'The launcher closed before Robotopia started.',
          ),
        );
      case JoinLaunchStarted():
        work.launchedTargets[launch.requestId] = flow.target;
        _showFlow(emit, flow.copyWith(launch: launch));
        if (flow.join!.cancelRequested) {
          await _stopStartedJoin(emit, launch, gateway);
          return;
        }
        final activity = launch.activity;
        if (activity != null) _followJoinActivity(emit, activity);
    }
  }

  /// A join cancelled while the game took it: a live join the game has not
  /// taken yet is withdrawn, and the launcher stops following the join.
  Future<void> _stopStartedJoin(
    Emitter<LauncherState> emit,
    JoinLaunchStarted launch,
    MultiplayerJoinGateway? gateway,
  ) async {
    final install = state.gameInstall;
    final flow = _flow;
    if (launch.live &&
        gateway != null &&
        install != null &&
        flow != null &&
        await gateway.withdrawJoinLaunch(install, launch)) {
      // The game never saw it, so nothing joins.
      _showFlow(emit, flow.copyWith(clearLaunch: true));
    }
    // The launching step is over once the game has the join; the reducer
    // then ends it as the cancel it held.
    _reduceJoin(emit, const JoinGameStarted());
  }

  /// Whether [activity] is the running join's, as the game reports it.
  bool _followsJoin(LaunchActivity activity) {
    final requestId = _flow?.launch?.requestId;
    return requestId != null && activity.requestId == requestId;
  }

  /// Moves the join on with what the game reported for it.
  void _followJoinActivity(
    Emitter<LauncherState> emit,
    LaunchActivity activity,
  ) {
    final flow = _flow;
    final join = flow?.join;
    if (flow == null || join == null || !_followsJoin(activity)) return;
    final waitingLong =
        activity.unconfirmed && join.stage == JoinStage.launching;
    var next = join;
    for (final event in joinEventsFor(join, activity)) {
      next = JoinSession.reduce(next, event);
    }
    _showFlow(
      emit,
      flow.copyWith(
        join: next,
        waitingLong: waitingLong,
        clearLaunch: _replanned(join, next),
      ),
      activity: activity,
    );
    if (next.stage != join.stage) _advanceJoin(emit);
  }

  /// The join reached the session: the game takes over, so the dialog
  /// closes, and the server joins the player's history.
  void _joinEntered(
    Emitter<LauncherState> emit,
    JoinFlow flow,
    MultiplayerJoinGateway gateway,
  ) {
    if (flow.closed) return;
    _showFlow(emit, flow.copyWith(closed: true));
    final server = flow.join?.descriptor;
    if (server == null) return;
    unawaited(
      gateway
          .recordJoin(
            serverId: server.serverId,
            name: withoutAddresses(server.descriptor.name),
          )
          .then<void>((_) {}, onError: (Object _) {}),
    );
  }

  /// The join ended: its work stops, and its outcome stays until the player
  /// closes it. A session that ended after it began needs no card: the
  /// status bar says why.
  void _joinEnded(Emitter<LauncherState> emit, JoinFlow flow) {
    _multiplayerWork.join.stop();
    final outcome = flow.join!.outcome!;
    if (outcome.sessionEnd != null) {
      _showFlow(
        emit,
        null,
        status: sessionEndMessage(outcome.sessionEnd!),
        severity: IssueSeverity.info,
      );
    }
  }
}

const _noInstall =
    "Select Robotopia's install in Setup, then join again. Nothing was "
    'started.';
