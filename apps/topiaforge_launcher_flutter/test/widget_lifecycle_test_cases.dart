part of 'widget_test.dart';

void registerLauncherLifecycleTests() {
  testWidgets('selects between validated discovered installs', (tester) async {
    final repository = _FakeLauncherRepository(
      snapshot: _multipleInstallSnapshot(),
    );
    await tester.pumpWidget(TopiaForgeLauncherApp(repository: repository));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Setup'));
    await tester.pumpAndSettle();

    expect(find.text('Detected installations'), findsOneWidget);
    expect(find.text('2 found'), findsOneWidget);
    await tester.tap(find.byKey(const Key('game-install-candidate-selector')));
    await tester.pumpAndSettle();
    await tester.tap(find.textContaining('SteamLibrary').last);
    await tester.pumpAndSettle();

    expect(repository.selectedGamePaths, [
      r'D:\SteamLibrary\steamapps\common\Robotopia',
    ]);
    final bloc = tester
        .element(find.byKey(const Key('game-install-candidate-selector')))
        .read<LauncherBloc>();
    expect(
      bloc.state.gameInstall?.path,
      r'D:\SteamLibrary\steamapps\common\Robotopia',
    );
    expect(find.text('Found by Steam.'), findsOneWidget);
  });

  testWidgets('shows one recovery candidate for an invalid saved install', (
    tester,
  ) async {
    await tester.pumpWidget(
      TopiaForgeLauncherApp(
        repository: _FakeLauncherRepository(
          snapshot: _singleRecoveryInstallSnapshot(),
        ),
      ),
    );
    await tester.pumpAndSettle();

    await tester.tap(find.text('Setup'));
    await tester.pumpAndSettle();

    expect(find.text('Detected installations'), findsOneWidget);
    expect(find.text('1 found'), findsOneWidget);
    expect(
      find.text('Choose one of the validated installations below.'),
      findsOneWidget,
    );
  });

  test('empty rediscovery clears stale candidates', () async {
    final repository = _FakeLauncherRepository(
      snapshot: _multipleInstallSnapshot(),
    );
    final bloc = LauncherBloc(repository)..add(const LauncherStarted());
    await bloc.stream.firstWhere((state) => !state.isBusy);
    repository.discoveryOverride = const [];

    bloc.add(const KnownInstallDetected());
    final refreshed = await bloc.stream.firstWhere(
      (state) => !state.isBusy && state.gameInstallCandidates.isEmpty,
    );

    expect(refreshed.gameInstallCandidates, isEmpty);
    expect(refreshed.gameInstall, isNull);
    expect(refreshed.canLaunch, isFalse);
    expect(refreshed.statusMessage, contains('No validated Robotopia install'));
    await bloc.close();
  });

  test('discovery falls back to the original single-install API', () async {
    final repository = _SingleInstallOnlyLauncherRepository();
    final bloc = LauncherBloc(repository)..add(const LauncherStarted());
    await bloc.stream.firstWhere((state) => !state.isBusy);

    bloc.add(const KnownInstallDetected());
    final detected = await bloc.stream.firstWhere(
      (state) =>
          !state.isBusy &&
          state.statusMessage == 'Detected one Robotopia install.',
    );

    expect(repository.selectedPath, detected.gameInstall?.path);
    await bloc.close();
  });

  test(
    'launcher serializes refresh events and disposes its repository',
    () async {
      final repository = _FakeLauncherRepository();
      repository.loadGate = Completer<void>();
      repository.loadEntered = Completer<void>();
      final bloc = LauncherBloc(repository);

      bloc.add(const LauncherStarted());
      await repository.loadEntered!.future;
      bloc.add(const LauncherRefreshRequested());
      await Future<void>.delayed(const Duration(milliseconds: 20));

      expect(repository.loadCount, 1);
      expect(repository.maxConcurrentLoads, 1);
      repository.loadGate!.complete();
      while (repository.loadCount < 2) {
        await Future<void>.delayed(const Duration(milliseconds: 5));
      }
      await bloc.close();

      expect(repository.maxConcurrentLoads, 1);
      expect(repository.disposed, isTrue);
    },
  );

  test('loaded launcher bloc closes and disposes its repository', () async {
    final repository = _FakeLauncherRepository();
    final bloc = LauncherBloc(repository)..add(const LauncherStarted());
    await bloc.stream.firstWhere((state) => !state.isBusy);

    await bloc.close().timeout(const Duration(seconds: 2));

    expect(repository.disposed, isTrue);
  });
}
