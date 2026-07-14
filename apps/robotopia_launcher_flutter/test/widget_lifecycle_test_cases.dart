part of 'widget_test.dart';

void registerLauncherLifecycleTests() {
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
