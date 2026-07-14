part of 'widget_test.dart';

typedef _PumpHome =
    Future<void> Function(
      WidgetTester tester,
      _FakeLauncherRepository repository,
    );

void _registerProfileLaunchWidgetTests(_PumpHome pumpHome) {
  testWidgets('profile card launches that exact safe-mode configuration', (
    tester,
  ) async {
    const profile = LauncherProfile(
      id: 'coop',
      name: 'Co-op',
      enabledMods: {'alpha.mod'},
      selectedVersions: {'alpha.mod': '1.2.3'},
      launchSettings: LaunchSettings(
        safeMode: true,
        extraArguments: ['--coop'],
        environment: {'ROBOTOPIA_PROFILE_TEST': 'coop'},
      ),
    );
    final repository = _FakeLauncherRepository(
      snapshot: _readySnapshot(
        profiles: [LauncherProfile.defaultProfile(), profile],
      ),
    );
    await pumpHome(tester, repository);

    expect(find.text('Jump back in'), findsOneWidget);
    expect(find.text('Play'), findsNWidgets(2));
    // Selected profile is listed first, so the last Play belongs to Co-op.
    await tester.tap(find.text('Play').last);
    await tester.pumpAndSettle();

    expect(repository.launchedProfileIds, ['coop']);
    final launched = repository.launchedProfiles.single;
    expect(launched.launchSettings.safeMode, isTrue);
    expect(launched.enabledMods, {'alpha.mod'});
    expect(launched.selectedVersions, {'alpha.mod': '1.2.3'});
    expect(
      launched.launchSettings.environment['ROBOTOPIA_PROFILE_TEST'],
      'coop',
    );
  });

  testWidgets('profile card play auto-repairs before launch', (tester) async {
    final repository = _FakeLauncherRepository(
      snapshot: _readySnapshot(
        needsRepair: true,
        profiles: [
          LauncherProfile.defaultProfile(),
          const LauncherProfile(id: 'coop', name: 'Co-op'),
        ],
      ),
    );
    await pumpHome(tester, repository);

    await tester.tap(find.text('Play').last);
    await tester.pumpAndSettle();

    expect(repository.installOrRepairRuntimeCount, 1);
    expect(repository.launchedProfileIds, ['coop']);
  });

  testWidgets('new profile snapshots installed mod state and versions', (
    tester,
  ) async {
    final repository = _FakeLauncherRepository(snapshot: _updateSnapshot());
    await tester.pumpWidget(RobotopiaLauncherApp(repository: repository));
    await tester.pumpAndSettle();

    await tester.tap(find.text('Profiles'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Create'));
    await tester.pumpAndSettle();

    final created = repository.savedProfiles.last;
    expect(created.inheritManagerModState, isFalse);
    expect(created.enabledMods, {'timer.mod'});
    expect(created.selectedVersions, {'timer.mod': '1.0.0'});
    expect(repository.savedSelectedProfileId, created.id);
  });
}
