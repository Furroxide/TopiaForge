part of 'join_flow_test.dart';

MultiplayerJoinHostServedTicked _tick(JoinFlow flow) =>
    MultiplayerJoinHostServedTicked(
      flow: flow.id,
      consent: flow.join!,
      ticked: true,
    );

void registerConsentGenerationTests() {
  test('cancel clears the host tick before any retry begins', () async {
    final (bloc, _) = await _atHostServedConsent();
    final consent = _flowOf(bloc)!;
    bloc.add(_tick(consent));
    await pumpEventQueue();
    expect(_flowOf(bloc)!.hostServedTicked, isTrue);
    bloc.add(MultiplayerJoinCancelled(consent.id));
    await _failed(bloc);
    expect(_flowOf(bloc)!.hostServedTicked, isFalse);
  });

  test(
    'a late tick from an earlier consent cannot acknowledge a retry',
    () async {
      final (bloc, gateway) = await _atHostServedConsent();
      final earlier = _flowOf(bloc)!;
      bloc.add(MultiplayerJoinCancelled(earlier.id));
      await _failed(bloc);
      bloc.add(MultiplayerJoinRetried(earlier.id));
      final current = await _atStage(bloc, JoinStage.awaitingConsent);
      expect(current.id, earlier.id);
      expect(current.join, isNot(same(earlier.join)));
      // Even when the planner reuses the same plan object, this is a new consent.
      expect(current.join!.plan, same(earlier.join!.plan));
      bloc.add(_tick(earlier));
      await pumpEventQueue();
      expect(_flowOf(bloc)!.hostServedTicked, isFalse);
      bloc.add(_consent(bloc));
      await pumpEventQueue();
      expect(_flowOf(bloc)!.join, same(current.join));
      expect(gateway.calls, isNot(contains('answer downloading')));
    },
  );

  test(
    'a late Join cannot consent to a different sheet even after a new tick',
    () async {
      final (bloc, gateway) = await _atHostServedConsent();
      final earlier = _flowOf(bloc)!;
      final oldPress = _consent(bloc);
      bloc.add(MultiplayerJoinCancelled(earlier.id));
      await _failed(bloc);
      bloc.add(MultiplayerJoinRetried(earlier.id));
      final current = await _atStage(bloc, JoinStage.awaitingConsent);
      bloc.add(_tick(current));
      await pumpEventQueue();
      expect(_flowOf(bloc)!.hostServedTicked, isTrue);
      bloc.add(oldPress);
      await pumpEventQueue();
      expect(_flowOf(bloc)!.join, same(current.join));
      expect(gateway.calls, isNot(contains('answer downloading')));
      bloc.add(_consent(bloc));
      await _atStage(bloc, JoinStage.launching);
    },
  );

  test(
    'double consent starts one download and late ticks outside consent do nothing',
    () async {
      final (bloc, gateway) = await _atHostServedConsent();
      final consent = _flowOf(bloc)!;
      gateway.hold(JoinStage.downloading);
      final press = _consent(bloc);
      bloc
        ..add(_tick(consent))
        ..add(press)
        ..add(press);
      await _atStage(bloc, JoinStage.downloading);
      bloc.add(_tick(_flowOf(bloc)!));
      await pumpEventQueue();
      expect(
        gateway.calls.where((call) => call == 'answer downloading'),
        hasLength(1),
      );
      expect(_flowOf(bloc)!.hostServedTicked, isFalse);
      bloc.add(MultiplayerJoinCancelled(consent.id));
      await _failed(bloc);
      expect(_flowOf(bloc)!.hostServedTicked, isFalse);
    },
  );

  test(
    'a failure clears the tick and a replacement flow cannot reuse it',
    () async {
      final (bloc, gateway) = await _atHostServedConsent();
      final earlier = _flowOf(bloc)!;
      bloc.add(_tick(earlier));
      await pumpEventQueue();
      bloc.add(
        MultiplayerJoinStepAnswered(
          flow: earlier.id,
          attempt: 1,
          answer: JoinFailed(JoinFailureCode.cancelled),
        ),
      );
      await _failed(bloc);
      expect(_flowOf(bloc)!.hostServedTicked, isFalse);
      bloc.add(MultiplayerJoinRequested(_lanLink));
      final current = await _atStage(bloc, JoinStage.awaitingConsent);
      expect(current.id, isNot(earlier.id));
      bloc
        ..add(_tick(earlier))
        ..add(_consent(bloc));
      await pumpEventQueue();
      expect(_flowOf(bloc)!.hostServedTicked, isFalse);
      expect(gateway.calls, isNot(contains('answer downloading')));
    },
  );

  test(
    'a changed host plan asks afresh and rejects events from before the replan',
    () async {
      final (server, plan) = await _hostServedPlan();
      final gateway = _JoinGateway(server: server, plan: plan);
      final repository = _JoinRepository(install: _install);
      final bloc = await _startedBloc(repository, gateway);
      bloc.add(MultiplayerJoinRequested(_lanLink));
      final earlier = await _atStage(bloc, JoinStage.awaitingConsent);
      bloc
        ..add(_tick(earlier))
        ..add(_consent(bloc));
      await _launched(bloc);
      final (nextServer, nextPlan) = await _hostServedPlan(
        hostedId: 'example.next',
      );
      gateway
        ..server = nextServer
        ..plan = nextPlan;
      repository.report(_activity(state: LaunchSessionState.connecting));
      await _atStage(bloc, JoinStage.connecting);
      repository.report(
        _activity(failure: LaunchMultiplayerFailure.admissionChanged),
      );
      final current = await _atStage(bloc, JoinStage.awaitingConsent);
      expect(current.join!.replans, 1);
      expect(current.hostServedTicked, isFalse);
      final downloads = gateway.calls
          .where((call) => call == 'answer downloading')
          .length;
      bloc
        ..add(_tick(earlier))
        ..add(_consent(bloc));
      await pumpEventQueue();
      expect(_flowOf(bloc)!.join, same(current.join));
      expect(
        gateway.calls.where((call) => call == 'answer downloading'),
        hasLength(downloads),
      );
    },
  );
}
