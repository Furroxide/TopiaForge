part of 'topiaforge_cli_test.dart';

void _worldCliTests(_CliTestHarness Function() currentHarness) {
  test('prints help for the update index command', () async {
    final result = await Process.run(Platform.resolvedExecutable, [
      'run',
      'topiaforge',
      'updates',
      'index',
      '--help',
    ], workingDirectory: Directory.current.path);

    expect(result.exitCode, 0);
    expect(
      result.stdout.toString(),
      contains('Usage: topiaforge updates index'),
    );
  });

  test('lists built-in templates', () async {
    final result = await currentHarness().runCli(['list', 'templates']);

    expect(result.exitCode, 0);
    final output = result.stdout.toString();
    for (final template in [
      'minimal',
      'gameplay',
      'gamemode',
      'service',
      'ui',
      'asset',
      'world',
    ]) {
      expect(output, contains('--template $template'));
    }
    expect(output, contains('unity-world'));
  });

  test('help covers the world authoring commands', () async {
    final result = await currentHarness().runCli(['help']);

    expect(result.exitCode, 0);
    final output = result.stdout.toString();
    expect(output, contains('topiaforge world link'));
    expect(output, contains('topiaforge world build'));
    expect(output, contains('topiaforge world play'));
  });

  test('scaffolds a world mod that passes check package', () async {
    final created = await currentHarness().runCli([
      'new',
      'mod',
      't.island',
      '--template',
      'world',
      '--name',
      'Sky Island',
      '--dir',
      currentHarness().temp.path,
    ]);
    expect(created.exitCode, 0, reason: '${created.stdout}\n${created.stderr}');

    final projectDir = p.join(currentHarness().temp.path, 't.island');
    final manifest =
        jsonDecode(
              File(
                p.join(projectDir, 'topiaforge.mod.json'),
              ).readAsStringSync(),
            )
            as Map<String, Object?>;
    expect(
      (manifest['dependencies'] as Map).keys,
      contains('io.github.furroxide.topiaforge.worlds'),
    );
    expect(manifest['capabilities'], contains('asset-bundles'));
    expect(manifest['capabilities'], contains('world-service'));

    final contributions = manifest['contributions'] as Map;
    final world = (contributions['worlds'] as List).single as Map;
    expect(world['id'], 't.island.world');
    expect(world['content'], {
      'kind': 'bundle',
      'bundle': 'AssetBundles/t-island.bundle',
      'prefab': 'assets/world/world.prefab',
    });
    expect(world['spawn'], {
      'kind': 'authored-marker',
      'markerName': 'SpawnPoint',
    });
    final target = (contributions['launchTargets'] as List).single as Map;
    expect(
      target['gamemode'],
      'io.github.furroxide.topiaforge.worlds.freeplay',
    );
    expect((target['world'] as Map)['default'], world['id']);
    final modSource = File(
      p.join(projectDir, 'TIslandMod.cs'),
    ).readAsStringSync();
    expect(modSource, isNot(contains('worlds.RegisterWorld(')));

    final checked = await currentHarness().runCli([
      'check',
      'package',
      projectDir,
    ]);
    expect(checked.exitCode, 0, reason: '${checked.stdout}\n${checked.stderr}');
  });

  test('world link pairs a Unity project with a mod', () async {
    final created = await currentHarness().runCli([
      'new',
      'mod',
      't.paired',
      '--template',
      'world',
      '--dir',
      currentHarness().temp.path,
    ]);
    expect(created.exitCode, 0, reason: '${created.stdout}\n${created.stderr}');
    final modDir = p.join(currentHarness().temp.path, 't.paired');

    final unityProject = Directory(
      p.join(currentHarness().temp.path, 'PairedWorld'),
    )..createSync(recursive: true);
    Directory(p.join(unityProject.path, 'ProjectSettings')).createSync();
    Directory(p.join(unityProject.path, 'Assets')).createSync();

    final linked = await currentHarness().runCli([
      'world',
      'link',
      '--project',
      unityProject.path,
      '--mod',
      modDir,
    ]);
    expect(linked.exitCode, 0, reason: '${linked.stdout}\n${linked.stderr}');

    final config =
        jsonDecode(
              File(
                p.join(unityProject.path, 'topiaforge.world.json'),
              ).readAsStringSync(),
            )
            as Map<String, Object?>;
    expect(config['worldId'], 't.paired.world');
    expect(config['bundleName'], 't-paired');
    expect(config['worldPrefab'], 'assets/world/world.prefab');

    // Dry run resolves the pairing without launching Unity.
    final dryRun = await currentHarness().runCli([
      'world',
      'build',
      '--project',
      unityProject.path,
      '--dry-run',
    ]);
    final dryRunOutput = '${dryRun.stdout}\n${dryRun.stderr}';
    expect(dryRunOutput, contains('Paired mod'));
    expect(dryRunOutput, contains('t-paired'));
  });

  test('help covers the UI bundle build command', () async {
    final result = await currentHarness().runCli(['help']);

    expect(result.exitCode, 0);
    expect(
      result.stdout.toString(),
      contains('topiaforge unity build-ui-bundle'),
    );
  });

  test(
    'unity build-ui-bundle --dry-run resolves the plan without launching Unity',
    () async {
      final result = await currentHarness().runCli([
        'unity',
        'build-ui-bundle',
        '--dry-run',
      ]);

      // Editor availability is machine-dependent; assert only the printed structure.
      final output = '${result.stdout}\n${result.stderr}';
      expect(output, contains('Unity project:'));
      expect(
        output,
        contains(p.join('tools', 'unity-ui-bundle')),
        reason: output,
      );
      expect(output, contains('Build editor:'));
      expect(output, contains('ui-bundle-build.log'));
    },
  );

  test('unity build-ui-bundle rejects a nonexistent --unity editor', () async {
    final result = await currentHarness().runCli([
      'unity',
      'build-ui-bundle',
      '--unity',
      p.join(currentHarness().temp.path, 'no-such', 'Unity.exe'),
    ]);

    expect(result.exitCode, 1);
    expect(result.stderr.toString(), contains('Unity editor not found'));
  });

  test(
    'unity build-ui-bundle gates an ineligible --unity editor version',
    () async {
      // Hub-layout folder named after a too-new editor stream.
      final editorDir = Directory(
        p.join(
          currentHarness().temp.path,
          'Hub',
          'Editor',
          '6000.5.1f1',
          'Editor',
        ),
      )..createSync(recursive: true);
      final fakeEditor = File(p.join(editorDir.path, 'Unity.exe'))
        ..writeAsStringSync('not a real editor');

      final result = await currentHarness().runCli([
        'unity',
        'build-ui-bundle',
        '--unity',
        fakeEditor.path,
      ]);

      expect(result.exitCode, 1);
      final errText = result.stderr.toString();
      expect(errText, contains('6000.5.1f1'));
      expect(errText, contains('6000.0.23f1'));
    },
  );

  test(
    'unity build-ui-bundle verifies the binary inside an eligible folder',
    () async {
      final editorDir = Directory(
        p.join(
          currentHarness().temp.path,
          'Hub',
          'Editor',
          '6000.0.23f1',
          'Editor',
        ),
      )..createSync(recursive: true);
      final fakeEditor = File(p.join(editorDir.path, 'Unity.exe'));
      File(Platform.resolvedExecutable).copySync(fakeEditor.path);
      if (!Platform.isWindows) {
        final chmod = await Process.run('chmod', ['+x', fakeEditor.path]);
        expect(chmod.exitCode, 0, reason: chmod.stderr.toString());
      }

      final result = await currentHarness().runCli([
        'unity',
        'build-ui-bundle',
        '--unity',
        fakeEditor.path,
      ]);

      expect(result.exitCode, 1);
      expect(result.stderr.toString(), contains('failed with exit code'));
    },
  );

  test('doctor prints a Recommended actions section', () async {
    final result = await currentHarness().runCliWithoutGameInstalls(['doctor']);

    expect(result.stdout.toString(), contains('Recommended actions:'));
    expect(
      result.stdout.toString(),
      contains('Robotopia install not detected'),
    );
  });

  test(
    'doctor writes no compat-status.json into installs the host exposes',
    () async {
      final temp = currentHarness().temp.path;
      final localAppData = p.join(temp, 'host-local-app-data');
      final games = [
        p.join(localAppData, 'Tomato Cake', 'launcher', 'Robotopia'),
        p.join(temp, 'host-game-dir'),
      ];
      final netstandard = await _netstandardFacade();
      for (final game in games) {
        _writeVerifiableGame(game, netstandard);
      }
      // Control: the extractor the CLI finds in a checkout gives them a verdict.
      final verdict = await Process.run(_builtExtractor(), [
        'verify',
        '--managed',
        p.join(games.last, 'Robotopia_Data', 'Managed'),
        '--format',
        'json',
      ]);
      expect(verdict.exitCode, lessThan(2), reason: '${verdict.stderr}');
      expect(
        (jsonDecode('${verdict.stdout}') as Map)['status'],
        anyOf('ok', 'broken'),
      );

      final result = await currentHarness().runCliWithoutGameInstalls(
        ['doctor'],
        hostEnvironment: {
          'LOCALAPPDATA': localAppData,
          'ROBOTOPIA_GAME_DIR': games.last,
        },
      );

      for (final game in games) {
        expect(
          File(
            p.join(game, 'BepInEx', 'TopiaForge', 'compat-status.json'),
          ).existsSync(),
          isFalse,
          reason: 'doctor cached a GameCompat verdict in $game',
        );
      }
      expect(
        result.stdout.toString(),
        contains('Robotopia install not detected'),
      );
    },
  );

  test('world build rejects a --project that is not a Unity project', () async {
    final notUnity = Directory(p.join(currentHarness().temp.path, 'PlainDir'))
      ..createSync(recursive: true);

    final result = await currentHarness().runCli([
      'world',
      'build',
      '--project',
      notUnity.path,
    ]);

    expect(result.exitCode, 1);
    expect(
      '${result.stdout}\n${result.stderr}',
      contains('is not a Unity project'),
    );
  });

  test('world build without a pairing points at world link', () async {
    final unityProject = Directory(
      p.join(currentHarness().temp.path, 'Unpaired'),
    )..createSync(recursive: true);
    Directory(p.join(unityProject.path, 'ProjectSettings')).createSync();
    Directory(p.join(unityProject.path, 'Assets')).createSync();

    final result = await currentHarness().runCli([
      'world',
      'build',
      '--project',
      unityProject.path,
    ]);
    expect(result.exitCode, 1);
    expect('${result.stdout}\n${result.stderr}', contains('world link'));
  });
}

/// Writes a game that discovery accepts and the GameCompat extractor gives a
/// verdict on, so validating it caches `BepInEx/TopiaForge/compat-status.json`
/// inside it. The extractor loads Managed with `netstandard` as its core
/// assembly and reports nothing without one; GameCode.dll only has to exist.
void _writeVerifiableGame(String root, File netstandard) {
  final managed = Directory(p.join(root, 'Robotopia_Data', 'Managed'))
    ..createSync(recursive: true);
  File(p.join(root, 'Robotopia.exe')).writeAsStringSync('game');
  File(p.join(managed.path, 'GameCode.dll')).writeAsStringSync('game code');
  netstandard.copySync(p.join(managed.path, 'netstandard.dll'));
}

/// The extractor `dotnet build TopiaForge.slnx -c Release` builds, which the
/// CLI runs when its working directory is inside the checkout.
String _builtExtractor() => p.normalize(
  p.join(
    Directory.current.absolute.path,
    '..',
    '..',
    'src',
    'TopiaForge.GameCompat.Extractor',
    'bin',
    'Release',
    'net10.0',
    Platform.isWindows
        ? 'TopiaForge.GameCompat.Extractor.exe'
        : 'TopiaForge.GameCompat.Extractor',
  ),
);

/// The netstandard facade every .NET runtime ships, which the extractor
/// accepts as its core assembly. SDKs on Linux lack the reference pack.
Future<File> _netstandardFacade() async {
  final listing = await Process.run('dotnet', const ['--list-runtimes']);
  for (final line in LineSplitter.split('${listing.stdout}')) {
    // `Microsoft.NETCore.App 10.0.9 [C:\Program Files\dotnet\shared\...]`
    final runtime = RegExp(
      r'^Microsoft\.NETCore\.App (\S+) \[(.+)\]$',
    ).firstMatch(line.trim());
    if (runtime == null) continue;
    final facade = File(
      p.join(runtime.group(2)!, runtime.group(1)!, 'netstandard.dll'),
    );
    if (facade.existsSync()) return facade;
  }
  fail(
    'No .NET runtime has a netstandard.dll:\n'
    '${listing.stdout}${listing.stderr}',
  );
}
