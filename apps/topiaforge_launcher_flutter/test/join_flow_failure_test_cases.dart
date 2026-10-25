part of 'join_flow_test.dart';

const _hostB =
    'cf82f7bc6b072f456554dd30c31b12fe5af423bbe03ff908124a1a08f0f16443';

/// The failures after which trying again cannot help, so the card offers no
/// Retry.
const _noRetry = {
  JoinFailureCode.invalidLink,
  JoinFailureCode.inviteExpired,
  JoinFailureCode.serverNotListed,
  JoinFailureCode.descriptorInvalid,
  JoinFailureCode.directNotAllowed,
  JoinFailureCode.identityChanged,
  JoinFailureCode.gameBuildMismatch,
  JoinFailureCode.runtimeOutdated,
  JoinFailureCode.admissionBlocked,
  JoinFailureCode.standaloneOnServer,
  JoinFailureCode.hostDownloadUnsupported,
  JoinFailureCode.kicked,
  JoinFailureCode.protocolMismatch,
};

/// The event a stage answers with to fail as [code].
JoinEvent _failure(JoinFailureCode code) => switch (code) {
  JoinFailureCode.installFailed => const JoinFailed.installFailed(
    rollbackComplete: true,
    detail: 'The install could not finish. Your mods are as they were.',
  ),
  JoinFailureCode.gameRunning => const JoinFailed.gameRunning(
    ownership: JoinGameOwnership.foreign,
    detail: 'Robotopia is running. Close the game, then join again.',
  ),
  JoinFailureCode.identityChanged => JoinFailed.identityChanged(
    servedServerId: _hostB,
  ),
  _ => JoinFailed(code, detail: 'What happened, in plain words.'),
};

/// A join that fails as [code] at [stage]: a typed address on the local
/// network for an identity change, a server link otherwise.
Future<(LauncherBloc, JoinFailure)> _failAt(
  JoinStage stage,
  JoinFailureCode code,
) async {
  final gateway = _JoinGateway();
  final repository = _JoinRepository(install: _install);
  final bloc = await _startedBloc(repository, gateway);
  switch (stage) {
    case JoinStage.launching:
      gateway.launch = (_) => JoinLaunchRefused(_failure(code) as JoinFailed);
    case JoinStage.connecting || JoinStage.synchronizing:
      break;
    default:
      gateway.answers[stage] = (_) => _failure(code);
  }
  final target = code == JoinFailureCode.identityChanged
      ? JoinAddressTarget(host: '192.168.1.20', port: 27250)
      : JoinLinkTarget(_serverLink);
  bloc.add(MultiplayerJoinRequested(target));
  if (stage.index > JoinStage.awaitingConsent.index) {
    await _atStage(bloc, JoinStage.awaitingConsent);
    bloc.add(MultiplayerJoinConsented(flow: _flowOf(bloc)!.id));
  }
  if (stage == JoinStage.connecting || stage == JoinStage.synchronizing) {
    await _launched(bloc);
    final failure = LaunchMultiplayerFailure.values.firstWhere(
      (item) => item.joinFailureCode == code,
    );
    if (stage == JoinStage.synchronizing) {
      repository.report(_activity(state: LaunchSessionState.synchronizing));
      await _atStage(bloc, JoinStage.synchronizing);
    } else {
      repository.report(_activity(state: LaunchSessionState.connecting));
      await _atStage(bloc, JoinStage.connecting);
    }
    repository.report(
      _activity(failure: failure, detail: 'The game said why.'),
    );
    if (code == JoinFailureCode.admissionChanged) {
      // The first change plans the join once more, on its own; only a
      // second one ends it.
      await _until(
        bloc,
        (state) =>
            _flowOf(bloc)!.join!.replans == 1 && _flowOf(bloc)!.launch != null,
      );
      repository.report(_activity(state: LaunchSessionState.connecting));
      await _atStage(bloc, JoinStage.connecting);
      repository.report(_activity(failure: failure, detail: 'Changed again.'));
    }
  }
  return (bloc, await _failed(bloc));
}

void registerJoinFailureTests() {
  group('Every failure the reducer has ends the join with its card', () {
    for (final MapEntry(key: stage, value: codes)
        in JoinSession.failuresByStage.entries) {
      for (final code in codes) {
        test('${stage.name}: ${code.name}', () async {
          final (bloc, failure) = await _failAt(stage, code);
          expect(failure.code, code);
          expect(failure.stage, stage);
          final text = JoinFailureText.of(failure);
          expect(text.title, isNotEmpty);
          expect(text.bodyFor(failure), isNotEmpty);
          expect(text.retry, !_noRetry.contains(code), reason: code.name);
          expect(
            bloc.state.statusMessage,
            'The join stopped: ${text.title.toLowerCase()}.',
          );
          expect(bloc.state.statusSeverity, IssueSeverity.error);
          expect(_flowOf(bloc)!.showsDialog, isTrue);
        });
      }
    }

    test('an install whose rollback did not finish offers no Retry', () async {
      final server = await _server();
      final failure = JoinSession.reduce(
        [
          JoinDescriptorVerified(server),
          JoinPlanReady(_plan(server)),
          const JoinConsentGiven(),
          const JoinDownloadsCompleted(),
        ].fold(
          JoinSession.start(
            JoinLinkTarget(
              SessionLink.direct(
                serverId: _serverId,
                host: '192.168.1.20',
                port: 27250,
              ),
            ),
            privacy: SessionPrivacy.relayOnly,
          ),
          JoinSession.reduce,
        ),
        const JoinFailed.installFailed(rollbackComplete: false),
      ).outcome!.failure!;
      final text = JoinFailureText.of(failure);
      expect(text.retry, isFalse);
      expect(text.nextStep, contains('Restart the launcher'));
    });

    test('a game that exits ends the join, or the session it began', () async {
      final (bloc, _, repository) = await _atConsent();
      bloc.add(MultiplayerJoinConsented(flow: _flowOf(bloc)!.id));
      await _launched(bloc);
      repository.report(_activity(exited: true));
      final failure = await _failed(bloc);
      expect(failure.code, JoinFailureCode.gameExited);
      expect(failure.stage, JoinStage.launching);
      expect(JoinFailureText.of(failure).retry, isTrue);

      final (other, _, again) = await _atConsent();
      other.add(MultiplayerJoinConsented(flow: _flowOf(other)!.id));
      await _launched(other);
      again.report(_activity(succeeded: true));
      await _atStage(other, JoinStage.inSession);
      again.report(_activity(succeeded: true, exited: true));
      await _until(other, (state) => state.multiplayer.join.flow == null);
      expect(
        other.state.statusMessage,
        'Robotopia closed, so the session ended.',
      );
    });

    test('a launch the game could not run ends the join as a failed start, '
        'without addresses', () async {
      final (bloc, _, repository) = await _atConsent();
      bloc.add(MultiplayerJoinConsented(flow: _flowOf(bloc)!.id));
      await _launched(bloc);
      repository.report(
        _activity(
          error: LaunchOperationError(
            code: 'unavailable',
            message: 'The provider at $_planted is not loaded.',
          ),
        ),
      );
      final failure = await _failed(bloc);
      expect(failure.code, JoinFailureCode.startFailed);
      expect(failure.detail, contains('is not loaded'));
      expect(failure.detail, isNot(contains(_planted)));
    });

    test(
      'a typed address that answers as another server can be trusted',
      () async {
        final (bloc, failure) = await _failAt(
          JoinStage.fetchingDescriptor,
          JoinFailureCode.identityChanged,
        );
        expect(failure.servedServerId, _hostB);
        final flow = _flowOf(bloc)!;

        bloc.add(MultiplayerJoinTrusted(flow.id));
        await _until(
          bloc,
          (state) =>
              _flowOf(bloc)?.trustedServerId == _hostB &&
              _flowOf(bloc)?.join?.isEnded == false,
        );
        expect(_flowOf(bloc)!.join!.serverId, _hostB);
      },
    );

    test(
      'a join that needs a direct connection asks, then joins with it',
      () async {
        final (bloc, failure) = await _failAt(
          JoinStage.fetchingDescriptor,
          JoinFailureCode.directNotAllowed,
        );
        expect(
          JoinFailureText.of(failure).action,
          JoinFailureAction.allowDirect,
        );
        final flow = _flowOf(bloc)!;

        bloc.add(MultiplayerJoinDirectAsked(flow.id));
        await _until(bloc, (state) => _flowOf(bloc)!.question != null);
        // No keeps the card.
        bloc.add(MultiplayerJoinQuestionAnswered(flow: flow.id, yes: false));
        await _until(bloc, (state) => _flowOf(bloc)!.question == null);
        expect(_flowOf(bloc)!.join!.outcome!.failure, same(failure));

        bloc.add(MultiplayerJoinDirectAsked(flow.id));
        await _until(bloc, (state) => _flowOf(bloc)!.question != null);
        bloc.add(MultiplayerJoinQuestionAnswered(flow: flow.id, yes: true));
        await _until(
          bloc,
          (state) =>
              _flowOf(bloc)!.join?.privacy == MultiplayerAddressPrivacy.direct,
        );
      },
    );

    test("an answer a step does not take ends the join with that step's "
        'failure instead of leaving it waiting', () async {
      final gateway = _JoinGateway();
      gateway.answers[JoinStage.downloading] = (_) => JoinFailed(
        JoinFailureCode.contentUnavailable,
        detail: 'Only planning fails this way.',
      );
      final (bloc, _, _) = await _atConsent(gateway: gateway);
      bloc.add(MultiplayerJoinConsented(flow: _flowOf(bloc)!.id));
      final failure = await _failed(bloc);
      expect(failure.code, JoinFailureCode.downloadFailed);
      expect(failure.stage, JoinStage.downloading);
      expect(gateway.intents, isEmpty);
    });

    test('Retry starts a new attempt, and an older attempt\'s answer is '
        'dropped', () async {
      final gateway = _JoinGateway();
      final late = gateway.hold(JoinStage.downloading);
      final (bloc, _, _) = await _atConsent(gateway: gateway);
      bloc.add(MultiplayerJoinConsented(flow: _flowOf(bloc)!.id));
      await _atStage(bloc, JoinStage.downloading);
      bloc.add(MultiplayerJoinCancelled(_flowOf(bloc)!.id));
      await _until(bloc, (state) => _flowOf(bloc)!.join!.isEnded);
      gateway.held.clear();

      bloc.add(MultiplayerJoinRetried(_flowOf(bloc)!.id));
      await _atStage(bloc, JoinStage.awaitingConsent);
      // The cancelled attempt's download finishing now changes nothing.
      if (!late.isCompleted) late.complete(const JoinDownloadsCompleted());
      await pumpEventQueue();
      expect(_flowOf(bloc)!.join!.stage, JoinStage.awaitingConsent);
      expect(gateway.intents, isEmpty);
    });
  });

  group('Cancel works at every step', () {
    for (final stage in [
      JoinStage.resolving,
      JoinStage.fetchingDescriptor,
      JoinStage.checking,
      JoinStage.downloading,
    ]) {
      test('${stage.name} stops at once', () async {
        final gateway = _JoinGateway();
        final work = gateway.hold(stage);
        final bloc = await _startedBloc(
          _JoinRepository(install: _install),
          gateway,
        );
        bloc.add(MultiplayerJoinRequested(JoinLinkTarget(_serverLink)));
        if (stage == JoinStage.downloading) {
          await _atStage(bloc, JoinStage.awaitingConsent);
          bloc.add(MultiplayerJoinConsented(flow: _flowOf(bloc)!.id));
        }
        await _atStage(bloc, stage);

        bloc.add(MultiplayerJoinCancelled(_flowOf(bloc)!.id));
        final failure = await _failed(bloc);

        expect(failure.code, JoinFailureCode.cancelled);
        expect(failure.stage, stage);
        expect(gateway.tokens[stage]!.isCancelled, isTrue);
        expect(work.isCompleted, isTrue);
        expect(bloc.state.statusMessage, 'The join was cancelled.');
        expect(JoinFailureText.of(failure).retry, isTrue);
      });
    }

    test('awaitingConsent stops at once', () async {
      final (bloc, _, _) = await _atConsent();
      bloc.add(MultiplayerJoinCancelled(_flowOf(bloc)!.id));
      expect((await _failed(bloc)).code, JoinFailureCode.cancelled);
    });

    for (final stage in [JoinStage.connecting, JoinStage.synchronizing]) {
      test(
        '${stage.name} stops following, and says the game may go on',
        () async {
          final (bloc, _, repository) = await _atConsent();
          bloc.add(MultiplayerJoinConsented(flow: _flowOf(bloc)!.id));
          await _launched(bloc);
          repository.report(
            _activity(
              state: stage == JoinStage.connecting
                  ? LaunchSessionState.connecting
                  : LaunchSessionState.synchronizing,
            ),
          );
          await _atStage(bloc, stage);

          bloc.add(MultiplayerJoinCancelled(_flowOf(bloc)!.id));
          final failure = await _failed(bloc);

          expect(failure.code, JoinFailureCode.cancelled);
          final text = JoinFailureText.of(failure, startedGame: true);
          expect(text.sentence, contains('may still enter the server'));
        },
      );
    }

    test(
      'installing finishes undoing first, then the join ends cancelled',
      () async {
        final gateway = _JoinGateway();
        gateway.hold(JoinStage.installing);
        final (bloc, _, _) = await _atConsent(gateway: gateway);
        bloc.add(MultiplayerJoinConsented(flow: _flowOf(bloc)!.id));
        await _atStage(bloc, JoinStage.installing);

        bloc.add(MultiplayerJoinCancelled(_flowOf(bloc)!.id));
        final failure = await _failed(bloc);

        expect(gateway.tokens[JoinStage.installing]!.isCancelled, isTrue);
        expect(failure.code, JoinFailureCode.cancelled);
        expect(failure.stage, JoinStage.installing);
      },
    );

    test(
      'preparing the profile finishes first, then the join ends cancelled',
      () async {
        final gateway = _JoinGateway();
        final saving = gateway.hold(JoinStage.preparingProfile);
        final (bloc, _, _) = await _atConsent(gateway: gateway);
        bloc.add(MultiplayerJoinConsented(flow: _flowOf(bloc)!.id));
        await _atStage(bloc, JoinStage.preparingProfile);

        bloc.add(MultiplayerJoinCancelled(_flowOf(bloc)!.id));
        await _until(bloc, (state) => _flowOf(bloc)!.join!.cancelRequested);
        expect(bloc.state.statusMessage, contains('once this step finishes'));
        expect(_flowOf(bloc)!.join!.isEnded, isFalse);
        saving.complete(const JoinProfilePrepared());
        final failure = await _failed(bloc);

        expect(failure.code, JoinFailureCode.cancelled);
        expect(gateway.calls, isNot(contains('launch')));
      },
    );

    test(
      'launching: a live join the game has not taken is withdrawn',
      () async {
        final gateway = _JoinGateway()..launchGate = Completer<JoinLaunch>();
        final (bloc, _, _) = await _atConsent(gateway: gateway);
        bloc.add(MultiplayerJoinConsented(flow: _flowOf(bloc)!.id));
        await _atStage(bloc, JoinStage.launching);

        bloc.add(MultiplayerJoinCancelled(_flowOf(bloc)!.id));
        await _until(bloc, (state) => _flowOf(bloc)!.join!.cancelRequested);
        gateway.launchGate!.complete(_started(live: true, requestId: 'live-2'));
        final failure = await _failed(bloc);

        expect(failure.code, JoinFailureCode.cancelled);
        expect(gateway.withdrawn.single.requestId, 'live-2');
        expect(_flowOf(bloc)!.launch, isNull, reason: 'the game never had it');
      },
    );

    test('launching: a game that already started keeps the join, and the '
        'card says so', () async {
      final gateway = _JoinGateway();
      final (bloc, _, _) = await _atConsent(gateway: gateway);
      bloc.add(MultiplayerJoinConsented(flow: _flowOf(bloc)!.id));
      await _launched(bloc);
      expect(_flowOf(bloc)!.launch, isNotNull);

      bloc.add(MultiplayerJoinCancelled(_flowOf(bloc)!.id));
      final failure = await _failed(bloc);

      expect(failure.code, JoinFailureCode.cancelled);
      expect(gateway.withdrawn, isEmpty);
      expect(_flowOf(bloc)!.launch, isNotNull);
    });
  });
}
