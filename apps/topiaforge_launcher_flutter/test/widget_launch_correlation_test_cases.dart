part of 'widget_test.dart';

void _registerLaunchCorrelationTests() {
  Future<LauncherBloc> launch(_FakeLauncherRepository fake) async {
    final bloc = LauncherBloc(fake)..add(const LauncherStarted());
    await _waitLaunchState(bloc, () => bloc.state.launchPreview != null);
    bloc.add(const GameLaunchRequested());
    await _waitLaunchState(
      bloc,
      () => fake.launchedProfiles.isNotEmpty && !bloc.state.isBusy,
    );
    return bloc;
  }

  test(
    'a missing receipt cannot accept an unsolicited first activity',
    () async {
      final profile = LauncherProfile.defaultProfile();
      final fake = _FakeLauncherRepository(
        snapshot: _readySnapshot(profiles: [profile]),
      );
      fake.launchResult = const LaunchResult(
        started: true,
        message: 'Process started.',
        requestId: 'request-one',
      );
      final bloc = await launch(fake);
      fake.activityController.add(
        _activity(profile).applyOutcome(
          LaunchOutcome(
            kind: 'launch',
            requestId: 'request-one',
            command: 'launch-target',
            sessionId: 'wrong',
            sequence: 1,
            status: 'succeeded',
            phase: 'running',
          ),
        ),
      );
      await Future<void>.delayed(const Duration(milliseconds: 30));
      expect(bloc.state.launchActivity, isNull);
      expect(bloc.state.statusMessage, contains('unconfirmed'));
      await bloc.close();
    },
  );
  for (final field in ['pid', 'start', 'executable', 'nativeToken']) {
    test('activity with changed process $field is ignored', () async {
      final profile = LauncherProfile.defaultProfile();
      final process = LaunchProcessIdentity(
        pid: 7,
        startTimeUtc: DateTime.utc(2026),
        executablePath: 'fixture/game.exe',
        nativeStartToken: 'native-one',
      );
      final base = _activity(profile).copyWith(process: process);
      final fake = _FakeLauncherRepository(
        snapshot: _readySnapshot(profiles: [profile]),
      );
      fake.launchResult = LaunchResult(
        started: true,
        message: 'Process started.',
        requestId: base.requestId,
        latestActivity: base,
      );
      final bloc = await launch(fake);
      fake.activityController.add(
        base
            .copyWith(
              process: LaunchProcessIdentity(
                pid: field == 'pid' ? 8 : process.pid,
                startTimeUtc: field == 'start'
                    ? DateTime.utc(2025)
                    : process.startTimeUtc,
                executablePath: field == 'executable'
                    ? 'other/game.exe'
                    : process.executablePath,
                nativeStartToken: field == 'nativeToken'
                    ? 'native-two'
                    : process.nativeStartToken,
              ),
            )
            .applyOutcome(
              LaunchOutcome(
                kind: 'launch',
                requestId: base.requestId,
                command: 'launch-target',
                sessionId: 'wrong',
                sequence: 1,
                status: 'succeeded',
                phase: 'running',
              ),
            ),
      );
      await Future<void>.delayed(const Duration(milliseconds: 30));
      expect(bloc.state.launchActivity!.launchOutcome, isNull);
      await bloc.close();
    });
  }
  test(
    'main menu acknowledgement needs no terminal session record after process exit',
    () async {
      final profile = LauncherProfile.defaultProfile();
      final base = LaunchActivity(
        requestId: 'menu-request',
        profileId: profile.id,
        profileRevision: profile.revision,
        installIdentity: 'menu-install',
        packageDigest: 'menu-digest',
        command: 'main-menu',
      );
      final fake = _FakeLauncherRepository(
        snapshot: _readySnapshot(profiles: [profile]),
      );
      fake.launchResult = LaunchResult(
        started: true,
        message: 'Process started.',
        requestId: base.requestId,
        latestActivity: base,
      );
      final bloc = await launch(fake);
      fake.activityController.add(
        base
            .applyOutcome(
              LaunchOutcome(
                kind: 'launch',
                requestId: base.requestId,
                command: 'main-menu',
                sequence: 1,
                status: 'succeeded',
                phase: 'idle',
              ),
            )
            .copyWith(processExited: true),
      );
      await _waitLaunchState(
        bloc,
        () => bloc.state.launchActivity!.acknowledged,
      );
      expect(bloc.state.statusMessage, contains('Main menu confirmed'));
      expect(bloc.state.statusMessage, isNot(contains('unconfirmed')));
      await bloc.close();
    },
  );
  test(
    'queued activity for old install cannot claim a selected new install',
    () async {
      final profile = LauncherProfile.defaultProfile();
      final base = _activity(profile);
      final fake = _FakeLauncherRepository(
        snapshot: _readySnapshot(profiles: [profile]),
      );
      fake.launchResult = LaunchResult(
        started: true,
        message: 'Process started.',
        requestId: base.requestId,
        latestActivity: base,
      );
      final bloc = await launch(fake);
      fake.onGameInstallSelected(
        const GameInstall(
          path: 'other-game',
          executablePath: 'other-game/game.exe',
          bepInExStatus: ComponentState.ready,
          loaderStatus: ComponentState.ready,
        ),
      );
      bloc.add(const LauncherRefreshRequested());
      fake.activityController.add(
        base.applyOutcome(
          LaunchOutcome(
            kind: 'launch',
            requestId: base.requestId,
            command: 'launch-target',
            sessionId: 'old-session',
            sequence: 1,
            status: 'succeeded',
            phase: 'running',
          ),
        ),
      );
      await _waitLaunchState(
        bloc,
        () => bloc.state.gameInstall?.path == 'other-game',
      );
      await Future<void>.delayed(const Duration(milliseconds: 30));
      expect(bloc.state.launchActivity, isNull);
      expect(bloc.state.statusMessage, isNot('Session is running.'));
      await bloc.close();
    },
  );
  for (final failure in ['preview', 'save']) {
    test(
      'launch selection $failure error is visible without saving or launching',
      () async {
        final profile = LauncherProfile.defaultProfile();
        final fake = _FakeLauncherRepository(
          snapshot: _readySnapshot(profiles: [profile]),
        );
        final bloc = LauncherBloc(fake)..add(const LauncherStarted());
        await _waitLaunchState(bloc, () => bloc.state.launchPreview != null);
        if (failure == 'preview') {
          fake.previewFailure = StateError('Fixture preview unreadable.');
        }
        if (failure == 'save') {
          fake.saveFailure = StateError('Fixture profile unwritable.');
        }
        bloc.add(const LaunchSelectionChanged(LaunchSelection.mainMenu()));
        await _waitLaunchState(bloc, () => bloc.state.errorMessage != null);
        expect(bloc.state.errorMessage, contains('Fixture'));
        expect(bloc.state.isBusy, isFalse);
        expect(fake.savedProfiles, isEmpty);
        expect(fake.launchedProfiles, isEmpty);
        await bloc.close();
      },
    );
  }
  test(
    'missing selected profile cannot fall back to the first profile',
    () async {
      final fake = _FakeLauncherRepository(
        snapshot: _readySnapshot(selectedProfileId: 'missing'),
      );
      final bloc = LauncherBloc(fake)..add(const LauncherStarted());
      await _waitLaunchState(bloc, () => !bloc.state.isBusy);
      expect(bloc.state.selectedProfile, isNull);
      expect(bloc.state.canStartLaunchFlow, isFalse);
      bloc.add(const GameLaunchRequested());
      await Future<void>.delayed(const Duration(milliseconds: 30));
      expect(fake.launchedProfiles, isEmpty);
      await bloc.close();
    },
  );
  test(
    'forbidden override remains a visible error without changing profile',
    () async {
      final profile = LauncherProfile.defaultProfile().copyWith(
        launchSelection: LaunchSelection.target(
          LaunchRequest(targetId: _targetId),
        ),
      );
      final fake = _FakeLauncherRepository(
        snapshot: _readySnapshot(
          profiles: [profile],
          installedMods: _declaredLaunchMods(),
        ),
      );
      final bloc = LauncherBloc(fake)..add(const LauncherStarted());
      await _waitLaunchState(bloc, () => bloc.state.launchPreview != null);
      bloc.add(
        LaunchSelectionChanged(
          LaunchSelection.target(
            LaunchRequest(targetId: _targetId, worldOverride: 'test.forbidden'),
          ),
        ),
      );
      await Future<void>.delayed(const Duration(milliseconds: 30));
      expect(bloc.state.errorMessage, contains('not permitted'));
      expect(bloc.state.isBusy, isFalse);
      expect(fake.savedProfiles, isEmpty);
      await bloc.close();
    },
  );
}
