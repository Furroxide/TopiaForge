part of 'launcher_domain_test.dart';

void _environmentAndWorldModelTests() {
  group('EnvironmentReport', () {
    test(
      'only develop-purpose tools block developing; optional ones do not',
      () {
        const env = EnvironmentReport(
          checks: [
            ToolCheck(
              name: '.NET SDK',
              status: ToolStatus.ok,
              purpose: ToolPurpose.develop,
            ),
            ToolCheck(
              name: 'Node.js',
              status: ToolStatus.missing,
              purpose: ToolPurpose.ugcAutomerge,
            ),
            ToolCheck(
              name: 'Git',
              status: ToolStatus.warning,
              purpose: ToolPurpose.optional,
            ),
          ],
        );

        expect(env.developerReady, isTrue);
        expect(env.ugcAutomergeReady, isFalse);
        expect(env.blockers, isEmpty);
      },
    );

    test('a missing develop tool is a blocker', () {
      const env = EnvironmentReport(
        checks: [
          ToolCheck(
            name: '.NET SDK',
            status: ToolStatus.missing,
            purpose: ToolPurpose.develop,
          ),
        ],
      );

      expect(env.developerReady, isFalse);
      expect(env.blockers, hasLength(1));
    });

    test('DeveloperSetupResult.ok mirrors environment.developerReady', () {
      const ready = DeveloperSetupResult(
        environment: EnvironmentReport(
          checks: [
            ToolCheck(
              name: '.NET SDK',
              status: ToolStatus.ok,
              purpose: ToolPurpose.develop,
            ),
          ],
        ),
        actions: ['Sidecar dependencies already present.'],
      );
      expect(ready.ok, isTrue);

      const notReady = DeveloperSetupResult(
        environment: EnvironmentReport(
          checks: [
            ToolCheck(
              name: '.NET SDK',
              status: ToolStatus.missing,
              purpose: ToolPurpose.develop,
            ),
          ],
        ),
      );
      expect(notReady.ok, isFalse);
    });
  });

  group('WorldSelection', () {
    test(
      'toRuntimeConfig emits exactly the keys the C# WorldsConfig expects',
      () {
        final config = const WorldSelection(
          worldId: 'w1',
          gamemodeId: 'g1',
          loadMode: WorldSelection.sceneReplacement,
          autoLoadOnStart: true,
        ).toRuntimeConfig();

        expect(
          config.keys.toSet(),
          equals({
            'selectedWorldId',
            'selectedGamemodeId',
            'loadMode',
            'autoLoadOnStart',
            'allowAdditiveFallback',
          }),
        );
        expect(config['selectedWorldId'], 'w1');
        expect(config['selectedGamemodeId'], 'g1');
        expect(config['loadMode'], 'sceneReplacement');
        expect(config['autoLoadOnStart'], isTrue);
        expect(config['allowAdditiveFallback'], isTrue);
      },
    );

    test('round-trips through toJson and back', () {
      const selection = WorldSelection(
        worldId: 'robotopia.level.city',
        gamemodeId: 'robotopia.zombies.survival',
        loadMode: WorldSelection.sceneReplacement,
        autoLoadOnStart: true,
      );

      final restored = WorldSelection.fromJson(selection.toJson());

      expect(restored.worldId, selection.worldId);
      expect(restored.gamemodeId, selection.gamemodeId);
      expect(restored.loadMode, selection.loadMode);
      expect(restored.autoLoadOnStart, selection.autoLoadOnStart);
    });

    test('fromJson reads the runtime selected* key aliases', () {
      final selection = WorldSelection.fromJson({
        'selectedWorldId': 'w',
        'selectedGamemodeId': 'g',
      });

      expect(selection.worldId, 'w');
      expect(selection.gamemodeId, 'g');
    });

    test('fromJson prefers canonical keys over selected* aliases', () {
      final selection = WorldSelection.fromJson({
        'worldId': 'canonical',
        'selectedWorldId': 'alias',
      });

      expect(selection.worldId, 'canonical');
    });

    test('fromJson clamps an unknown loadMode and applies defaults', () {
      final bad = WorldSelection.fromJson({'loadMode': 'totally-bogus'});
      expect(bad.loadMode, WorldSelection.additiveArena);

      final empty = WorldSelection.fromJson(const {});
      expect(empty.worldId, WorldCatalog.openSandboxWorldId);
      expect(empty.gamemodeId, WorldCatalog.sandboxGamemodeId);
      expect(empty.loadMode, WorldSelection.additiveArena);
      expect(empty.autoLoadOnStart, isFalse);
    });
  });

  group('WorldCatalog', () {
    test('fromJson returns the built-in fallback for empty json', () {
      final catalog = WorldCatalog.fromJson(const {});

      expect(catalog.worlds.single.id, WorldCatalog.openSandboxWorldId);
      expect(catalog.gamemodes.single.id, WorldCatalog.sandboxGamemodeId);
    });

    test(
      'fromJson keeps real worlds and backfills only the missing gamemodes',
      () {
        final catalog = WorldCatalog.fromJson({
          'worlds': [
            {'id': 'robotopia.level.city', 'name': 'City'},
          ],
        });

        expect(catalog.worlds.single.id, 'robotopia.level.city');
        expect(catalog.gamemodes.single.id, WorldCatalog.sandboxGamemodeId);
      },
    );

    test('fromJson drops entries with a blank id or name', () {
      final catalog = WorldCatalog.fromJson({
        'worlds': [
          {'id': '', 'name': 'Nameless'},
          {'id': 'good', 'name': 'Good World'},
        ],
        'gamemodes': [
          {'id': 'mode', 'name': 'Mode'},
        ],
      });

      expect(catalog.worlds.map((world) => world.id), ['good']);
      expect(catalog.gamemodes.single.id, 'mode');
    });

    group('reconcileLoadMode', () {
      const catalog = WorldCatalog(
        worlds: [
          WorldDefinition(
            id: 'robotopia.worlds.open_sandbox',
            name: 'Open Sandbox',
          ),
          WorldDefinition(
            id: 'robotopia.level.introsewer',
            name: 'The Sewer',
            sceneName: 'IntroSewer',
            firstParty: true,
            supportsSceneReplacement: true,
            supportsAdditiveArena: false,
          ),
          WorldDefinition(
            id: 'robotopia.first_party.arena',
            name: 'Arena',
            sceneName: 'Arena',
            firstParty: true,
            supportsSceneReplacement: true,
          ),
        ],
        gamemodes: [
          GamemodeDefinition(id: 'robotopia.worlds.sandbox', name: 'Sandbox'),
        ],
      );

      test('snaps a scene-replacement-only world off additiveArena', () {
        expect(
          catalog.reconcileLoadMode(
            'robotopia.level.introsewer',
            WorldSelection.additiveArena,
          ),
          WorldSelection.sceneReplacement,
        );
      });

      test('snaps an additive-only world off sceneReplacement', () {
        expect(
          catalog.reconcileLoadMode(
            'robotopia.worlds.open_sandbox',
            WorldSelection.sceneReplacement,
          ),
          WorldSelection.additiveArena,
        );
      });

      test('keeps a supported mode for a world that honours both', () {
        expect(
          catalog.reconcileLoadMode(
            'robotopia.first_party.arena',
            WorldSelection.additiveArena,
          ),
          WorldSelection.additiveArena,
        );
        expect(
          catalog.reconcileLoadMode(
            'robotopia.first_party.arena',
            WorldSelection.sceneReplacement,
          ),
          WorldSelection.sceneReplacement,
        );
      });

      test('normalizes an unknown/bogus mode before clamping', () {
        expect(
          catalog.reconcileLoadMode('robotopia.level.introsewer', 'bogus'),
          WorldSelection.sceneReplacement,
        );
        expect(
          catalog.reconcileLoadMode('robotopia.level.unknown', 'bogus'),
          WorldSelection.additiveArena,
        );
      });
    });
  });

  group('WorldDefinition', () {
    test(
      'fromJson defaults supportsAdditiveArena true and other flags false',
      () {
        final world = WorldDefinition.fromJson({'id': 'w', 'name': 'W'});

        expect(world.supportsAdditiveArena, isTrue);
        expect(world.supportsSceneReplacement, isFalse);
        expect(world.firstParty, isFalse);
        expect(world.supportedLoadModes, {WorldSelection.additiveArena});
      },
    );

    test('supportedLoadModes reflects the capability flags', () {
      const checkpointLevel = WorldDefinition(
        id: 'lvl',
        name: 'Level',
        firstParty: true,
        supportsSceneReplacement: true,
        supportsAdditiveArena: false,
      );
      expect(checkpointLevel.supportedLoadModes, {
        WorldSelection.sceneReplacement,
      });

      const buildScene = WorldDefinition(
        id: 'scene',
        name: 'Scene',
        supportsSceneReplacement: true,
        supportsAdditiveArena: true,
      );
      expect(buildScene.supportedLoadModes, {
        WorldSelection.sceneReplacement,
        WorldSelection.additiveArena,
      });
    });
  });
}
