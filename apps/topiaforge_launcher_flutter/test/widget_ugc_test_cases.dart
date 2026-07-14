part of 'widget_test.dart';

// The Developer screen's ListView scrollable (stable across rebuilds). Scoped
// so pane-internal TextField scrollables cannot be selected by mistake.
Finder _devScrollable() => find
    .descendant(
      of: find.byKey(const Key('developer-scroll')),
      matching: find.byType(Scrollable),
    )
    .first;

void registerUgcWidgetTests() {
  testWidgets('UGC cleanup works without a detected game install', (
    tester,
  ) async {
    tester.view.physicalSize = const Size(1280, 1400);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    final developerRepository = _FakeDeveloperRepository();
    await tester.pumpWidget(
      TopiaForgeLauncherApp(
        repository: _FakeLauncherRepository(developerMode: true),
        developerRepository: developerRepository,
      ),
    );
    await tester.pumpAndSettle();

    await tester.tap(find.text('Dev'));
    await tester.pumpAndSettle();
    await tester.scrollUntilVisible(
      find.text('Clean Up Live Sync'),
      300,
      scrollable: _devScrollable(),
    );
    await tester.ensureVisible(find.text('Clean Up Live Sync'));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Clean Up Live Sync'));
    await tester.pumpAndSettle();
    await tester.tap(find.widgetWithText(FilledButton, 'Clean Up'));
    await tester.pumpAndSettle();

    final cleaned = developerRepository.updatedUgcSettings;
    expect(cleaned, isNotNull);
    expect(cleaned!.autoConnectOnStart, isFalse);
    expect(cleaned.editorUrl, isEmpty);
    expect(cleaned.documentUrl, isEmpty);
    final launcherState = BlocProvider.of<LauncherBloc>(
      tester.element(find.byType(LauncherShell)),
    ).state;
    expect(launcherState.ugcLiveSync.autoConnectOnStart, isFalse);
    expect(launcherState.ugcLiveSync.editorUrl, isEmpty);
    expect(launcherState.ugcLiveSync.documentUrl, isEmpty);
  });

  testWidgets('publisher capture does not discard unsaved UGC form edits', (
    tester,
  ) async {
    tester.view.physicalSize = const Size(1280, 1400);
    tester.view.devicePixelRatio = 1.0;
    addTearDown(tester.view.reset);
    final launcherRepository = _FakeLauncherRepository(developerMode: true);
    final developerRepository = _FakeDeveloperRepository(
      initialUgcSettings: const UgcLiveSyncSettings(
        transport: 'automerge',
        watchFolder: '/tmp/original-watch',
      ),
    );
    await tester.pumpWidget(
      TopiaForgeLauncherApp(
        repository: launcherRepository,
        developerRepository: developerRepository,
      ),
    );
    await tester.pumpAndSettle();

    await tester.tap(find.text('Dev'));
    await tester.pumpAndSettle();
    final watchFolder = find.widgetWithText(TextField, 'Watch folder');
    await tester.scrollUntilVisible(
      watchFolder,
      300,
      scrollable: _devScrollable(),
    );
    await tester.enterText(watchFolder, '/tmp/unsaved-watch');
    await tester.scrollUntilVisible(
      find.text('Publish via Automerge'),
      200,
      scrollable: _devScrollable(),
    );
    await tester.tap(find.text('Publish via Automerge'));
    await tester.pumpAndSettle();

    launcherRepository.emitPublisherSession('automerge:captured');
    await tester.pumpAndSettle();

    expect(
      tester.widget<TextField>(watchFolder).controller!.text,
      '/tmp/unsaved-watch',
    );
    expect(find.text('Document captured'), findsOneWidget);
  });

  testWidgets(
    'detached publisher controls remain available without a project',
    (tester) async {
      tester.view.physicalSize = const Size(1280, 1400);
      tester.view.devicePixelRatio = 1.0;
      addTearDown(tester.view.reset);
      final launcherRepository = _FakeLauncherRepository(developerMode: true);
      final developerRepository = _FakeDeveloperRepository(
        initialUgcSettings: const UgcLiveSyncSettings(
          transport: 'automerge',
          watchFolder: '/tmp/ugc-watch',
        ),
      );
      await tester.pumpWidget(
        TopiaForgeLauncherApp(
          repository: launcherRepository,
          developerRepository: developerRepository,
        ),
      );
      await tester.pumpAndSettle();

      await tester.tap(find.text('Dev'));
      await tester.pumpAndSettle();
      final publishButton = find.widgetWithText(
        OutlinedButton,
        'Publish via Automerge',
      );
      await tester.scrollUntilVisible(
        publishButton,
        300,
        scrollable: _devScrollable(),
      );
      await tester.ensureVisible(publishButton);
      await tester.pumpAndSettle();
      await tester.tap(publishButton);
      await tester.pumpAndSettle();
      expect(find.text('Stop Automerge publisher'), findsOneWidget);

      developerRepository.hasProject = false;
      await tester.tap(find.widgetWithText(FilledButton, 'Refresh'));
      await tester.pumpAndSettle();
      await tester.scrollUntilVisible(
        find.text('Clean Up Live Sync'),
        300,
        scrollable: _devScrollable(),
      );

      expect(find.text('Stop Automerge publisher'), findsOneWidget);
      await tester.tap(find.text('Clean Up Live Sync'));
      await tester.pumpAndSettle();
      await tester.tap(find.widgetWithText(FilledButton, 'Clean Up'));
      await tester.pumpAndSettle();

      expect(launcherRepository.publisherRunning, isFalse);
      expect(launcherRepository.revokePublisherCount, 1);
    },
  );
}

void registerUgcBlocTests() {
  test(
    'closing without an owned publisher preserves a detached lease',
    () async {
      final launcher = _FakeLauncherRepository();
      final bloc = LauncherBloc(launcher);

      await bloc.close();

      expect(launcher.revokePublisherCount, 0);
    },
  );

  test(
    'explicit cleanup revokes a lease without an install or project',
    () async {
      final launcher = _FakeLauncherRepository();
      final bloc = LauncherBloc(launcher);
      addTearDown(bloc.close);
      bloc.add(const LauncherStarted());
      await _waitForUgcState(bloc, (state) => !state.isBusy);

      bloc.add(const DeveloperUgcCleanupRequested());
      await _waitForUgcState(bloc, (_) => launcher.revokePublisherCount == 1);

      expect(launcher.revokePublisherCount, 1);
    },
  );

  test(
    'captured publisher session becomes the Go Live source of truth',
    () async {
      final launcher = _FakeLauncherRepository(snapshot: _readySnapshot());
      final developer = _FakeDeveloperRepository(
        initialUgcSettings: const UgcLiveSyncSettings(
          transport: 'automerge',
          watchFolder: '/tmp/ugc-watch',
        ),
      );
      final bloc = LauncherBloc(launcher, developerRepository: developer);
      addTearDown(bloc.close);
      await _loadUgcBloc(bloc);

      bloc.add(const DeveloperUgcPublishToggled());
      await _waitForUgcState(bloc, (state) => state.ugcPublisherRunning);
      launcher.emitPublisherSession('automerge:captured');
      final captured = await _waitForUgcState(
        bloc,
        (state) => state.ugcLiveSync.documentUrl == 'automerge:captured',
      );
      expect(captured.developerWorkspace!.project, isNotNull);

      bloc.add(const DeveloperUgcGoLive());
      await _waitForUgcState(
        bloc,
        (state) => launcher.launchedProfileIds.isNotEmpty,
      );

      expect(launcher.deployedUgcSettings!.documentUrl, 'automerge:captured');
      expect(launcher.launchedProfileIds, hasLength(1));
    },
  );

  test('direct publisher start prepares sidecar dependencies first', () async {
    final launcher = _FakeLauncherRepository(snapshot: _readySnapshot());
    final developer = _FakeDeveloperRepository(
      initialUgcSettings: const UgcLiveSyncSettings(
        transport: 'automerge',
        watchFolder: '/tmp/ugc-watch',
      ),
    );
    final bloc = LauncherBloc(launcher, developerRepository: developer);
    addTearDown(bloc.close);
    await _loadUgcBloc(bloc);

    bloc.add(const DeveloperUgcPublishToggled());
    await _waitForUgcState(bloc, (state) => state.ugcPublisherRunning);

    expect(developer.runSetupCount, 1);
    expect(launcher.publisherStartCount, 1);

    bloc.add(const DeveloperUgcPublishToggled());
    await _waitForUgcState(bloc, (state) => !state.ugcPublisherRunning);
    expect(developer.runSetupCount, 1);

    launcher.publisherStartError = StateError('injected publisher failure');
    bloc.add(const DeveloperUgcPublishToggled());
    final failed = await _waitForUgcState(
      bloc,
      (state) => state.errorMessage?.contains('injected publisher') == true,
    );
    expect(failed.ugcPublisherRunning, isFalse);
    expect(failed.statusMessage, 'Could not start the Automerge publisher.');
    expect(developer.runSetupCount, 2);
  });

  test(
    'Go Live waits on an already-running publisher without a session',
    () async {
      final launcher = _FakeLauncherRepository(snapshot: _readySnapshot());
      final developer = _FakeDeveloperRepository(
        initialUgcSettings: const UgcLiveSyncSettings(
          transport: 'automerge',
          watchFolder: '/tmp/ugc-watch',
        ),
      );
      final bloc = LauncherBloc(launcher, developerRepository: developer);
      addTearDown(bloc.close);
      await _loadUgcBloc(bloc);

      bloc.add(const DeveloperUgcPublishToggled());
      await _waitForUgcState(bloc, (state) => state.ugcPublisherRunning);
      bloc.add(const DeveloperUgcGoLive());
      await _waitForUgcState(
        bloc,
        (state) => state.statusMessage.startsWith('Waiting for the publisher'),
      );
      expect(launcher.launchedProfileIds, isEmpty);
      expect(launcher.publisherStartCount, 1);

      launcher.emitPublisherSession('automerge:delayed');
      await _waitForUgcState(
        bloc,
        (state) => launcher.launchedProfileIds.isNotEmpty,
      );

      expect(launcher.launchedProfileIds, hasLength(1));
      expect(launcher.deployedUgcSettings!.documentUrl, 'automerge:delayed');
    },
  );

  test(
    'Go Live can retry a captured session after persistence fails',
    () async {
      final launcher = _FakeLauncherRepository(snapshot: _readySnapshot());
      final developer = _FakeDeveloperRepository(
        initialUgcSettings: const UgcLiveSyncSettings(
          transport: 'automerge',
          watchFolder: '/tmp/ugc-watch',
        ),
      );
      final bloc = LauncherBloc(launcher, developerRepository: developer);
      addTearDown(bloc.close);
      await _loadUgcBloc(bloc);
      bloc.add(const DeveloperUgcPublishToggled());
      await _waitForUgcState(bloc, (state) => state.ugcPublisherRunning);
      bloc.add(const DeveloperUgcGoLive());
      await _waitForUgcState(
        bloc,
        (state) => state.statusMessage.startsWith('Waiting for the publisher'),
      );
      launcher.deployFailuresRemaining = 1;

      launcher.emitPublisherSession('automerge:retry');
      final failed = await _waitForUgcState(
        bloc,
        (state) => state.errorMessage?.contains('injected UGC') == true,
      );
      expect(failed.ugcCapturedDocumentUrl, 'automerge:retry');
      expect(launcher.launchedProfileIds, isEmpty);

      bloc.add(const DeveloperUgcGoLive());
      await _waitForUgcState(
        bloc,
        (_) => launcher.launchedProfileIds.isNotEmpty,
      );

      expect(launcher.launchedProfileIds, hasLength(1));
      expect(launcher.deployedUgcSettings!.documentUrl, 'automerge:retry');
    },
  );

  test(
    'settings save queued behind capture keeps the captured document',
    () async {
      final launcher = _FakeLauncherRepository(snapshot: _readySnapshot());
      final developer = _FakeDeveloperRepository(
        initialUgcSettings: const UgcLiveSyncSettings(
          transport: 'automerge',
          watchFolder: '/tmp/old-watch',
        ),
      );
      final bloc = LauncherBloc(launcher, developerRepository: developer);
      addTearDown(bloc.close);
      await _loadUgcBloc(bloc);
      bloc.add(const DeveloperUgcPublishToggled());
      await _waitForUgcState(bloc, (state) => state.ugcPublisherRunning);
      developer.updateUgcGate = Completer<void>();
      developer.updateUgcEntered = Completer<void>();

      launcher.emitPublisherSession('automerge:raced');
      await developer.updateUgcEntered!.future;
      bloc.add(const DeveloperUgcSettingsSaved(watchFolder: '/tmp/new-watch'));
      await Future<void>.delayed(Duration.zero);
      developer.updateUgcGate!.complete();
      final saved = await _waitForUgcState(
        bloc,
        (state) => state.ugcLiveSync.watchFolder == '/tmp/new-watch',
      );

      expect(saved.ugcLiveSync.documentUrl, 'automerge:raced');
      expect(developer.updatedUgcSettings!.documentUrl, 'automerge:raced');
    },
  );

  for (final invalidSession in {
    'malformed': 'not-json',
    'non-object': '[]',
    'missing document': '{"sceneId":"scene-1"}',
  }.entries) {
    test(
      'Go Live clears pending state after ${invalidSession.key} session payload',
      () async {
        final launcher = _FakeLauncherRepository(snapshot: _readySnapshot());
        final developer = _FakeDeveloperRepository(
          initialUgcSettings: const UgcLiveSyncSettings(
            transport: 'automerge',
            watchFolder: '/tmp/ugc-watch',
          ),
        );
        final bloc = LauncherBloc(launcher, developerRepository: developer);
        addTearDown(bloc.close);
        await _loadUgcBloc(bloc);

        bloc.add(const DeveloperUgcGoLive());
        await _waitForUgcState(
          bloc,
          (state) =>
              state.statusMessage.startsWith('Waiting for the publisher'),
        );
        launcher.emitPublisherPayload(invalidSession.value);
        final failed = await _waitForUgcState(
          bloc,
          (state) => state.errorMessage?.contains('Publisher') == true,
        );

        expect(failed.ugcPublisherRunning, isTrue);
        expect(failed.statusMessage, contains('try Go Live again'));
        launcher.emitPublisherSession('automerge:late');
        await _waitForUgcState(
          bloc,
          (state) => state.ugcCapturedDocumentUrl == 'automerge:late',
        );
        expect(launcher.launchedProfileIds, isEmpty);
      },
    );
  }

  test('Go Live reports a failed game launch as an error', () async {
    final launcher = _FakeLauncherRepository(snapshot: _readySnapshot())
      ..launchResult = const LaunchResult(
        started: false,
        message: 'TopiaForge is already running.',
      );
    final developer = _FakeDeveloperRepository(
      initialUgcSettings: const UgcLiveSyncSettings(
        transport: 'localFolder',
        watchFolder: '/tmp/ugc-watch',
      ),
    );
    final bloc = LauncherBloc(launcher, developerRepository: developer);
    addTearDown(bloc.close);
    await _loadUgcBloc(bloc);

    bloc.add(const DeveloperUgcGoLive());
    final failed = await _waitForUgcState(
      bloc,
      (state) => state.errorMessage == 'TopiaForge is already running.',
    );

    expect(failed.statusMessage, startsWith('Could not go live.'));
    expect(failed.statusMessage, isNot(contains('Going live.')));
  });

  test('settings saved to the project survive a game deploy failure', () async {
    final launcher = _FakeLauncherRepository(snapshot: _readySnapshot())
      ..deployFailuresRemaining = 1;
    final developer = _FakeDeveloperRepository(
      initialUgcSettings: const UgcLiveSyncSettings(
        transport: 'automerge',
        watchFolder: '/tmp/old-watch',
      ),
    );
    final bloc = LauncherBloc(launcher, developerRepository: developer);
    addTearDown(bloc.close);
    await _loadUgcBloc(bloc);

    bloc.add(
      const DeveloperUgcSettingsSaved(watchFolder: '/tmp/persisted-watch'),
    );
    final failed = await _waitForUgcState(
      bloc,
      (state) => state.errorMessage?.contains('injected UGC') == true,
    );

    expect(failed.ugcLiveSync.watchFolder, '/tmp/persisted-watch');
    expect(developer.updatedUgcSettings!.watchFolder, '/tmp/persisted-watch');
  });
}

Future<void> _loadUgcBloc(LauncherBloc bloc) async {
  bloc.add(const LauncherStarted());
  await _waitForUgcState(bloc, (state) => !state.isBusy);
  bloc.add(const DeveloperWorkspaceRefreshed());
  await _waitForUgcState(bloc, (state) => state.developerWorkspace != null);
}

Future<LauncherState> _waitForUgcState(
  LauncherBloc bloc,
  bool Function(LauncherState state) predicate,
) {
  if (predicate(bloc.state)) {
    return Future.value(bloc.state);
  }
  return bloc.stream.firstWhere(predicate).timeout(const Duration(seconds: 2));
}
