part of 'widget_test.dart';

/// The bloc over a fake launcher and [gateway], started, and closed when the
/// test ends.
LauncherBloc _multiplayerBloc(
  _FakeMultiplayerGateway gateway, {
  _FakeLauncherRepository? repository,
  SessionLinkHandlingGateway? links,
  bool start = true,
}) {
  final bloc = LauncherBloc(
    repository ?? _FakeLauncherRepository(snapshot: _readySnapshot()),
    multiplayer: gateway,
    sessionLinkHandling: links,
  );
  if (start) bloc.add(const LauncherStarted());
  addTearDown(bloc.close);
  return bloc;
}

/// Lets the bloc and the work beside it run until nothing is left to do.
Future<void> _idle() => pumpEventQueue(times: 60);

MultiplayerPreferences _named(String name, {int networkNotice = 0}) =>
    MultiplayerPreferences(
      settings: MultiplayerSettings(displayName: name),
      networkNotice: networkNotice,
    );

void registerMultiplayerBlocTests() {
  MultiplayerSettingsView view(LauncherBloc bloc) =>
      bloc.state.multiplayer.settings;

  group('Settings → Multiplayer, in the bloc', () {
    test('at start the preferences load, the game is checked once and a '
        'round takes in what changed in game', () async {
      final gateway = _FakeMultiplayerGateway()
        ..round = (current) =>
            MultiplayerPreferencesSynced(_named('Robin'), changedInGame: true);
      final bloc = _multiplayerBloc(gateway);
      await _idle();
      expect(gateway.calls, ['load', 'game', 'round']);
      expect(view(bloc).phase, MultiplayerPreferencesPhase.ready);
      expect(view(bloc).lock, MultiplayerPreferencesLock.none);
      expect(view(bloc).syncing, isFalse);
      expect(view(bloc).preferences!.settings.displayName, 'Robin');
      expect(
        bloc.state.statusMessage,
        'Took in the multiplayer settings you changed in Robotopia.',
      );
    });

    test('while the game runs, what it can change is read-only and nothing '
        'is taken in', () async {
      final gateway = _FakeMultiplayerGateway()..running = GameRunState.running;
      final bloc = _multiplayerBloc(gateway);
      await _idle();
      expect(gateway.calls, ['load', 'game']);
      expect(view(bloc).lock, MultiplayerPreferencesLock.gameRunning);
      expect(view(bloc).editable, isFalse);
      expect(view(bloc).linksEditable, isTrue);

      for (final change in const [
        MultiplayerPreferencesChange(displayName: 'Robin'),
        MultiplayerPreferencesChange(acknowledgeDisclosure: true),
        MultiplayerPreferencesChange(hostingPort: 27300),
        MultiplayerPreferencesChange(upnp: true),
        MultiplayerPreferencesChange(lanDiscovery: false),
        MultiplayerPreferencesChange(voiceConsent: true),
      ]) {
        bloc.add(MultiplayerPreferencesEdited(change));
        await _idle();
        expect(bloc.state.statusMessage, contains('Robotopia is running'));
      }
      expect(gateway.calls, ['load', 'game']);

      // What the game never changes still saves.
      bloc.add(const MultiplayerNetworkNoticeRead());
      await _idle();
      expect(gateway.calls.last, 'change notice');

      // Closed, the game's changes are taken in and the lock lifts.
      gateway.running = GameRunState.notRunning;
      bloc.add(const MultiplayerGameCheckRequested());
      await _idle();
      expect(gateway.calls.sublist(gateway.calls.length - 2), [
        'game',
        'round',
      ]);
      expect(view(bloc).lock, MultiplayerPreferencesLock.none);
    });

    test('a game that cannot be checked counts as running', () async {
      final gateway = _FakeMultiplayerGateway()..running = GameRunState.unknown;
      final bloc = _multiplayerBloc(gateway);
      await _idle();
      expect(view(bloc).lock, MultiplayerPreferencesLock.gameMaybeRunning);
      expect(gateway.calls, isNot(contains('round')));
      bloc.add(
        const MultiplayerPreferencesEdited(
          MultiplayerPreferencesChange(displayName: 'Robin'),
        ),
      );
      await _idle();
      expect(bloc.state.statusMessage, contains('could not tell'));
      expect(gateway.calls, isNot(contains(startsWith('change'))));
    });

    test('a change asks about the game first, and one that started since '
        'refuses it', () async {
      final gateway = _FakeMultiplayerGateway();
      final bloc = _multiplayerBloc(gateway);
      await _idle();
      expect(view(bloc).lock, MultiplayerPreferencesLock.none);
      gateway
        ..calls.clear()
        ..running = GameRunState.running;
      bloc.add(
        const MultiplayerPreferencesEdited(
          MultiplayerPreferencesChange(displayName: 'Robin'),
        ),
      );
      await _idle();
      expect(gateway.calls, ['game']);
      expect(view(bloc).lock, MultiplayerPreferencesLock.gameRunning);
      expect(view(bloc).saving, isFalse);
      expect(view(bloc).preferences!.settings.displayName, 'Sam');
      expect(bloc.state.statusSeverity, IssueSeverity.warning);
    });

    test('a saved change brings the game its copy with a round', () async {
      final gateway = _FakeMultiplayerGateway();
      final bloc = _multiplayerBloc(gateway);
      await _idle();
      gateway.calls.clear();
      bloc.add(
        const MultiplayerPreferencesEdited(
          MultiplayerPreferencesChange(displayName: 'Robin', upnp: true),
        ),
      );
      expect((await bloc.stream.first).multiplayer.settings.saving, isTrue);
      await _idle();
      expect(gateway.calls, ['game', 'change name Robin, upnp true', 'round']);
      expect(view(bloc).preferences!.settings.displayName, 'Robin');
      expect(view(bloc).preferences!.settings.upnpEnabled, isTrue);
      expect(view(bloc).saving, isFalse);
      expect(bloc.state.statusMessage, 'Multiplayer settings saved.');
    });

    test('without an install the preferences change, with no round', () async {
      final gateway = _FakeMultiplayerGateway();
      final bloc = _multiplayerBloc(
        gateway,
        repository: _FakeLauncherRepository(),
      );
      await _idle();
      expect(gateway.calls, ['load']);
      expect(view(bloc).lock, MultiplayerPreferencesLock.none);
      bloc.add(
        const MultiplayerPreferencesEdited(
          MultiplayerPreferencesChange(voiceConsent: true),
        ),
      );
      await _idle();
      expect(gateway.calls, ['load', 'change consent true']);
      expect(view(bloc).preferences!.settings.voiceActive, isTrue);
      bloc.add(
        const MultiplayerPreferencesEdited(
          MultiplayerPreferencesChange(voiceConsent: false),
        ),
      );
      await _idle();
      expect(view(bloc).preferences!.settings.hasVoiceConsent, isFalse);
      expect(bloc.state.statusMessage, contains('Voice chat is off'));
    });

    test('settings that cannot be read show nothing, change nothing, and '
        'read again on Try again', () async {
      final gateway = _FakeMultiplayerGateway()..unreadable = true;
      final bloc = _multiplayerBloc(gateway);
      await _idle();
      // No round runs on stand-ins.
      expect(gateway.calls, ['load', 'game']);
      expect(view(bloc).phase, MultiplayerPreferencesPhase.unreadable);
      expect(view(bloc).preferences, isNull);
      expect(bloc.state.statusMessage, contains('could not be read'));
      bloc.add(
        const MultiplayerPreferencesEdited(
          MultiplayerPreferencesChange(displayName: 'Robin'),
        ),
      );
      await _idle();
      expect(gateway.calls, isNot(contains(startsWith('change'))));

      gateway.unreadable = false;
      bloc.add(const MultiplayerPreferencesRefreshRequested());
      expect(
        (await bloc.stream.first).multiplayer.settings.phase,
        MultiplayerPreferencesPhase.loading,
      );
      await _idle();
      expect(view(bloc).phase, MultiplayerPreferencesPhase.ready);
      expect(view(bloc).preferences!.settings.displayName, 'Sam');
      expect(gateway.calls.where((call) => call == 'round'), hasLength(1));
    });

    test(
      'a change the store cannot read or write is refused plainly',
      () async {
        final gateway = _FakeMultiplayerGateway();
        final bloc = _multiplayerBloc(gateway);
        await _idle();
        gateway.failSaves = true;
        bloc.add(
          const MultiplayerPreferencesEdited(
            MultiplayerPreferencesChange(displayName: 'Robin'),
          ),
        );
        await _idle();
        expect(view(bloc).problem, contains('could not be saved'));
        expect(view(bloc).preferences!.settings.displayName, 'Sam');
        expect(bloc.state.statusSeverity, IssueSeverity.error);

        gateway
          ..failSaves = false
          ..unreadable = true;
        bloc.add(
          const MultiplayerPreferencesEdited(
            MultiplayerPreferencesChange(displayName: 'Robin'),
          ),
        );
        await _idle();
        expect(view(bloc).phase, MultiplayerPreferencesPhase.unreadable);
        expect(view(bloc).preferences, isNull);
      },
    );

    test('a change waits while another is saved', () async {
      final gateway = _FakeMultiplayerGateway();
      final bloc = _multiplayerBloc(gateway);
      await _idle();
      gateway.changeGate = Completer<void>();
      bloc.add(
        const MultiplayerPreferencesEdited(
          MultiplayerPreferencesChange(upnp: true),
        ),
      );
      await _idle();
      bloc.add(
        const MultiplayerPreferencesEdited(
          MultiplayerPreferencesChange(upnp: false),
        ),
      );
      await _idle();
      expect(bloc.state.statusMessage, contains('still being saved'));
      gateway.changeGate!.complete();
      await _idle();
      expect(gateway.calls.where((call) => call.startsWith('change')), [
        'change upnp true',
      ]);
      expect(view(bloc).preferences!.settings.upnpEnabled, isTrue);
    });

    test('an older read is stale and dropped', () async {
      final gateway = _FakeMultiplayerGateway()..hold = true;
      final bloc = _multiplayerBloc(gateway);
      await _idle();
      bloc.add(const MultiplayerPreferencesRefreshRequested(checkGame: false));
      await _idle();
      expect(gateway.heldLoads, hasLength(2));
      gateway.heldLoads[1].complete(_named('Newer'));
      await _idle();
      gateway.heldLoads[0].complete(_named('Older'));
      await _idle();
      expect(view(bloc).preferences!.settings.displayName, 'Newer');

      // Answers from no current generation change nothing either.
      bloc
        ..add(MultiplayerPreferencesLoaded(-1, _named('Stale')))
        ..add(
          const MultiplayerGameChecked(
            -1,
            'C:\\Games\\Robotopia',
            GameRunState.running,
          ),
        )
        ..add(
          MultiplayerPreferencesRoundFinished(
            -1,
            CancellationToken(),
            MultiplayerPreferencesSynced(_named('Stale'), changedInGame: true),
          ),
        );
      await _idle();
      expect(view(bloc).preferences!.settings.displayName, 'Newer');
      expect(view(bloc).lock, isNot(MultiplayerPreferencesLock.gameRunning));
    });

    test('an older game check is stale and dropped', () async {
      final gateway = _FakeMultiplayerGateway()..hold = true;
      final bloc = _multiplayerBloc(gateway);
      await _idle();
      gateway.heldLoads.single.complete(_named('Sam'));
      bloc.add(const MultiplayerGameCheckRequested());
      await _idle();
      expect(gateway.heldGames, hasLength(2));
      gateway.heldGames[1].complete(GameRunState.notRunning);
      await _idle();
      gateway.heldGames[0].complete(GameRunState.running);
      await _idle();
      expect(view(bloc).lock, MultiplayerPreferencesLock.none);
      expect(gateway.heldRounds, hasLength(1));
    });

    test('a game that starts cancels a round before it writes, and its '
        'answer is dropped', () async {
      final gateway = _FakeMultiplayerGateway()..hold = true;
      final bloc = _multiplayerBloc(gateway);
      await _idle();
      gateway.heldLoads.single.complete(_named('Sam'));
      gateway.heldGames.single.complete(GameRunState.notRunning);
      await _idle();
      expect(view(bloc).syncing, isTrue);
      final token = gateway.roundTokens.single!;
      bloc.add(const MultiplayerGameCheckRequested());
      await _idle();
      gateway.heldGames.last.complete(GameRunState.running);
      await _idle();
      expect(token.isCancelled, isTrue);
      expect(view(bloc).lock, MultiplayerPreferencesLock.gameRunning);
      gateway.heldRounds.single.complete(
        MultiplayerPreferencesSynced(_named('Robin'), changedInGame: true),
      );
      await _idle();
      expect(view(bloc).syncing, isFalse);
      expect(view(bloc).preferences!.settings.displayName, 'Sam');
      expect(bloc.state.statusMessage, isNot(contains('Took in')));
    });

    test(
      'a round that fails says so, and the next check runs it again',
      () async {
        final gateway = _FakeMultiplayerGateway()
          ..round = (current) => MultiplayerPreferencesRoundFailed(
            current,
            MultiplayerRoundProblem.gameCopyNotWritten,
          );
        final bloc = _multiplayerBloc(gateway);
        await _idle();
        expect(view(bloc).problem, startsWith("Robotopia's copy"));
        expect(bloc.state.statusSeverity, IssueSeverity.warning);
        gateway.round = null;
        bloc.add(const MultiplayerGameCheckRequested());
        await _idle();
        expect(gateway.calls.where((call) => call == 'round'), hasLength(2));
        expect(view(bloc).problem, isNull);
      },
    );

    test('a game this launcher starts locks the preferences, and its exit '
        'takes in its changes', () async {
      final profile = LauncherProfile.defaultProfile().copyWith(
        launchSelection: LaunchSelection.target(
          LaunchRequest(targetId: _targetId),
        ),
      );
      final repository = _FakeLauncherRepository(
        snapshot: _readySnapshot(
          profiles: [profile],
          installedMods: _declaredLaunchMods(),
        ),
      );
      final started = _activity(profile);
      repository.launchResult = LaunchResult(
        started: true,
        message: 'Process started.',
        requestId: started.requestId,
        latestActivity: started,
      );
      final gateway = _FakeMultiplayerGateway();
      final bloc = _multiplayerBloc(gateway, repository: repository);
      await _waitLaunchState(bloc, () => bloc.state.launchPreview != null);
      await _idle();
      gateway
        ..calls.clear()
        ..running = GameRunState.running;
      bloc.add(const GameLaunchRequested());
      await _idle();
      expect(gateway.calls, ['game']);
      expect(view(bloc).lock, MultiplayerPreferencesLock.gameRunning);

      gateway
        ..running = GameRunState.notRunning
        ..round = (current) =>
            MultiplayerPreferencesSynced(_named('Robin'), changedInGame: true);
      repository.activityController.add(started.copyWith(processExited: true));
      await _idle();
      expect(gateway.calls, ['game', 'game', 'round']);
      expect(view(bloc).lock, MultiplayerPreferencesLock.none);
      expect(view(bloc).preferences!.settings.displayName, 'Robin');
    });

    test('opening Settings checks the game again', () async {
      final gateway = _FakeMultiplayerGateway();
      final bloc = _multiplayerBloc(gateway);
      await _idle();
      gateway.calls.clear();
      bloc.add(const LauncherSectionSelected(LauncherSection.settings));
      await _idle();
      expect(gateway.calls, ['game']);
      bloc.add(const LauncherSectionSelected(LauncherSection.mods));
      await _idle();
      expect(gateway.calls, ['game']);
    });

    test('a link choice shows as saved, even while the game runs', () async {
      final gateway = _FakeMultiplayerGateway()..running = GameRunState.running;
      final links = _FakeSessionLinkHandling();
      final bloc = _multiplayerBloc(gateway, links: links);
      await _idle();
      bloc.add(const SessionLinkHandlingChanged(false));
      await _idle();
      expect(links.calls, ['start', 'set false']);
      expect(view(bloc).preferences!.settings.linkHandlingEnabled, isFalse);
      expect(bloc.state.multiplayer.linkHandling!.enabled, isFalse);
    });

    test('closing the bloc lets go of the gateway', () async {
      final gateway = _FakeMultiplayerGateway();
      final bloc = _multiplayerBloc(gateway);
      await _idle();
      await bloc.close();
      expect(gateway.calls.last, 'dispose');
    });
  });
}
