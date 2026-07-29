part of 'widget_test.dart';

void registerLauncherUpdateWidgetTests() {
  testWidgets('settings expose signed beta updates with explicit install', (
    tester,
  ) async {
    await tester.pumpWidget(
      TopiaForgeLauncherApp(repository: _FakeLauncherRepository()),
    );
    await tester.pumpAndSettle();

    await tester.tap(find.text('Settings'));
    await tester.pumpAndSettle();

    expect(find.text('Idle'), findsOneWidget);
    expect(
      find.textContaining('verifies Ed25519-signed release metadata'),
      findsOneWidget,
    );
    expect(find.text('Enable signed update checks'), findsOneWidget);
    expect(find.text('Check at startup'), findsOneWidget);
    expect(find.text('Check now'), findsOneWidget);
  });

  testWidgets('signed update flows through download and confirmation states', (
    tester,
  ) async {
    final candidate = _updateCandidate();
    final updater = _FakeLauncherUpdateRepository(
      checkStatus: LauncherUpdateStatus(
        phase: LauncherUpdatePhase.available,
        candidate: candidate,
        message: 'TopiaForge ${candidate.version} is available.',
      ),
    );
    await tester.pumpWidget(
      TopiaForgeLauncherApp(
        repository: _FakeLauncherRepository(),
        updateRepository: updater,
      ),
    );
    await tester.pumpAndSettle();
    await tester.tap(find.text('Settings'));
    await tester.pumpAndSettle();

    expect(find.text('Available'), findsOneWidget);
    final download = find.text('Download and verify');
    expect(download, findsOneWidget);
    expect(find.textContaining(candidate.signingKeyId), findsOneWidget);

    await tester.ensureVisible(download);
    await tester.tap(download);
    await tester.pumpAndSettle();
    expect(find.text('Verified'), findsOneWidget);
    final install = find.text('Restart and install');
    expect(install, findsOneWidget);

    await tester.ensureVisible(install.first);
    await tester.tap(install.first);
    await tester.pumpAndSettle();
    expect(find.text('Install verified update?'), findsOneWidget);
    expect(find.textContaining('roll back automatically'), findsOneWidget);
    await tester.tap(find.text('Cancel'));
    await tester.pumpAndSettle();
    expect(updater.applyCount, 0);
  });

  testWidgets('update progress and offline failure remain bounded', (
    tester,
  ) async {
    final updater = _FakeLauncherUpdateRepository();
    await tester.pumpWidget(
      TopiaForgeLauncherApp(
        repository: _FakeLauncherRepository(),
        updateRepository: updater,
      ),
    );
    await tester.pumpAndSettle();
    await tester.tap(find.text('Settings'));
    await tester.pumpAndSettle();

    updater.emit(
      LauncherUpdateStatus(
        phase: LauncherUpdatePhase.downloading,
        candidate: _updateCandidate(),
        progress: 0.5,
        message: 'Downloading verified package.',
      ),
    );
    await tester.pumpAndSettle();
    expect(find.byType(LinearProgressIndicator), findsOneWidget);
    expect(tester.takeException(), isNull);

    updater.emit(
      const LauncherUpdateStatus(
        phase: LauncherUpdatePhase.failed,
        message: 'Update check failed: offline.',
      ),
    );
    await tester.pumpAndSettle();
    expect(find.text('Attention'), findsOneWidget);
    expect(find.textContaining('offline'), findsWidgets);
    expect(tester.takeException(), isNull);
  });
}
