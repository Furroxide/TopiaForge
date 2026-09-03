part of 'widget_test.dart';

/// What Home's Launch affordance says it will do, and when it refuses to do it.
void _registerHomeLaunchTests(_PumpHome pumpHome) {
  testWidgets('home game-mode picker makes Launch start that mode', (
    tester,
  ) async {
    final repository = _FakeLauncherRepository(
      snapshot: _readySnapshot(worldCatalog: _gamemodeCatalog()),
    );
    await pumpHome(tester, repository);

    // Play normally is the default, and it is a visible choice rather than an absence.
    expect(find.text('LAUNCH'), findsOneWidget);

    await tester.tap(find.byType(DropdownButton<String?>));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Zombies').last);
    await tester.pumpAndSettle();

    // The button now states what it will do, so the intent is visible before it is pressed.
    expect(find.text('LAUNCH ZOMBIES'), findsOneWidget);

    final saved = repository.savedProfiles.single.worldSelection;
    expect(saved.launchIntoGamemode, isTrue);
    expect(saved.gamemodeId, 'io.github.furroxide.topiaforge.zombies.survival');
    expect(
      saved.worldId,
      'io.github.furroxide.topiaforge.worlds.open_sandbox',
      reason: "the mode's own menu entry decides where it starts",
    );
  });

  testWidgets('home game-mode picker can go back to playing normally', (
    tester,
  ) async {
    final launching = LauncherProfile.defaultProfile().copyWith(
      worldSelection: const WorldSelection(
        gamemodeId: 'io.github.furroxide.topiaforge.zombies.survival',
        launchIntoGamemode: true,
      ),
    );
    final repository = _FakeLauncherRepository(
      snapshot: _readySnapshot(
        profiles: [launching],
        worldCatalog: _gamemodeCatalog(),
      ),
    );
    await pumpHome(tester, repository);
    expect(find.text('LAUNCH ZOMBIES'), findsOneWidget);

    await tester.tap(find.byType(DropdownButton<String?>));
    await tester.pumpAndSettle();
    await tester.tap(find.text('None — play normally').last);
    await tester.pumpAndSettle();

    expect(find.text('LAUNCH'), findsOneWidget);
    expect(
      repository.savedProfiles.single.worldSelection.launchIntoGamemode,
      isFalse,
    );
  });

  testWidgets('conflicting mods block launch and say so', (tester) async {
    final repository = _FakeLauncherRepository(
      snapshot: _readySnapshot(installedMods: _conflictingMods()),
    );
    await pumpHome(tester, repository);

    // The game itself is fine, so the old copy would have claimed "Almost ready" and pointed at the
    // runtime-repair button, which cannot fix a mod conflict.
    expect(find.text('Mods need attention'), findsOneWidget);
    expect(find.text('Almost ready'), findsNothing);

    final glowButton = tester.widget<GlowButton>(find.byType(GlowButton));
    expect(
      glowButton.onPressed,
      isNull,
      reason: 'launch must be blocked before the attempt, not after it fails',
    );

    // A bare count would read "0 mods enabled" and leave the player to notice a number went down.
    expect(find.text('0 of 2 mods enabled'), findsOneWidget);
  });
}
