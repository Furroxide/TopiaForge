part of 'topiaforge_cli_test.dart';

void _launchCliTests(_CliTestHarness Function() currentHarness) {
  for (final arguments in <List<String>>[
    ['--target'],
    ['--target', '--world', 'test.world'],
    ['--world', 'test.world'],
    ['--main-menu', '--target', 'test.target'],
    ['--gamemode', 'none'],
    ['--target', 'test.über'],
    ['--unknown-launch-option'],
  ]) {
    test(
      'launch argument rejection is early for ${arguments.join(' ')}',
      () async {
        final harness = currentHarness();
        final game = p.join(harness.temp.path, 'missing-game');
        final result = await harness.runCli(
          ['launch', '--game-dir', game, ...arguments],
          environment: {'ROBOTOPIA_GAME_DIR': game},
        );
        expect(result.exitCode, 2);
        expect(
          result.stderr.toString(),
          anyOf(
            contains('requires'),
            contains('cannot'),
            contains('retired'),
            contains('Invalid'),
            contains('Unknown'),
          ),
        );
        expect(
          Directory(p.join(harness.temp.path, 'data')).existsSync(),
          isFalse,
          reason:
              'Invalid launch syntax must fail before repository or filesystem work.',
        );
      },
    );
  }
  for (final scenario in ['one', 'multiple', 'none', 'mismatch']) {
    test('world link requires a declared world: $scenario', () async {
      final harness = currentHarness();
      final project = Directory(p.join(harness.temp.path, 'Unity'))
        ..createSync();
      Directory(p.join(project.path, 'Assets')).createSync();
      Directory(p.join(project.path, 'ProjectSettings')).createSync();
      final mod = Directory(p.join(harness.temp.path, 'mod'))..createSync();
      final manifest = {
        'schemaVersion': 6,
        'name': 'test.link',
        'displayName': 'Link fixture',
        'version': '1.0.0',
        'description': 'World link regression',
        'supportedGameVersionRange': '*',
        'supportedLoaderVersionRange': '*',
        'supportedSdkVersionRange': '*',
        'capabilities': ['world-service', 'asset-bundles'],
        'author': {'name': 'Tests'},
        'license': 'MIT',
        'licenseFiles': ['LICENSE'],
        'entryAssembly': 'Fixture.dll',
        'entryType': 'Fixture.Entry',
        if (scenario != 'none')
          'contributions': {
            'worlds': [
              for (final suffix in [
                'world',
                if (scenario == 'multiple') 'other',
              ])
                {
                  'id': 'test.link.$suffix',
                  'name': 'Fixture world',
                  'content': {
                    'kind': 'bundle',
                    'bundle': 'AssetBundles/declared.bundle',
                    'prefab': 'assets/world/declared.prefab',
                  },
                  'transitions': ['scene-replacement'],
                  'spawn': {
                    'kind': 'authored-marker',
                    'markerName': 'SpawnPoint',
                  },
                },
            ],
          },
      };
      File(
        p.join(mod.path, 'topiaforge.mod.json'),
      ).writeAsStringSync(jsonEncode(manifest));
      final result = await harness.runCli([
        'world',
        'link',
        '--project',
        project.path,
        '--mod',
        mod.path,
        if (scenario == 'mismatch') ...['--bundle', 'wrong'],
      ]);
      final config = File(p.join(project.path, 'topiaforge.world.json'));
      if (scenario == 'one') {
        expect(
          result.exitCode,
          0,
          reason: '${result.stdout}\n${result.stderr}',
        );
        final saved = jsonDecode(config.readAsStringSync()) as Map;
        expect(saved['worldId'], 'test.link.world');
        expect(saved['bundleName'], 'declared');
        expect(saved['worldPrefab'], 'assets/world/declared.prefab');
      } else {
        expect(
          result.exitCode,
          1,
          reason: '${result.stdout}\n${result.stderr}',
        );
        expect(config.existsSync(), isFalse);
      }
    });
  }
  test('world play requires explicit target before build tooling', () async {
    final result = await currentHarness().runCli(['world', 'play']);
    expect(result.exitCode, 2);
    expect(result.stderr.toString(), contains('--target'));
  });
}
