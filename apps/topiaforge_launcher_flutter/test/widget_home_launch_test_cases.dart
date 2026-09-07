part of 'widget_test.dart';

void _registerHomeLaunchTests(_PumpHome pumpHome) {
  testWidgets('home target picker starts the declared target', (tester) async {
    final repository = _FakeLauncherRepository(
      snapshot: _readySnapshot(installedMods: _declaredLaunchMods()),
    );
    await pumpHome(tester, repository);
    expect(find.text('LAUNCH'), findsOneWidget);
    await tester.tap(find.byType(DropdownButtonFormField<String>).first);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Zombies').last);
    await tester.pumpAndSettle();
    expect(find.text('LAUNCH'), findsOneWidget);
    final saved = repository.savedProfiles.single.launchSelection;
    expect(saved.kind, LaunchSelectionKind.target);
    expect(saved.request!.targetId, _targetId);
    expect(saved.request!.worldOverride, isNull);
    expect(saved.request!.transitionOverride, isNull);
    expect(repository.savedProfiles.single.revision, greaterThan(0));
  });

  testWidgets('home target picker explicitly returns to main menu', (
    tester,
  ) async {
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
    await pumpHome(tester, repository);
    expect(find.text('LAUNCH'), findsOneWidget);
    await tester.tap(find.byType(DropdownButtonFormField<String>).first);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Main menu').last);
    await tester.pumpAndSettle();
    expect(find.text('LAUNCH'), findsOneWidget);
    expect(
      repository.savedProfiles.single.launchSelection.kind,
      LaunchSelectionKind.mainMenu,
    );
  });

  testWidgets('conflicting mods block launch and say so', (tester) async {
    final repository = _FakeLauncherRepository(
      snapshot: _readySnapshot(installedMods: _conflictingMods()),
    );
    await pumpHome(tester, repository);
    expect(find.text('Mods need attention'), findsOneWidget);
    expect(find.text('Almost ready'), findsNothing);
    final button = tester.widget<GlowButton>(find.byType(GlowButton));
    expect(button.onPressed, isNull);
    expect(find.text('0 of 2 mods enabled'), findsOneWidget);
  });
  testWidgets('confirms restart before relaunching TopiaForge', (tester) async {
    final repository = _FakeLauncherRepository(
      snapshot: _updateSnapshot(needsRepair: true),
    );
    await tester.pumpWidget(TopiaForgeLauncherApp(repository: repository));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Mods'));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Restart').first);
    await tester.pumpAndSettle();

    expect(find.text('Restart TopiaForge?'), findsOneWidget);
    expect(repository.restartCount, 0);

    await tester.tap(find.widgetWithText(FilledButton, 'Restart'));
    await tester.pumpAndSettle();

    expect(repository.installOrRepairRuntimeCount, 1);
    expect(repository.restartCount, 1);
    expect(find.textContaining('session start is unconfirmed'), findsOneWidget);
  });
}
