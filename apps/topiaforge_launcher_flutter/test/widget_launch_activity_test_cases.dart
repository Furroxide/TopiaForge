part of 'widget_test.dart';

LaunchActivity _activity(
  LauncherProfile profile, {
  String requestId = 'request-one',
}) => LaunchActivity(
  requestId: requestId,
  profileId: profile.id,
  profileRevision: profile.revision,
  installIdentity: r'C:\Games\Robotopia',
  packageDigest: 'fixture-package-digest',
  command: 'launch-target',
  unconfirmed: true,
);

Future<void> _waitLaunchState(
  LauncherBloc bloc,
  bool Function() condition,
) async {
  if (condition()) return;
  await bloc.stream
      .firstWhere((_) => condition())
      .timeout(const Duration(seconds: 3));
}

void _registerLaunchActivityTests() {
  _registerLaunchCorrelationTests();
  LauncherProfile selected() => LauncherProfile.defaultProfile().copyWith(
    launchSelection: LaunchSelection.target(LaunchRequest(targetId: _targetId)),
  );
  _FakeLauncherRepository repository(LauncherProfile profile) =>
      _FakeLauncherRepository(
        snapshot: _readySnapshot(
          profiles: [profile],
          installedMods: _declaredLaunchMods(),
        ),
      );

  test(
    'process start stays unconfirmed until correlated launch acknowledgement',
    () async {
      final profile = selected();
      final fake = repository(profile);
      final base = _activity(profile);
      fake.launchResult = LaunchResult(
        started: true,
        message: 'Process started.',
        requestId: base.requestId,
        latestActivity: base,
      );
      final bloc = LauncherBloc(fake)..add(const LauncherStarted());
      await _waitLaunchState(bloc, () => bloc.state.launchPreview != null);
      bloc.add(const GameLaunchRequested());
      await _waitLaunchState(bloc, () => bloc.state.launchActivity != null);
      expect(bloc.state.statusMessage, contains('unconfirmed'));
      expect(bloc.state.launchActivity!.sessionStarted, isFalse);
      final loading = base.applyProgress(
        LaunchProgress(
          requestId: base.requestId,
          sessionId: 'session-one',
          sequence: 1,
          phase: 'loading-world',
        ),
      );
      fake.activityController.add(loading);
      await _waitLaunchState(
        bloc,
        () => bloc.state.statusMessage == 'Loading world…',
      );
      fake.activityController.add(
        loading.applyProgress(
          LaunchProgress(
            requestId: base.requestId,
            sessionId: 'session-one',
            sequence: 2,
            phase: 'running',
          ),
        ),
      );
      await _waitLaunchState(
        bloc,
        () => bloc.state.launchActivity!.progress!.phase == 'running',
      );
      expect(bloc.state.statusMessage, contains('awaiting confirmation'));
      expect(bloc.state.launchActivity!.sessionStarted, isFalse);
      final running = loading.applyOutcome(
        LaunchOutcome(
          kind: 'launch',
          requestId: base.requestId,
          command: 'launch-target',
          sessionId: 'session-one',
          sequence: 2,
          status: 'succeeded',
          phase: 'running',
        ),
      );
      fake.activityController.add(running);
      await _waitLaunchState(
        bloc,
        () => bloc.state.launchActivity!.sessionStarted,
      );
      expect(bloc.state.statusMessage, 'Session is running.');
      fake.activityController.add(
        running.copyWith(processExited: true, unconfirmed: true),
      );
      await _waitLaunchState(
        bloc,
        () => bloc.state.statusMessage.contains('process ended'),
      );
      expect(bloc.state.launchActivity!.sessionOutcome, isNull);
      expect(bloc.state.statusMessage, contains('unconfirmed'));
      fake.activityController.add(
        running.applyOutcome(
          LaunchOutcome(
            kind: 'session',
            requestId: base.requestId,
            sessionId: 'session-one',
            sequence: 3,
            status: 'succeeded',
            phase: 'idle',
          ),
        ),
      );
      await _waitLaunchState(
        bloc,
        () => bloc.state.statusMessage == 'Session ended.',
      );
      await bloc.close();
      expect(fake.disposed, isTrue);
      expect(fake.activityController.hasListener, isFalse);
    },
  );

  test('acknowledgement arriving before process receipt is retained', () async {
    final profile = selected();
    final fake = repository(profile);
    final base = _activity(profile);
    final running = base.applyOutcome(
      LaunchOutcome(
        kind: 'launch',
        requestId: base.requestId,
        command: 'launch-target',
        sessionId: 'fast-session',
        sequence: 1,
        status: 'succeeded',
        phase: 'running',
      ),
    );
    fake.launchResult = LaunchResult(
      started: true,
      message: 'Process started.',
      requestId: base.requestId,
      latestActivity: base,
    );
    fake.onLaunch = (_) => fake.activityController.add(running);
    final bloc = LauncherBloc(fake)..add(const LauncherStarted());
    await _waitLaunchState(bloc, () => bloc.state.launchPreview != null);
    bloc.add(const GameLaunchRequested());
    await _waitLaunchState(
      bloc,
      () => bloc.state.launchActivity?.sessionStarted == true,
    );
    expect(bloc.state.statusMessage, 'Session is running.');
    await bloc.close();
  });

  test(
    'runtime failure survives foreign activity and stale progress',
    () async {
      final profile = selected();
      final fake = repository(profile);
      final base = _activity(profile);
      fake.launchResult = LaunchResult(
        started: true,
        message: 'Process started.',
        requestId: base.requestId,
        latestActivity: base,
      );
      final bloc = LauncherBloc(fake)..add(const LauncherStarted());
      await _waitLaunchState(bloc, () => bloc.state.launchPreview != null);
      bloc.add(const GameLaunchRequested());
      await _waitLaunchState(bloc, () => bloc.state.launchActivity != null);
      final failed = base.applyOutcome(
        LaunchOutcome(
          kind: 'launch',
          requestId: base.requestId,
          command: 'launch-target',
          sequence: 4,
          status: 'failed',
          phase: 'idle',
          error: LaunchOperationError(
            code: 'notFound',
            message: 'Spawn marker is missing.',
          ),
        ),
      );
      fake.activityController.add(failed.copyWith(processExited: true));
      await _waitLaunchState(
        bloc,
        () => bloc.state.errorMessage?.contains('Spawn marker') == true,
      );
      fake.activityController.add(
        _activity(profile, requestId: 'foreign').applyProgress(
          LaunchProgress(requestId: 'foreign', sequence: 20, phase: 'running'),
        ),
      );
      fake.activityController.add(
        base
            .copyWith(unconfirmed: false)
            .applyProgress(
              LaunchProgress(
                requestId: base.requestId,
                sequence: 3,
                phase: 'starting-mode',
              ),
            ),
      );
      await Future<void>.delayed(const Duration(milliseconds: 30));
      expect(bloc.state.statusMessage, contains('Spawn marker is missing.'));
      expect(bloc.state.launchActivity!.launchOutcome!.status, 'failed');
      await bloc.close();
    },
  );

  test(
    'rapid profile switching discards late preview from earlier generation',
    () async {
      final first = selected();
      const second = LauncherProfile(id: 'second', name: 'Second');
      final snapshot = _readySnapshot(
        profiles: [first, second],
        installedMods: _declaredLaunchMods(),
      );
      final fake = _FakeLauncherRepository(snapshot: snapshot);
      final old = Completer<LaunchPreview>();
      fake.previewGates[first.id] = old;
      final bloc = LauncherBloc(fake)..add(const LauncherStarted());
      await _waitLaunchState(bloc, () => !bloc.state.isBusy);
      bloc.add(const ProfileSelected('second'));
      await _waitLaunchState(
        bloc,
        () =>
            bloc.state.selectedProfileId == 'second' &&
            bloc.state.launchPreview != null,
      );
      fake.previewGates.remove(first.id);
      bloc.add(ProfileSelected(first.id));
      await _waitLaunchState(
        bloc,
        () =>
            bloc.state.selectedProfileId == first.id &&
            bloc.state.launchPreview != null,
      );
      final fresh = bloc.state.launchPreview;
      old.complete(
        LaunchPreview(
          profileId: first.id,
          profileRevision: first.revision,
          installIdentity: snapshot.gameInstall!.path,
          packageDigest: 'stale',
          requestedSelection: first.launchSelection,
          effectiveSelection: first.launchSelection,
          issues: const [
            LauncherIssue(
              severity: IssueSeverity.error,
              message: 'Stale result',
            ),
          ],
        ),
      );
      await Future<void>.delayed(const Duration(milliseconds: 30));
      expect(bloc.state.launchPreview, same(fresh));
      expect(bloc.state.errorMessage, isNot(contains('Stale result')));
      await bloc.close();
    },
  );
}
