part of 'widget_test.dart';

void registerPublishedMapTests() {
  final source = PublishedMapSource(
    map: 'example',
    scene: 'courtyard',
    name: 'Courtyard',
  );
  Future<LauncherBloc> started(_FakeLauncherRepository repository) async {
    final bloc = LauncherBloc(repository);
    addTearDown(() async {
      if (repository is _PublishedMapRepository &&
          repository.pending != null &&
          !repository.pending!.isCompleted) {
        repository.pending!.complete(repository.sources);
      }
      await bloc.close();
    });
    bloc.add(const LauncherStarted());
    await _idle();
    return bloc;
  }

  group('published map sources', () {
    test(
      'loads saved references without changing the selected launch',
      () async {
        final repository = _PublishedMapRepository()..sources = [source];
        final bloc = await started(repository);
        expect(bloc.state.publishedMapSources.single, same(source));
        expect(repository.launchedSelections, isEmpty);
        expect(bloc.state.snapshotLoaded, isTrue);
      },
    );
    test(
      'awaits saving and preserves profiles and selected installation',
      () async {
        final repository = _PublishedMapRepository()
          ..pending = Completer<List<PublishedMapSource>>();
        final bloc = await started(repository);
        final before = bloc.state;
        bloc.add(PublishedMapSourceSaved(source));
        await _idle();
        expect(bloc.state.isBusy, isTrue);
        expect(bloc.state.publishedMapSources, isEmpty);
        repository.pending!.complete([source]);
        await _idle();
        expect(bloc.state.publishedMapSources.single, same(source));
        expect(bloc.state.isBusy, isFalse);
        expect(bloc.state.statusMessage, contains('next game start'));
        expect(bloc.state.profiles, same(before.profiles));
        expect(bloc.state.gameInstall, same(before.gameInstall));
      },
    );
    test('save failure retains old references and permits retry', () async {
      final repository = _PublishedMapRepository()
        ..failure = StateError('Saved map list could not be updated.');
      final bloc = await started(repository);
      bloc.add(PublishedMapSourceSaved(source));
      await _idle();
      expect(bloc.state.errorMessage, contains('could not be updated'));
      expect(bloc.state.publishedMapSources, isEmpty);
      repository.failure = null;
      bloc.add(PublishedMapSourceSaved(source));
      await _idle();
      expect(bloc.state.errorMessage, isNull);
      expect(bloc.state.publishedMapSources.single, same(source));
    });
    test(
      'unsupported hosts report unavailability without losing launch state',
      () async {
        final bloc = await started(
          _FakeLauncherRepository(snapshot: _readySnapshot()),
        );
        final before = bloc.state;
        bloc.add(PublishedMapSourceSaved(source));
        await _idle();
        expect(bloc.state.errorMessage, contains('unavailable'));
        expect(bloc.state.profiles, same(before.profiles));
      },
    );
    for (final size in [const Size(1920, 1080), const Size(2560, 1440)]) {
      testWidgets(
        'saved maps confirm removal and validate input at ${size.width}',
        (tester) async {
          tester.view.physicalSize = size;
          tester.view.devicePixelRatio = 1;
          addTearDown(tester.view.reset);
          final repository = _PublishedMapRepository()..sources = [source];
          await tester.pumpWidget(
            TopiaForgeLauncherApp(repository: repository),
          );
          await tester.pumpAndSettle();
          await tester.tap(find.text('Setup'));
          await tester.pumpAndSettle();
          final remove = find.byTooltip('Remove saved map');
          await tester.ensureVisible(remove);
          await tester.tap(remove);
          await tester.pumpAndSettle();
          expect(find.text('Remove saved map?'), findsOneWidget);
          expect(repository.removed, 0);
          await tester.tap(find.text('Cancel'));
          await tester.pumpAndSettle();
          expect(repository.removed, 0);
          await tester.tap(remove);
          await tester.pumpAndSettle();
          await tester.tap(find.text('Remove'));
          await tester.pumpAndSettle();
          expect(repository.removed, 1);
          expect(find.byTooltip('Remove saved map'), findsNothing);
          expect(
            find.text(
              'No maps saved yet. Add a map link or id and choose its scene id.',
            ),
            findsOneWidget,
          );
          final add = find.text('Add map');
          await tester.ensureVisible(add);
          await tester.tap(add);
          await tester.pumpAndSettle();
          final fields = find.descendant(
            of: find.byType(AlertDialog),
            matching: find.byType(TextField),
          );
          await tester.enterText(fields.at(0), 'Map');
          await tester.enterText(fields.at(1), 'automerge:private');
          await tester.enterText(fields.at(2), 'scene');
          await tester.tap(find.text('Save map'));
          await tester.pumpAndSettle();
          expect(
            find.text('Use a robotopia.gg map page link or its map id.'),
            findsOneWidget,
          );
          expect(repository.saved, 0);
          expect(tester.takeException(), isNull);
        },
      );
    }
  });
}

class _PublishedMapRepository extends _FakeLauncherRepository
    implements PublishedMapSourceRepository {
  _PublishedMapRepository() : super(snapshot: _readySnapshot());
  List<PublishedMapSource> sources = [];
  Completer<List<PublishedMapSource>>? pending;
  Object? failure;
  int saved = 0, removed = 0;
  @override
  Future<List<PublishedMapSource>> loadPublishedMapSources() async => sources;
  @override
  Future<List<PublishedMapSource>> savePublishedMapSource(
    PublishedMapSource source,
  ) async {
    saved++;
    if (failure != null) throw failure!;
    if (pending != null) return pending!.future;
    return sources = [...sources.where((s) => s.key != source.key), source];
  }

  @override
  Future<List<PublishedMapSource>> removePublishedMapSource(
    String map,
    String scene,
  ) async {
    removed++;
    return sources = sources
        .where((s) => s.map != map || s.scene != scene)
        .toList();
  }
}
