part of 'launcher_bloc.dart';

/// The join flow: what the player does in it, and how it opens.
///
/// Every way in, a session link, the browser's JOIN, an address typed in the
/// direct-connect dialog and the game's handoff, opens the same flow. Before
/// it contacts anyone it asks the approved questions: the address disclosure
/// before the player's first join, and "Allow a direct connection?" whenever
/// the target can only be reached directly, Cancel first. Nothing is
/// downloaded, installed or started before the player consents on the sheet.
/// No message here repeats an address or an error's own text.
extension LauncherJoinActions on LauncherBloc {
  FutureOr<void> _dispatchMultiplayerJoinEvent(
    MultiplayerJoinEvent event,
    Emitter<LauncherState> emit,
  ) => switch (event) {
    MultiplayerJoinRequested() => _onJoinRequested(event, emit),
    MultiplayerJoinQuestionAnswered() => _onJoinQuestionAnswered(event, emit),
    MultiplayerJoinHostServedTicked() => _onJoinHostServedTicked(event, emit),
    MultiplayerJoinConsented() => _onJoinConsented(event, emit),
    MultiplayerJoinCancelled() => _onJoinCancelled(event.flow, emit),
    MultiplayerJoinRetried() => _onJoinRetried(event, emit),
    MultiplayerJoinTrusted() => _onJoinTrusted(event, emit),
    MultiplayerJoinDirectAsked() => _onJoinDirectAsked(event, emit),
    MultiplayerJoinClosed() => _onJoinClosed(event, emit),
    MultiplayerJoinPreferencesRead() => _onJoinPreferencesRead(event, emit),
    MultiplayerJoinDisclosureSaved() => _onJoinDisclosureSaved(event, emit),
    MultiplayerJoinStepAnswered() => _onJoinStepAnswered(event, emit),
    MultiplayerJoinProgressed() => _onJoinProgressed(event, emit),
    MultiplayerJoinGameChecked() => _onJoinGameChecked(event, emit),
    MultiplayerJoinLaunched() => _onJoinLaunched(event, emit),
    MultiplayerJoinHandoffReceived() => _onJoinHandoffReceived(event, emit),
  };

  /// Listens for the joins the games this launcher started hand over.
  void _listenForJoinHandoffs() {
    final gateway = _joinGateway;
    final work = _multiplayerWork.join;
    if (gateway == null || work.handoffs != null || isClosed) return;
    work.handoffs = gateway.handoffs.listen((arrival) {
      if (!isClosed) add(MultiplayerJoinHandoffReceived(arrival));
    }, onError: (Object _) {});
  }

  Future<void> _onJoinRequested(
    MultiplayerJoinRequested event,
    Emitter<LauncherState> emit,
  ) async {
    _openJoin(emit, event.target, event.origin);
  }

  /// Opens a flow for [target], unless a join past its consent still runs.
  /// [received] says what asked for it, as a sentence. Says whether it
  /// opened.
  bool _openJoin(
    Emitter<LauncherState> emit,
    JoinTarget target,
    JoinFlowOrigin origin, {
    String? received,
  }) {
    if (_joinGateway == null) {
      emit(
        state.copyWith(
          statusMessage:
              '${received == null ? '' : '$received '}Joining is not '
              'available in this build, so nothing was joined.',
          statusSeverity: IssueSeverity.warning,
        ),
      );
      return false;
    }
    final status = received == null
        ? null
        : '$received Nothing joins until you agree.';
    final current = _flow;
    if (current != null && _joinUnderWay(current)) {
      emit(
        state.copyWith(
          statusMessage:
              'A join is already under way. Finish or cancel it before you '
              'join another server.',
          statusSeverity: IssueSeverity.warning,
        ),
      );
      return false;
    }
    final work = _multiplayerWork.join;
    work
      ..stop()
      ..attempt = 0;
    final flow = JoinFlow(id: ++work.flows, origin: origin, target: target);
    final typed =
        target is JoinAddressTarget && origin == JoinFlowOrigin.address
        ? _typedAddress(target)
        : null;
    if (typed != null) {
      final history = state.multiplayer.join.typedAddresses;
      emit(
        state.copyWith(
          multiplayer: state.multiplayer.copyWith(
            join: state.multiplayer.join.copyWith(
              typedAddresses: [
                typed,
                ...history.where((entry) => entry != typed),
              ].take(MultiplayerJoinView.typedAddressLimit).toList(),
            ),
          ),
        ),
      );
    }
    if (state.gameInstall == null) {
      _showFlow(
        emit,
        flow.copyWith(problem: JoinFlowProblem.noInstall),
        status: status ?? "Select Robotopia's install in Setup to join.",
        severity: IssueSeverity.warning,
      );
      return true;
    }
    _showFlow(emit, flow, status: status ?? 'Getting ready to join…');
    _readJoinPreferences(flow);
    return true;
  }

  /// Whether [flow]'s join is past the player's consent and not over yet.
  bool _joinUnderWay(JoinFlow flow) {
    final stage = flow.join?.stage;
    return stage != null &&
        stage.index > JoinStage.awaitingConsent.index &&
        stage.index < JoinStage.inSession.index;
  }

  void _readJoinPreferences(JoinFlow flow) {
    final gateway = _joinGateway!;
    _beside<MultiplayerPreferences>(
      Future.sync(gateway.loadPreferences),
      (preferences) => MultiplayerJoinPreferencesRead(flow.id, preferences),
      () => MultiplayerJoinPreferencesRead(flow.id, _unreadablePreferences()),
    );
  }

  Future<void> _onJoinPreferencesRead(
    MultiplayerJoinPreferencesRead event,
    Emitter<LauncherState> emit,
  ) async {
    final flow = _flow;
    if (flow == null || flow.id != event.flow || flow.join != null) return;
    if (!event.preferences.readable) {
      _showFlow(
        emit,
        flow.copyWith(problem: JoinFlowProblem.settingsUnreadable),
        status:
            'Your multiplayer settings could not be read just now, so nothing '
            'was joined.',
        severity: IssueSeverity.warning,
      );
      return;
    }
    _multiplayerWork.join.preferences = event.preferences;
    _askOrStart(emit, flow);
  }

  /// Asks what must be asked before anything is contacted, then starts.
  void _askOrStart(Emitter<LauncherState> emit, JoinFlow flow) {
    final settings = _multiplayerWork.join.preferences!.settings;
    if (settings.needsPrivacyDisclosure) {
      _showFlow(
        emit,
        flow.copyWith(question: JoinQuestion.disclosure),
        status: 'Read how multiplayer treats your address before you join.',
      );
      return;
    }
    if (flow.privacy == MultiplayerAddressPrivacy.relayOnly &&
        _needsDirect(flow.target)) {
      _showFlow(
        emit,
        flow.copyWith(question: JoinQuestion.directConnection),
        status: 'This server can only be reached directly.',
      );
      return;
    }
    _startJoin(emit, flow);
  }

  /// Whether Hide my IP cannot reach [target]: the reducer itself refuses
  /// to start it relay-only, before anything is contacted.
  bool _needsDirect(JoinTarget target) =>
      JoinSession.start(
        target,
        privacy: SessionPrivacy.relayOnly,
      ).outcome?.failure?.code ==
      JoinFailureCode.directNotAllowed;

  Future<void> _onJoinQuestionAnswered(
    MultiplayerJoinQuestionAnswered event,
    Emitter<LauncherState> emit,
  ) async {
    final flow = _flow;
    final question = flow?.question;
    if (flow == null || flow.id != event.flow || question == null) return;
    final failed = flow.join?.outcome?.failure;
    switch (question) {
      case JoinQuestion.disclosure:
        if (!event.yes) {
          _dropJoin(emit, 'Nothing was joined.');
          return;
        }
        _showFlow(
          emit,
          flow.copyWith(clearQuestion: true),
          status: 'Getting ready to join…',
        );
        // Raising the acknowledgement can only add to what the game's copy
        // records, so it is saved even while the game runs.
        final gateway = _joinGateway!;
        _beside<MultiplayerPreferences>(
          Future.sync(
            () => gateway.changePreferences(
              const MultiplayerPreferencesChange(acknowledgeDisclosure: true),
            ),
          ),
          (preferences) => MultiplayerJoinDisclosureSaved(flow.id, preferences),
          () => MultiplayerJoinDisclosureSaved(flow.id, null),
        );
      case JoinQuestion.directConnection:
        if (!event.yes) {
          if (failed != null) {
            _showFlow(
              emit,
              flow.copyWith(clearQuestion: true),
              status: 'Hide my IP stays on.',
            );
          } else {
            _dropJoin(emit, 'Nothing was joined. Hide my IP stays on.');
          }
          return;
        }
        _startJoin(
          emit,
          flow.copyWith(
            privacy: MultiplayerAddressPrivacy.direct,
            clearQuestion: true,
          ),
        );
    }
  }

  Future<void> _onJoinDisclosureSaved(
    MultiplayerJoinDisclosureSaved event,
    Emitter<LauncherState> emit,
  ) async {
    final flow = _flow;
    if (flow == null || flow.id != event.flow || flow.join != null) return;
    final saved = event.preferences;
    if (saved == null || saved.settings.needsPrivacyDisclosure) {
      _showFlow(
        emit,
        flow.copyWith(problem: JoinFlowProblem.disclosureNotSaved),
        status: 'That you read the disclosure could not be saved.',
        severity: IssueSeverity.warning,
      );
      return;
    }
    _multiplayerWork.join.preferences = saved;
    // Settings → Multiplayer shows the disclosure as read.
    add(const MultiplayerPreferencesRefreshRequested(checkGame: false));
    _askOrStart(emit, flow);
  }

  /// The developer-mode box exists only on a consent whose plan takes mods
  /// from the host, so a tick counts nowhere else.
  Future<void> _onJoinHostServedTicked(
    MultiplayerJoinHostServedTicked event,
    Emitter<LauncherState> emit,
  ) async {
    final flow = _flow;
    final join = flow?.join;
    if (flow == null ||
        flow.id != event.flow ||
        !identical(join, event.consent) ||
        join?.stage != JoinStage.awaitingConsent ||
        !(join?.plan?.hasUnverifiedPackages ?? false)) {
      return;
    }
    _showFlow(emit, flow.copyWith(hostServedTicked: event.ticked));
  }

  /// The reducer takes no consent to a plan with mods only the host has
  /// unless the developer-mode box was ticked, so the join then waits at
  /// the sheet.
  Future<void> _onJoinConsented(
    MultiplayerJoinConsented event,
    Emitter<LauncherState> emit,
  ) async {
    final flow = _flow;
    if (flow == null ||
        flow.id != event.flow ||
        !identical(flow.join, event.consent) ||
        flow.join?.stage != JoinStage.awaitingConsent) {
      return;
    }
    _reduceJoin(
      emit,
      JoinConsentGiven(
        hostServedAcknowledged: flow.hostServedTicked,
        closeOwnedGameAcknowledged: event.closeOwnedGame,
      ),
    );
  }

  Future<void> _onJoinCancelled(int id, Emitter<LauncherState> emit) async {
    final flow = _flow;
    if (flow == null || flow.id != id) return;
    final join = flow.join;
    if (join == null || join.isEnded) {
      _dropJoin(emit, join == null ? 'Nothing was joined.' : null);
      return;
    }
    if (join.stage == JoinStage.inSession) return;
    // Stops resolving, fetching and downloading at once, and an install
    // before it changes anything the game reads.
    _multiplayerWork.join.token?.cancel();
    final cancelled = JoinSession.reduce(join, const JoinCancelRequested());
    _showFlow(
      emit,
      flow.copyWith(join: cancelled, hostServedTicked: false),
      status: cancelled.isEnded
          ? null
          : 'Stopping the join once this step finishes…',
    );
    if (cancelled.isEnded) {
      _advanceJoin(emit);
    } else if (join.stage == JoinStage.launching && flow.launch != null) {
      await _stopStartedJoin(emit, flow.launch!, _joinGateway);
    }
  }

  Future<void> _onJoinRetried(
    MultiplayerJoinRetried event,
    Emitter<LauncherState> emit,
  ) async {
    final flow = _flow;
    if (flow == null || flow.id != event.flow || !_joinStopped(flow)) return;
    _restartJoin(emit, flow);
  }

  Future<void> _onJoinTrusted(
    MultiplayerJoinTrusted event,
    Emitter<LauncherState> emit,
  ) async {
    final flow = _flow;
    final failure = flow?.join?.outcome?.failure;
    final served = failure?.servedServerId;
    if (flow == null ||
        flow.id != event.flow ||
        failure?.code != JoinFailureCode.identityChanged ||
        served == null ||
        flow.target is! JoinAddressTarget) {
      return;
    }
    _restartJoin(emit, flow.copyWith(trustedServerId: served));
  }

  Future<void> _onJoinDirectAsked(
    MultiplayerJoinDirectAsked event,
    Emitter<LauncherState> emit,
  ) async {
    final flow = _flow;
    if (flow == null ||
        flow.id != event.flow ||
        flow.join?.outcome?.failure?.code != JoinFailureCode.directNotAllowed) {
      return;
    }
    _showFlow(
      emit,
      flow.copyWith(question: JoinQuestion.directConnection),
      status: 'This server can only be reached directly.',
    );
  }

  Future<void> _onJoinClosed(
    MultiplayerJoinClosed event,
    Emitter<LauncherState> emit,
  ) async {
    final flow = _flow;
    if (flow == null || flow.id != event.flow) return;
    if (_joinStopped(flow)) {
      _dropJoin(emit, null);
    } else {
      await _onJoinCancelled(flow.id, emit);
    }
  }

  /// Whether [flow] stopped: its join ended, or it never began.
  bool _joinStopped(JoinFlow flow) =>
      flow.problem != null || (flow.join?.isEnded ?? false);

  /// The same flow from its start: the preferences are read again, and a
  /// new attempt begins once any question is answered.
  void _restartJoin(Emitter<LauncherState> emit, JoinFlow flow) {
    _multiplayerWork.join.stop();
    final next = flow.copyWith(
      clearJoin: true,
      hostServedTicked: false,
      clearProblem: true,
      clearQuestion: true,
      clearLaunch: true,
      clearGameRunning: true,
      waitingLong: false,
      closed: false,
    );
    if (state.gameInstall == null) {
      _showFlow(
        emit,
        next.copyWith(problem: JoinFlowProblem.noInstall),
        status: "Select Robotopia's install in Setup to join.",
        severity: IssueSeverity.warning,
      );
      return;
    }
    _showFlow(emit, next, status: 'Getting ready to join…');
    _readJoinPreferences(next);
  }

  /// Ends the flow and closes its dialog; [status] says what became of it.
  void _dropJoin(Emitter<LauncherState> emit, String? status) {
    _multiplayerWork.join.stop();
    _showFlow(emit, null, status: status);
  }

  Future<void> _onJoinHandoffReceived(
    MultiplayerJoinHandoffReceived event,
    Emitter<LauncherState> emit,
  ) async {
    final arrival = event.arrival;
    final install = state.gameInstall;
    String path(String value) => value.replaceAll('\\', '/').toLowerCase();
    if (install == null || path(install.path) != path(arrival.install.path)) {
      emit(
        state.copyWith(
          statusMessage:
              'A game in another install asked to join a server. Select that '
              'install in Setup, then ask the game again.',
          statusSeverity: IssueSeverity.warning,
        ),
      );
      return;
    }
    final target = switch (arrival.handoff.target) {
      LaunchHandoffInvite(:final inviteCode) => JoinLinkTarget(
        SessionLink.invite(inviteCode),
      ),
      LaunchHandoffListing(:final serverId, :final source) => JoinListingTarget(
        serverId: serverId,
        source: source,
      ),
      LaunchHandoffCurrent() =>
        _multiplayerWork.join.launchedTargets[arrival.handoff.requestId],
    };
    if (target == null) {
      emit(
        state.copyWith(
          statusMessage:
              'Robotopia asked to join its server again, but this launcher no '
              'longer knows which server that was. Join it again from '
              'Robotopia.',
          statusSeverity: IssueSeverity.warning,
        ),
      );
      return;
    }
    _openJoin(
      emit,
      target,
      JoinFlowOrigin.handoff,
      received: 'Robotopia asked to join a server.',
    );
  }
}

/// The address the player typed, as the direct-connect dialog's history
/// shows it.
String _typedAddress(JoinAddressTarget target) => target.host.contains(':')
    ? '[${target.host}]:${target.port}'
    : '${target.host}:${target.port}';
