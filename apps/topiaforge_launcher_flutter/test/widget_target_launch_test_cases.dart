part of 'widget_test.dart';

void _registerTargetLaunchRegressions(_PumpHome pumpHome) {
  testWidgets(
    'unavailable remembered selection stays visible and blocks launch',
    (tester) async {
      const retired = 'io.github.furroxide.topiaforge.worlds.sandbox';
      final profile = LauncherProfile.defaultProfile().copyWith(
        launchSelection: LaunchSelection.unresolvedLegacy({
          'worldSelection': {
            'worldId': 'missing.package.world',
            'gamemodeId': retired,
            'loadMode': 'additiveArena',
            'launchIntoGamemode': true,
          },
        }),
      );
      final repository = _FakeLauncherRepository(
        snapshot: _readySnapshot(profiles: [profile]),
      );
      await pumpHome(tester, repository);
      expect(find.textContaining(retired), findsWidgets);
      expect(
        tester.widget<GlowButton>(find.byType(GlowButton)).onPressed,
        isNull,
      );
      expect(repository.savedProfiles, isEmpty);
      expect(repository.launchedProfiles, isEmpty);
    },
  );

  testWidgets('setup safely presents an empty declared target inventory', (
    tester,
  ) async {
    final repository = _FakeLauncherRepository(snapshot: _readySnapshot());
    await pumpHome(tester, repository);
    await tester.tap(find.text('Setup'));
    await tester.pumpAndSettle();
    expect(tester.takeException(), isNull);
    expect(find.text('No launch targets available'), findsOneWidget);
    expect(repository.savedProfiles, isEmpty);
  });

  for (final blockedSelected in [true, false]) {
    testWidgets(
      'profile cards gate their own profile when blockedSelected=$blockedSelected',
      (tester) async {
        final blocked = LauncherProfile.defaultProfile().copyWith(
          id: 'blocked',
          name: 'Blocked profile',
        );
        const safe = LauncherProfile(id: 'safe', name: 'Empty profile');
        final repository = _FakeLauncherRepository(
          snapshot: _readySnapshot(
            profiles: [blocked, safe],
            selectedProfileId: blockedSelected ? 'blocked' : 'safe',
            installedMods: _conflictingMods(),
          ),
        );
        await pumpHome(tester, repository);
        final playButtons = tester
            .widgetList<FilledButton>(find.widgetWithText(FilledButton, 'Play'))
            .toList();
        expect(playButtons, hasLength(2));
        expect(playButtons[blockedSelected ? 0 : 1].onPressed, isNull);
        expect(playButtons[blockedSelected ? 1 : 0].onPressed, isNotNull);
      },
    );
  }
  testWidgets('long launch target remains usable at enlarged desktop text', (
    tester,
  ) async {
    tester.view.physicalSize = const Size(800, 600);
    tester.view.devicePixelRatio = 1;
    tester.platformDispatcher.textScaleFactorTestValue = 2;
    addTearDown(() {
      tester.view.reset();
      tester.platformDispatcher.clearTextScaleFactorTestValue();
    });
    final profile = LauncherProfile.defaultProfile().copyWith(
      launchSelection: LaunchSelection.target(
        LaunchRequest(targetId: _targetId),
      ),
    );
    final snapshot = _readySnapshot(
      profiles: [profile],
      installedMods: _declaredLaunchMods(),
    );
    final fake = _FakeLauncherRepository(snapshot: snapshot);
    final preview = _buildFakePreview(
      snapshot,
      snapshot.gameInstall!,
      profile,
      null,
    );
    fake.previewOverrides[profile.id] = LaunchPreview(
      profileId: profile.id,
      profileRevision: profile.revision,
      installIdentity: preview.installIdentity,
      packageDigest: preview.packageDigest,
      requestedSelection: profile.launchSelection,
      effectiveSelection: profile.launchSelection,
      resolution: preview.resolution,
      targets: [
        LaunchTargetChoice(
          id: _targetId,
          title: 'Long title ' * 20,
          owner: preview.targets.single.owner,
        ),
      ],
    );
    await tester.pumpWidget(TopiaForgeLauncherApp(repository: fake));
    await tester.pumpAndSettle();
    expect(tester.takeException(), isNull);
    expect(
      tester.widget<GlowButton>(find.byType(GlowButton)).onPressed,
      isNotNull,
    );
  });
  testWidgets(
    'target options offer only resolver choices and preserve explicit overrides',
    (tester) async {
      final profile = LauncherProfile.defaultProfile().copyWith(
        launchSelection: LaunchSelection.target(
          LaunchRequest(targetId: _targetId),
        ),
      );
      final fake = _FakeLauncherRepository(
        snapshot: _readySnapshot(
          profiles: [profile],
          installedMods: _declaredLaunchMods(overrides: true),
        ),
      );
      await pumpHome(tester, fake);
      Finder dropdown(String label) => find.byWidgetPredicate(
        (widget) =>
            widget is DropdownButtonFormField<String> &&
            widget.decoration.labelText == label,
      );
      await tester.tap(dropdown('World'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Alternate').last);
      await tester.pumpAndSettle();
      expect(
        fake.savedProfiles.single.launchSelection.request!.worldOverride,
        '$_targetPackage.alternate',
      );
      expect(
        fake.savedProfiles.single.launchSelection.request!.transitionOverride,
        isNull,
      );
      await tester.tap(dropdown('Transition'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Scene replacement').last);
      await tester.pumpAndSettle();
      final request = fake.savedProfiles.single.launchSelection.request!;
      expect(request.targetId, _targetId);
      expect(request.worldOverride, '$_targetPackage.alternate');
      expect(request.transitionOverride, ModTransitions.sceneReplacement);
      expect(tester.takeException(), isNull);
    },
  );
}
