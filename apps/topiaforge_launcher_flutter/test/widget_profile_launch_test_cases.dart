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
        environment: {'TOPIAFORGE_PROFILE_TEST': 'coop'},
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
      launched.launchSettings.environment['TOPIAFORGE_PROFILE_TEST'],
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
    await tester.pumpWidget(TopiaForgeLauncherApp(repository: repository));
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

  testWidgets('profile enables Creator Tools with its dependency closure', (
    tester,
  ) async {
    const robotKitId = 'io.github.furroxide.topiaforge.robotkit';
    const contentId = 'io.github.furroxide.topiaforge.creatorcontent';
    const toolsId = 'io.github.furroxide.topiaforge.creatortools';
    const worldsId = 'io.github.furroxide.topiaforge.worlds';
    final robotKit = _manifest(robotKitId, version: '1.0.0', name: 'RobotKit');
    final content = _manifest(
      contentId,
      version: '1.0.0',
      name: 'Creator Content',
    );
    final worlds = _manifest(worldsId, version: '1.0.0', name: 'Worlds');
    final tools = _manifest(
      toolsId,
      version: '1.0.0',
      name: 'Creator Tools',
      category: 'DevTool',
      dependencies: const [
        ModDependency(id: contentId),
        ModDependency(id: robotKitId),
        ModDependency(id: worldsId),
      ],
    );
    const ordinary = LauncherProfile(
      id: 'ordinary',
      name: 'Ordinary',
      enabledMods: {'ordinary.mod'},
    );
    final repository = _FakeLauncherRepository(
      snapshot: _readySnapshot(
        profiles: [LauncherProfile.defaultProfile(), ordinary],
        installedMods: [
          _installedMod(robotKit),
          _installedMod(content),
          _installedMod(worlds),
          _installedMod(tools),
        ],
      ),
    );
    await pumpHome(tester, repository);

    await tester.tap(find.text('Profiles'));
    await tester.pumpAndSettle();
    await tester.tap(find.byKey(const ValueKey('profile-mod-$toolsId')));
    await tester.pumpAndSettle();

    final updated = repository.savedProfiles.first;
    expect(updated.inheritManagerModState, isFalse);
    expect(updated.enabledMods, {robotKitId, contentId, worldsId, toolsId});
    expect(updated.selectedVersions.keys, containsAll(updated.enabledMods));
    expect(repository.savedProfiles.last, same(ordinary));
  });
}
