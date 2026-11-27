part of 'widget_test.dart';

void registerDownloadedMapCacheTests() {
  Future<LauncherBloc> started(_FakeLauncherRepository repository) async {
    final bloc = LauncherBloc(repository);
    addTearDown(() async {
      if (repository is _MapCacheRepository) {
        final pending = repository.pendingClear;
        if (pending != null && !pending.isCompleted) pending.complete(0);
      }
      await bloc.close();
    });
    final loaded = bloc.stream.firstWhere((state) => state.snapshotLoaded);
    bloc.add(const LauncherStarted());
    await loaded;
    return bloc;
  }

  group('downloaded map cache', () {
    test(
      'awaits the selected install cleanup before reporting success',
      () async {
        final repository = _MapCacheRepository()
          ..pendingClear = Completer<int>();
        final bloc = await started(repository);
        final before = bloc.state;
        bloc.add(const DownloadedMapsClearRequested());
        await _idle();

        expect(repository.clearedInstalls, [same(before.gameInstall)]);
        expect(bloc.state.isBusy, isTrue);
        expect(
          bloc.state.statusMessage,
          isNot(contains('Downloaded maps cleared')),
        );
        repository.pendingClear!.complete(3);
        await _idle();

        expect(bloc.state.isBusy, isFalse);
        expect(bloc.state.errorMessage, isNull);
        expect(bloc.state.statusMessage, contains('Downloaded maps cleared'));
        expect(bloc.state.statusMessage, contains('download again'));
        expect(bloc.state.gameInstall, same(before.gameInstall));
        expect(bloc.state.profiles, same(before.profiles));
        expect(bloc.state.installedMods, same(before.installedMods));
      },
    );

    test('refuses when no game installation is selected', () async {
      final repository = _MapCacheRepository(
        snapshot: _replaceGameInstall(_readySnapshot(), null),
      );
      final bloc = await started(repository);
      bloc.add(const DownloadedMapsClearRequested());
      await _idle();

      expect(repository.clearedInstalls, isEmpty);
      expect(bloc.state.statusMessage, contains('Select a game installation'));
      expect(bloc.state.statusSeverity, IssueSeverity.warning);
      expect(bloc.state.errorMessage, isNull);
      expect(bloc.state.isBusy, isFalse);
    });

    test(
      'refuses an unsupported repository without changing selection',
      () async {
        final repository = _FakeLauncherRepository(snapshot: _readySnapshot());
        final bloc = await started(repository);
        final selected = bloc.state.gameInstall;
        bloc.add(const DownloadedMapsClearRequested());
        await _idle();

        expect(bloc.state.statusMessage, contains('map cache support'));
        expect(bloc.state.statusSeverity, IssueSeverity.warning);
        expect(bloc.state.gameInstall, same(selected));
        expect(bloc.state.errorMessage, isNull);
        expect(bloc.state.isBusy, isFalse);
      },
    );

    for (final message in [
      'Close Robotopia before clearing downloaded maps.',
      'Downloaded maps could not be cleared. Check folder permissions.',
    ]) {
      test('surfaces "$message" and permits a successful retry', () async {
        final repository = _MapCacheRepository()
          ..clearFailure = StateError(message);
        final bloc = await started(repository);
        bloc.add(const DownloadedMapsClearRequested());
        await _idle();

        expect(bloc.state.isBusy, isFalse);
        expect(bloc.state.statusMessage, 'Action failed.');
        expect(bloc.state.errorMessage, contains(message));
        expect(repository.clearedInstalls, hasLength(1));

        repository.clearFailure = null;
        bloc.add(const DownloadedMapsClearRequested());
        await _idle();
        expect(repository.clearedInstalls, hasLength(2));
        expect(bloc.state.isBusy, isFalse);
        expect(bloc.state.errorMessage, isNull);
        expect(bloc.state.statusMessage, contains('Downloaded maps cleared'));
      });
    }

    testWidgets('requires confirmation and cancellation leaves maps alone', (
      tester,
    ) async {
      tester.view.physicalSize = const Size(1920, 1080);
      tester.view.devicePixelRatio = 1;
      addTearDown(tester.view.reset);
      final repository = _MapCacheRepository();
      await tester.pumpWidget(TopiaForgeLauncherApp(repository: repository));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Settings'));
      await tester.pumpAndSettle();
      final clear = find.byKey(const Key('clear-downloaded-maps'));
      await tester.ensureVisible(clear);
      await tester.tap(clear);
      await tester.pumpAndSettle();
      expect(find.text('Clear downloaded maps?'), findsOneWidget);
      expect(repository.clearedInstalls, isEmpty);
      await tester.tap(find.text('Cancel'));
      await tester.pumpAndSettle();
      expect(repository.clearedInstalls, isEmpty);

      await tester.tap(clear);
      await tester.pumpAndSettle();
      await tester.tap(find.text('Clear maps'));
      await tester.pumpAndSettle();
      expect(repository.clearedInstalls, hasLength(1));
      expect(find.textContaining('Downloaded maps cleared.'), findsOneWidget);
    });
  });
}

class _MapCacheRepository extends _FakeLauncherRepository
    implements DownloadedMapRepository {
  _MapCacheRepository({LauncherSnapshot? snapshot})
    : super(snapshot: snapshot ?? _readySnapshot());

  final clearedInstalls = <GameInstall>[];
  Completer<int>? pendingClear;
  Object? clearFailure;

  @override
  Future<int> clearDownloadedMaps(GameInstall install) async {
    clearedInstalls.add(install);
    final failure = clearFailure;
    if (failure != null) throw failure;
    final pending = pendingClear;
    if (pending != null) return pending.future;
    return 2;
  }
}
