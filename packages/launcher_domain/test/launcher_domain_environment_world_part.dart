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

  group('RobotopiaGameUnityCompatibility', () {
    const releaseEditor = UnityEditor(
      version: '6000.0.23f1',
      path: '/unity/6000.0.23f1',
    );
    const newestEditor = UnityEditor(
      version: '6000.2.10f1',
      path: '/unity/6000.2.10f1',
    );
    const configuredEditor = UnityEditor(
      version: '6000.0.31f1',
      path: '/unity/6000.0.31f1',
    );

    test('selects the exact release editor regardless of discovery order', () {
      expect(
        RobotopiaGameUnityCompatibility.selectEditor(const [
          newestEditor,
          releaseEditor,
        ]),
        same(releaseEditor),
      );
    });

    test('honors an explicit project editor pin', () {
      expect(
        RobotopiaGameUnityCompatibility.selectEditor(const [
          newestEditor,
          releaseEditor,
          configuredEditor,
        ], configuredVersion: ' 6000.0.31f1 '),
        same(configuredEditor),
      );
    });

    test('does not silently fall back to an incompatible editor', () {
      expect(
        RobotopiaGameUnityCompatibility.selectEditor(const [newestEditor]),
        isNull,
      );
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

    test('mergeRuntimeConfig preserves runtime-owned and future keys', () {
      final merged =
          const WorldSelection(
            worldId: 'new-world',
            gamemodeId: 'new-mode',
          ).mergeRuntimeConfig({
            'selectedWorldId': 'old-world',
            'endSessionOnMenuScene': false,
            'interceptPauseMenu': false,
            'futureRuntimeOption': {'enabled': true},
          });

      expect(merged['selectedWorldId'], 'new-world');
      expect(merged['selectedGamemodeId'], 'new-mode');
      expect(merged['endSessionOnMenuScene'], isFalse);
      expect(merged['interceptPauseMenu'], isFalse);
      expect(merged['futureRuntimeOption'], {'enabled': true});
    });

    test('round-trips through toJson and back', () {
      const selection = WorldSelection(
        worldId: 'io.github.furroxide.topiaforge.worlds.level.city',
        gamemodeId: 'io.github.furroxide.topiaforge.zombies.survival',
        loadMode: WorldSelection.sceneReplacement,
        autoLoadOnStart: true,
      );

      final restored = WorldSelection.fromJson(selection.toJson());

      expect(restored.worldId, selection.worldId);
      expect(restored.gamemodeId, selection.gamemodeId);
      expect(restored.loadMode, selection.loadMode);
      expect(restored.autoLoadOnStart, selection.autoLoadOnStart);
    });

    test('fromJson ignores runtime-only selected* keys', () {
      final selection = WorldSelection.fromJson({
        'selectedWorldId': 'w',
        'selectedGamemodeId': 'g',
      });

      expect(selection.worldId, WorldCatalog.openSandboxWorldId);
      expect(selection.gamemodeId, WorldCatalog.sandboxGamemodeId);
    });

    test('fromJson rejects retired canonical world and gamemode ids', () {
      for (final selection in [
        {
          'worldId':
              'robo'
              'topia.world.old',
        },
        {
          'gamemodeId':
              'robo'
              'topia.mode.old',
        },
      ]) {
        expect(() => WorldSelection.fromJson(selection), throwsFormatException);
      }
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

    test('fromJson filters retired gamemode identifiers', () {
      final catalog = WorldCatalog.fromJson({
        'worlds': [
          {'id': 'author.world', 'name': 'World'},
        ],
        'gamemodes': [
          {
            'id':
                'robo'
                'topia.mode.old',
            'name': 'Old Mode',
          },
        ],
      });

      expect(catalog.worlds.single.id, 'author.world');
      expect(catalog.gamemodes.single.id, WorldCatalog.sandboxGamemodeId);
    });

    test('fromJson filters retired world identifiers', () {
      final catalog = WorldCatalog.fromJson({
        'worlds': [
          {
            'id':
                'robo'
                'topia.world.old',
            'name': 'Old World',
          },
        ],
        'gamemodes': [
          {'id': 'author.mode', 'name': 'Mode'},
        ],
      });

      expect(catalog.worlds.single.id, WorldCatalog.openSandboxWorldId);
      expect(catalog.gamemodes.single.id, 'author.mode');
    });

    test(
      'fromJson keeps real worlds and backfills only the missing gamemodes',
      () {
        final catalog = WorldCatalog.fromJson({
          'worlds': [
            {
              'id': 'io.github.furroxide.topiaforge.worlds.level.city',
              'name': 'City',
            },
          ],
        });

        expect(
          catalog.worlds.single.id,
          'io.github.furroxide.topiaforge.worlds.level.city',
        );
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
            id: 'io.github.furroxide.topiaforge.worlds.open_sandbox',
            name: 'Open Sandbox',
          ),
          WorldDefinition(
            id: 'io.github.furroxide.topiaforge.worlds.level.introsewer',
            name: 'The Sewer',
            sceneName: 'IntroSewer',
            firstParty: true,
            supportsSceneReplacement: true,
            supportsAdditiveArena: false,
          ),
          WorldDefinition(
            id: 'io.github.furroxide.topiaforge.worlds.first-party.arena',
            name: 'Arena',
            sceneName: 'Arena',
            firstParty: true,
            supportsSceneReplacement: true,
          ),
        ],
        gamemodes: [
          GamemodeDefinition(
            id: 'io.github.furroxide.topiaforge.worlds.sandbox',
            name: 'Sandbox',
          ),
        ],
      );

      test('snaps a scene-replacement-only world off additiveArena', () {
        expect(
          catalog.reconcileLoadMode(
            'io.github.furroxide.topiaforge.worlds.level.introsewer',
            WorldSelection.additiveArena,
          ),
          WorldSelection.sceneReplacement,
        );
      });

      test('snaps an additive-only world off sceneReplacement', () {
        expect(
          catalog.reconcileLoadMode(
            'io.github.furroxide.topiaforge.worlds.open_sandbox',
            WorldSelection.sceneReplacement,
          ),
          WorldSelection.additiveArena,
        );
      });

      test('keeps a supported mode for a world that honours both', () {
        expect(
          catalog.reconcileLoadMode(
            'io.github.furroxide.topiaforge.worlds.first-party.arena',
            WorldSelection.additiveArena,
          ),
          WorldSelection.additiveArena,
        );
        expect(
          catalog.reconcileLoadMode(
            'io.github.furroxide.topiaforge.worlds.first-party.arena',
            WorldSelection.sceneReplacement,
          ),
          WorldSelection.sceneReplacement,
        );
      });

      test('normalizes an unknown/bogus mode before clamping', () {
        expect(
          catalog.reconcileLoadMode(
            'io.github.furroxide.topiaforge.worlds.level.introsewer',
            'bogus',
          ),
          WorldSelection.sceneReplacement,
        );
        expect(
          catalog.reconcileLoadMode(
            'io.github.furroxide.topiaforge.worlds.level.unknown',
            'bogus',
          ),
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
