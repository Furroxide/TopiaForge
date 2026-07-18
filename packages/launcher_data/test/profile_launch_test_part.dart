part of 'launcher_data_test.dart';

void _registerProfileLaunchTests({
  required Directory Function() root,
  required Directory Function() dataRoot,
  required Directory Function() repositoryRoot,
  required Directory Function() gameRoot,
}) {
  group('profile launch isolation', () {
    test(
      'revalidates the exact selected version against the live game build',
      () async {
        File(
          p.join(gameRoot().path, 'installed-build.json'),
        ).writeAsStringSync('{"id":2227}');
        var processStarted = false;
        final prepared = await _prepareProfileLaunchRepository(
          dataRoot: dataRoot(),
          repositoryRoot: repositoryRoot(),
          gameRoot: gameRoot(),
          starter: (_) async {
            processStarted = true;
            return 1;
          },
        );
        final repository = prepared.$1;
        final install = prepared.$2;
        await repository.installPackage(
          _createPackage(
            root(),
            id: 'versioned.mod',
            version: '1.0.0',
            gameVersionRange: '0.0.2227',
          ).path,
          install,
        );
        await repository.installPackage(
          _createPackage(root(), id: 'versioned.mod', version: '2.0.0').path,
          install,
        );
        File(
          p.join(gameRoot().path, 'installed-build.json'),
        ).writeAsStringSync('{"id":2228}');

        final result = await repository.launch(
          install,
          const LauncherProfile(
            id: 'old-version',
            name: 'Old Version',
            enabledMods: {'versioned.mod'},
            selectedVersions: {'versioned.mod': '1.0.0'},
          ),
        );

        expect(result.started, isFalse);
        expect(result.message, contains('not 0.0.2228'));
        expect(processStarted, isFalse);
      },
    );

    test(
      'passes exact mods, selected versions, and environment without state writes',
      () async {
        late GameProcessRequest request;
        late Map<String, Object?> launchJson;
        late String launchFilePath;
        final prepared = await _prepareProfileLaunchRepository(
          dataRoot: dataRoot(),
          repositoryRoot: repositoryRoot(),
          gameRoot: gameRoot(),
          starter: (value) async {
            request = value;
            launchFilePath = value
                .environment[ProfileLaunchConfiguration.environmentVariable]!;
            launchJson =
                jsonDecode(File(launchFilePath).readAsStringSync())
                    as Map<String, Object?>;
            File(launchFilePath).deleteSync();
            return 4242;
          },
        );
        final repository = prepared.$1;
        final install = prepared.$2;
        await repository.installPackage(
          _createPackage(root(), id: 'alpha.mod', version: '1.0.0').path,
          install,
        );
        await repository.installPackage(
          _createPackage(root(), id: 'alpha.mod', version: '2.0.0').path,
          install,
        );
        await repository.installPackage(
          _createPackage(root(), id: 'beta.mod', version: '1.0.0').path,
          install,
        );
        final stateFile = _profileManagerState(gameRoot());
        final stateBefore = stateFile.readAsStringSync();

        final result = await repository.launch(
          install,
          const LauncherProfile(
            id: 'isolated',
            name: 'Isolated',
            enabledMods: {'alpha.mod'},
            selectedVersions: {'alpha.mod': '1.0.0'},
            launchSettings: LaunchSettings(
              extraArguments: ['--profile-test'],
              environment: {'TOPIAFORGE_PROFILE_TEST': 'isolated'},
            ),
          ),
        );

        expect(result.started, isTrue);
        expect(result.processId, 4242);
        expect(request.arguments, contains('--profile-test'));
        expect(request.environment['TOPIAFORGE_PROFILE_TEST'], 'isolated');
        expect(
          request.environment[ProfileLaunchConfiguration.environmentVariable],
          launchFilePath,
        );
        if (!Platform.isWindows) {
          expect(request.environment['WINEDLLOVERRIDES'], 'winhttp=n,b');
        }
        expect(launchJson['profileId'], 'isolated');
        expect(launchJson['inheritManagerModState'], isFalse);
        expect(launchJson['enabledMods'], ['alpha.mod']);
        expect(launchJson['selectedVersions'], {'alpha.mod': '1.0.0'});
        expect(stateFile.readAsStringSync(), stateBefore);
        expect(File(launchFilePath).existsSync(), isFalse);
      },
    );

    test(
      'safe mode is process-scoped and leaves enabled mods intact',
      () async {
        late Map<String, Object?> launchJson;
        final prepared = await _prepareProfileLaunchRepository(
          dataRoot: dataRoot(),
          repositoryRoot: repositoryRoot(),
          gameRoot: gameRoot(),
          starter: (request) async {
            final path = request
                .environment[ProfileLaunchConfiguration.environmentVariable]!;
            launchJson =
                jsonDecode(File(path).readAsStringSync())
                    as Map<String, Object?>;
            File(path).deleteSync();
            return 7;
          },
        );
        final repository = prepared.$1;
        final install = prepared.$2;
        await repository.installPackage(
          _createPackage(root(), id: 'alpha.mod', version: '1.0.0').path,
          install,
        );
        final stateFile = _profileManagerState(gameRoot());
        final stateBefore = stateFile.readAsStringSync();

        final result = await repository.launch(
          install,
          const LauncherProfile(
            id: 'safe',
            name: 'Safe',
            enabledMods: {'missing.mod'},
            selectedVersions: {'missing.mod': '9.0.0'},
            launchSettings: LaunchSettings(safeMode: true),
          ),
        );

        expect(result.started, isTrue);
        expect(result.message, contains('for this run only'));
        expect(launchJson['safeMode'], isTrue);
        expect(stateFile.readAsStringSync(), stateBefore);
        final state = jsonDecode(stateBefore) as Map<String, Object?>;
        final mods = (state['mods'] as List).cast<Map<String, Object?>>();
        expect(mods.single['enabled'], isTrue);
      },
    );

    test(
      'process-start failure removes the override and rolls back state',
      () async {
        late String launchFilePath;
        final prepared = await _prepareProfileLaunchRepository(
          dataRoot: dataRoot(),
          repositoryRoot: repositoryRoot(),
          gameRoot: gameRoot(),
          starter: (request) async {
            launchFilePath = request
                .environment[ProfileLaunchConfiguration.environmentVariable]!;
            throw StateError('synthetic process failure');
          },
        );
        final repository = prepared.$1;
        final install = prepared.$2;
        await repository.installPackage(
          _createPackage(root(), id: 'alpha.mod', version: '1.0.0').path,
          install,
        );
        final stateFile = _profileManagerState(gameRoot());
        final stateBefore = stateFile.readAsStringSync();
        final worldFile = File(
          p.join(
            gameRoot().path,
            'BepInEx',
            'TopiaForge',
            'config',
            'topiaforge.worlds.json',
          ),
        );
        const worldBefore =
            '{"selectedWorldId":"existing.world","providerState":{"x":1}}\n';
        worldFile
          ..parent.createSync(recursive: true)
          ..writeAsStringSync(worldBefore);

        final result = await repository.launch(
          install,
          const LauncherProfile(
            id: 'failure',
            name: 'Failure',
            enabledMods: {'alpha.mod'},
          ),
        );

        expect(result.started, isFalse);
        expect(result.message, contains('No mod state was changed'));
        expect(File(launchFilePath).existsSync(), isFalse);
        expect(stateFile.readAsStringSync(), stateBefore);
        expect(worldFile.readAsStringSync(), worldBefore);
      },
    );

    test('profile environment cannot replace the one-shot contract', () async {
      var processStarted = false;
      final prepared = await _prepareProfileLaunchRepository(
        dataRoot: dataRoot(),
        repositoryRoot: repositoryRoot(),
        gameRoot: gameRoot(),
        starter: (_) async {
          processStarted = true;
          return 1;
        },
      );

      final result = await prepared.$1.launch(
        prepared.$2,
        const LauncherProfile(
          id: 'reserved-environment',
          name: 'Reserved environment',
          launchSettings: LaunchSettings(
            environment: {
              ProfileLaunchConfiguration.environmentVariable: 'forged.json',
            },
          ),
        ),
      );

      expect(result.started, isFalse);
      expect(result.message, contains('cannot replace required variable'));
      expect(processStarted, isFalse);
      final staging = Directory(
        p.join(gameRoot().path, 'BepInEx', 'TopiaForge', 'staging'),
      );
      expect(
        staging.listSync().where(
          (entry) => p.basename(entry.path).startsWith('launch-profile-'),
        ),
        isEmpty,
      );
    });

    test('world selection write rejects retired in-memory ids', () async {
      var processStarted = false;
      final prepared = await _prepareProfileLaunchRepository(
        dataRoot: dataRoot(),
        repositoryRoot: repositoryRoot(),
        gameRoot: gameRoot(),
        starter: (_) async {
          processStarted = true;
          return 1;
        },
      );
      final retired =
          'robo'
          'topia.world.retired';

      await expectLater(
        prepared.$1.launch(
          prepared.$2,
          LauncherProfile(
            id: 'retired-world',
            name: 'Retired world',
            worldSelection: WorldSelection(worldId: retired),
          ),
        ),
        throwsFormatException,
      );

      expect(processStarted, isFalse);
      expect(
        File(
          p.join(
            gameRoot().path,
            'BepInEx',
            'TopiaForge',
            'config',
            'topiaforge.worlds.json',
          ),
        ).existsSync(),
        isFalse,
      );
    });
  });
}

Future<(LocalLauncherRepository, GameInstall)> _prepareProfileLaunchRepository({
  required Directory dataRoot,
  required Directory repositoryRoot,
  required Directory gameRoot,
  required GameProcessStarter starter,
}) async {
  final repository = LocalLauncherRepository(
    dataRoot: dataRoot.path,
    repositoryRoot: repositoryRoot.path,
    knownGamePath: gameRoot.path,
    gameProcessStarter: starter,
    packageMetadataValidator: _acceptPackageMetadata,
  );
  var install = await repository.selectGameDirectory(gameRoot.path);
  final repair = await repository.installOrRepairRuntime(install);
  expect(repair.ok, isTrue);
  install = await repository.selectGameDirectory(gameRoot.path);

  final settingsFile = File(p.join(dataRoot.path, 'settings.json'));
  final settings =
      jsonDecode(settingsFile.readAsStringSync()) as Map<String, Object?>;
  settings['wineCommand'] = 'synthetic-wine';
  settingsFile.writeAsStringSync(jsonEncode(settings));
  return (repository, install);
}

File _profileManagerState(Directory gameRoot) =>
    File(p.join(gameRoot.path, 'BepInEx', 'TopiaForge', 'state.json'));
