part of 'join_flow_test.dart';

void registerJoinBlocTests() {
  group('The join flow', () {
    test('a link opens the flow and joins nothing before consent', () async {
      final gateway = _JoinGateway();
      final repository = _JoinRepository(install: _install);
      final links = StreamController<SessionLinkArrival>();
      addTearDown(links.close);
      final bloc = await _startedBloc(repository, gateway, links: links.stream);

      links.add(
        SessionLinkArrival(
          _serverLink.canonical,
          source: SessionLinkSource.forwarded,
        ),
      );
      final flow = await _atStage(bloc, JoinStage.awaitingConsent);

      expect(flow.origin, JoinFlowOrigin.link);
      expect(bloc.state.multiplayer.sessionLink, isA<SessionLinkOpened>());
      expect(flow.join!.plan, isNotNull);
      expect(flow.gameRunning, anyOf(isNull, JoinGameRunning.none));
      // Let any work that could run do so: nothing downloads, installs or
      // starts until the player consents.
      await pumpEventQueue();
      expect(gateway.calls, isNot(contains('answer downloading')));
      expect(gateway.calls, isNot(contains('answer preparingProfile')));
      expect(gateway.calls, isNot(contains('launch')));
      expect(_flowOf(bloc)!.join!.stage, JoinStage.awaitingConsent);
      expect(flow.showsDialog, isTrue);
    });

    test('the disclosure comes first, before anything is contacted', () async {
      final gateway = _JoinGateway(
        preferences: MultiplayerPreferences(
          settings: MultiplayerSettings(displayName: 'Sam'),
        ),
      );
      final bloc = await _startedBloc(
        _JoinRepository(install: _install),
        gateway,
      );

      bloc.add(MultiplayerJoinRequested(JoinLinkTarget(_serverLink)));
      await _until(
        bloc,
        (state) =>
            state.multiplayer.join.flow?.question == JoinQuestion.disclosure,
      );
      expect(gateway.calls.where((call) => call.startsWith('answer')), isEmpty);

      bloc.add(
        MultiplayerJoinQuestionAnswered(flow: _flowOf(bloc)!.id, yes: true),
      );
      await _atStage(bloc, JoinStage.awaitingConsent);
      expect(gateway.calls, contains('acknowledge'));
      expect(
        gateway.calls.indexOf('acknowledge'),
        lessThan(gateway.calls.indexOf('answer resolving')),
      );
    });

    test('closing the disclosure another way joins nothing', () async {
      final gateway = _JoinGateway(
        preferences: MultiplayerPreferences(settings: MultiplayerSettings()),
      );
      final bloc = await _startedBloc(
        _JoinRepository(install: _install),
        gateway,
      );
      bloc.add(MultiplayerJoinRequested(JoinLinkTarget(_serverLink)));
      await _until(bloc, (state) => _flowOf(bloc)?.question != null);

      bloc.add(
        MultiplayerJoinQuestionAnswered(flow: _flowOf(bloc)!.id, yes: false),
      );
      await _until(bloc, (state) => state.multiplayer.join.flow == null);
      expect(bloc.state.statusMessage, 'Nothing was joined.');
      expect(gateway.calls, isNot(contains('acknowledge')));
      expect(gateway.calls.where((call) => call.startsWith('answer')), isEmpty);
    });

    test('a target only a direct connection reaches asks first; Cancel '
        'contacts nothing and keeps Hide my IP on', () async {
      final gateway = _JoinGateway();
      final bloc = await _startedBloc(
        _JoinRepository(install: _install),
        gateway,
      );

      bloc.add(
        MultiplayerJoinRequested(
          _publicAddress,
          origin: JoinFlowOrigin.address,
        ),
      );
      await _until(
        bloc,
        (state) => _flowOf(bloc)?.question == JoinQuestion.directConnection,
      );
      expect(_flowOf(bloc)!.join, isNull);
      bloc.add(
        MultiplayerJoinQuestionAnswered(flow: _flowOf(bloc)!.id, yes: false),
      );
      await _until(bloc, (state) => state.multiplayer.join.flow == null);

      expect(gateway.calls.where((call) => call.startsWith('answer')), isEmpty);
      expect(bloc.state.statusMessage, contains('Hide my IP stays on'));
      expect(bloc.state.multiplayer.join.typedAddresses, ['$_planted:27250']);
    });

    test(
      'allowing a direct connection joins with it for this session only',
      () async {
        final gateway = _JoinGateway();
        final bloc = await _startedBloc(
          _JoinRepository(install: _install),
          gateway,
        );
        bloc.add(
          MultiplayerJoinRequested(
            _publicAddress,
            origin: JoinFlowOrigin.address,
          ),
        );
        await _until(bloc, (state) => _flowOf(bloc)?.question != null);

        bloc.add(
          MultiplayerJoinQuestionAnswered(flow: _flowOf(bloc)!.id, yes: true),
        );
        final flow = await _atStage(bloc, JoinStage.awaitingConsent);

        expect(flow.privacy, MultiplayerAddressPrivacy.direct);
        expect(flow.join!.privacy, MultiplayerAddressPrivacy.direct);
        // The next join of the same address asks again.
        bloc.add(MultiplayerJoinCancelled(flow.id));
        await _until(bloc, (state) => _flowOf(bloc)!.join!.isEnded);
        bloc.add(MultiplayerJoinClosed(flow.id));
        bloc.add(
          MultiplayerJoinRequested(
            _publicAddress,
            origin: JoinFlowOrigin.address,
          ),
        );
        await _until(
          bloc,
          (state) =>
              _flowOf(bloc)?.question == JoinQuestion.directConnection &&
              _flowOf(bloc)!.id != flow.id,
        );
      },
    );

    test('a consented join installs, saves the profile, launches, and follows '
        'the game into the session', () async {
      final (bloc, gateway, repository) = await _atConsent();
      bloc.add(MultiplayerJoinConsented(flow: _flowOf(bloc)!.id));

      final launching = await _launched(bloc);
      expect(
        gateway.calls,
        containsAllInOrder([
          'answer downloading',
          'answer installing',
          'answer preparingProfile',
          'launch',
        ]),
      );
      expect(launching.launch?.requestId, _requestId);
      expect(gateway.intents.single, startsWith('join-'));

      repository.report(
        _activity(state: LaunchSessionState.connecting, phase: 'connecting'),
      );
      await _atStage(bloc, JoinStage.connecting);
      repository.report(
        _activity(
          state: LaunchSessionState.synchronizing,
          phase: 'synchronizing',
        ),
      );
      await _atStage(bloc, JoinStage.synchronizing);
      repository.report(_activity(succeeded: true));
      final inSession = await _atStage(bloc, JoinStage.inSession);

      expect(inSession.closed, isTrue, reason: 'the game takes over');
      expect(bloc.state.statusMessage, "Joined Sam's Sandbox.");
      await pumpEventQueue();
      expect(gateway.recorded, ['$_serverId Sam\'s Sandbox']);

      repository.report(
        _activity(succeeded: true, ended: LaunchSessionEndReason.hostEnded),
      );
      await _until(bloc, (state) => state.multiplayer.join.flow == null);
      expect(bloc.state.statusMessage, 'The host ended the session.');
    });

    test(
      'a game that took the join live is followed under its join id',
      () async {
        final gateway = _JoinGateway()
          ..launch = (_) => _started(live: true, requestId: 'live-join-1');
        final (bloc, _, repository) = await _atConsent(gateway: gateway);
        bloc.add(MultiplayerJoinConsented(flow: _flowOf(bloc)!.id));
        final launching = await _launched(bloc);
        expect(launching.launch!.live, isTrue);

        // Another request's records move nothing.
        repository.report(_activity(succeeded: true));
        await pumpEventQueue();
        expect(_flowOf(bloc)!.join!.stage, JoinStage.launching);
        repository.report(_activity(requestId: 'live-join-1', succeeded: true));
        await _atStage(bloc, JoinStage.inSession);
      },
    );

    test(
      'a game that has not answered for a while says so, and waits',
      () async {
        final (bloc, _, repository) = await _atConsent();
        bloc.add(MultiplayerJoinConsented(flow: _flowOf(bloc)!.id));
        await _launched(bloc);

        repository.report(_activity(unconfirmed: true));
        await _until(bloc, (state) => _flowOf(bloc)!.waitingLong);
        expect(_flowOf(bloc)!.join!.stage, JoinStage.launching);
      },
    );

    test('the consent covers closing the game this launcher started only '
        'when the sheet said so', () async {
      final gateway = _JoinGateway()..running = JoinGameRunning.owned;
      final (bloc, _, _) = await _atConsent(gateway: gateway);
      await _until(bloc, (state) => _flowOf(bloc)!.gameRunning != null);
      expect(_flowOf(bloc)!.gameRunning, JoinGameRunning.owned);

      bloc.add(
        MultiplayerJoinConsented(flow: _flowOf(bloc)!.id, closeOwnedGame: true),
      );
      final downloading = await _until(
        bloc,
        (state) =>
            _flowOf(bloc)!.join!.stage.index > JoinStage.awaitingConsent.index,
      );
      expect(
        downloading.multiplayer.join.flow!.join!.closeOwnedGameAcknowledged,
        isTrue,
      );
    });

    test('a plan with packages only the host serves cannot be consented to '
        'yet', () async {
      final server = await _server(hostServesPackages: true);
      final plan = const JoinPlanner().plan(
        server: server,
        resolutions: {
          _sha('zombies'): JoinPackageResolution.hostOffered(),
          _sha('sandbox'): JoinPackageResolution.installed(
            _manifest('tf.sandbox', '1.4.0'),
          ),
        },
        environment: JoinEnvironment(
          gameBuild: '0.0.2545',
          runtimeVersion: '0.1.0-rc.1',
          sdkVersion: '0.1.0-rc.1',
          protocolVersion: '1.0.0',
          developerMode: true,
        ),
        mayContactHost: true,
      );
      expect(plan.hasUnverifiedPackages, isTrue);
      final gateway = _JoinGateway(server: server, plan: plan);
      final bloc = await _startedBloc(
        _JoinRepository(install: _install),
        gateway,
      );
      bloc.add(
        MultiplayerJoinRequested(
          JoinLinkTarget(
            SessionLink.direct(
              serverId: _serverId,
              host: '192.168.1.20',
              port: 27250,
            ),
          ),
        ),
      );
      final flow = await _atStage(bloc, JoinStage.awaitingConsent);

      // Without the developer-mode acknowledgement the reducer holds it.
      bloc.add(MultiplayerJoinConsented(flow: flow.id));
      await pumpEventQueue();
      expect(_flowOf(bloc)!.join!.stage, JoinStage.awaitingConsent);
      expect(gateway.calls, isNot(contains('answer downloading')));
    });

    test('a newer link replaces a join waiting for consent, and a join under '
        'way keeps going', () async {
      final gateway = _JoinGateway();
      final (bloc, _, _) = await _atConsent(gateway: gateway);
      final first = _flowOf(bloc)!.id;

      bloc.add(MultiplayerJoinRequested(JoinLinkTarget(_serverLink)));
      await _until(bloc, (state) => _flowOf(bloc)!.id != first);
      final second = _flowOf(bloc)!.id;
      await _atStage(bloc, JoinStage.awaitingConsent);

      gateway.hold(JoinStage.downloading);
      bloc.add(MultiplayerJoinConsented(flow: second));
      await _atStage(bloc, JoinStage.downloading);
      bloc.add(MultiplayerJoinRequested(JoinLinkTarget(_serverLink)));
      await _until(
        bloc,
        (state) =>
            state.statusMessage.startsWith('A join is already under way'),
      );
      expect(_flowOf(bloc)!.id, second);
      expect(_flowOf(bloc)!.join!.stage, JoinStage.downloading);
    });

    test(
      'handoffs: a listing, the game\'s current join, and refusals',
      () async {
        final gateway = _JoinGateway();
        final repository = _JoinRepository(install: _install);
        final bloc = await _startedBloc(repository, gateway);
        SessionHandoff handoff(LaunchHandoffTarget target, {String? request}) =>
            SessionHandoff(
              requestId: request ?? _requestId,
              handoffId: 'handoff-1',
              target: target,
            );

        // The game's current join, before this launcher started any: unknown.
        gateway.handOff(
          JoinHandoffArrival(
            install: _install,
            handoff: handoff(const LaunchHandoffCurrent()),
          ),
        );
        await _until(
          bloc,
          (state) =>
              state.statusMessage.contains('no longer knows which server'),
        );
        expect(_flowOf(bloc), isNull);

        // Another install's game.
        gateway.handOff(
          JoinHandoffArrival(
            install: const GameInstall(
              path: '/games/Other',
              executablePath: '/games/Other/Robotopia.exe',
              bepInExStatus: ComponentState.ready,
              loaderStatus: ComponentState.ready,
            ),
            handoff: handoff(
              LaunchHandoffListing(
                serverId: _serverId,
                source: ServerListingSource.lan,
              ),
            ),
          ),
        );
        await _until(
          bloc,
          (state) => state.statusMessage.contains('another install'),
        );

        gateway.handOff(
          JoinHandoffArrival(
            install: _install,
            handoff: handoff(
              LaunchHandoffListing(
                serverId: _serverId,
                source: ServerListingSource.lan,
              ),
            ),
          ),
        );
        final flow = await _atStage(bloc, JoinStage.awaitingConsent);
        expect(flow.origin, JoinFlowOrigin.handoff);
        expect(flow.target, isA<JoinListingTarget>());

        // Started, then handed back as the game's current join.
        bloc.add(MultiplayerJoinConsented(flow: flow.id));
        await _launched(bloc);
        repository.report(
          _activity(
            failure: LaunchMultiplayerFailure.timeout,
            detail: 'Timed out.',
          ),
        );
        await _failed(bloc);
        bloc.add(MultiplayerJoinClosed(flow.id));
        gateway.handOff(
          JoinHandoffArrival(
            install: _install,
            handoff: handoff(const LaunchHandoffCurrent()),
          ),
        );
        final again = await _atStage(bloc, JoinStage.awaitingConsent);
        expect(again.id, isNot(flow.id));
        expect(again.target, same(flow.target));
      },
    );

    test('no install, unreadable settings and an acknowledgement that could '
        'not be saved each stop the flow before it begins', () async {
      final noInstall = await _startedBloc(_JoinRepository(), _JoinGateway());
      noInstall.add(MultiplayerJoinRequested(JoinLinkTarget(_serverLink)));
      await _until(noInstall, (state) => _flowOf(noInstall)?.problem != null);
      expect(_flowOf(noInstall)!.problem, JoinFlowProblem.noInstall);

      final unreadable = _JoinGateway()..unreadable = true;
      final blocked = await _startedBloc(
        _JoinRepository(install: _install),
        unreadable,
      );
      blocked.add(MultiplayerJoinRequested(JoinLinkTarget(_serverLink)));
      await _until(blocked, (state) => _flowOf(blocked)?.problem != null);
      expect(_flowOf(blocked)!.problem, JoinFlowProblem.settingsUnreadable);
      // Try again reads them again.
      unreadable.unreadable = false;
      blocked.add(MultiplayerJoinRetried(_flowOf(blocked)!.id));
      await _atStage(blocked, JoinStage.awaitingConsent);

      final unsaved = _JoinGateway(
        preferences: MultiplayerPreferences(settings: MultiplayerSettings()),
      )..failDisclosure = true;
      final refused = await _startedBloc(
        _JoinRepository(install: _install),
        unsaved,
      );
      refused.add(MultiplayerJoinRequested(JoinLinkTarget(_serverLink)));
      await _until(refused, (state) => _flowOf(refused)?.question != null);
      refused.add(
        MultiplayerJoinQuestionAnswered(flow: _flowOf(refused)!.id, yes: true),
      );
      await _until(refused, (state) => _flowOf(refused)?.problem != null);
      expect(_flowOf(refused)!.problem, JoinFlowProblem.disclosureNotSaved);
      expect(unsaved.calls.where((call) => call.startsWith('answer')), isEmpty);
    });

    test('closing the launcher during a download stops it at once', () async {
      final gateway = _JoinGateway();
      final (bloc, _, repository) = await _atConsent(gateway: gateway);
      final download = gateway.hold(JoinStage.downloading);
      bloc.add(MultiplayerJoinConsented(flow: _flowOf(bloc)!.id));
      await _atStage(bloc, JoinStage.downloading);
      expect(gateway.tokens[JoinStage.downloading]!.isCancelled, isFalse);

      await bloc.close().timeout(const Duration(seconds: 5));

      expect(gateway.tokens[JoinStage.downloading]!.isCancelled, isTrue);
      expect(download.isCompleted, isTrue);
      expect(repository.disposed, isTrue);
    });
  });
}
