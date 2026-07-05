part of 'robotopia_cli_test.dart';

void _ugcAndWorldCliTests(_CliTestHarness Function() currentHarness) {
  test(
    'ugc setup --no-deploy persists live-sync settings into the project',
    () async {
      final created = await currentHarness().runCli([
        'new',
        'mod',
        't.synced',
        '--dir',
        currentHarness().temp.path,
      ]);
      expect(
        created.exitCode,
        0,
        reason: '${created.stdout}\n${created.stderr}',
      );
      final projectDir = p.join(currentHarness().temp.path, 't.synced');
      final watchDir = p.join(currentHarness().temp.path, 'watch');

      final setupResult = await currentHarness().runCli([
        'ugc',
        'setup',
        '--watch',
        watchDir,
        '--auto-connect',
        '--scene',
        'main',
        '--no-deploy',
        '--project',
        projectDir,
      ]);
      expect(
        setupResult.exitCode,
        0,
        reason: '${setupResult.stdout}\n${setupResult.stderr}',
      );

      final project =
          jsonDecode(
                File(
                  p.join(projectDir, 'robotopia.project.json'),
                ).readAsStringSync(),
              )
              as Map<String, Object?>;
      final liveSync = ((project['unityCompanion'] as Map)['liveSync'] as Map)
          .cast<String, Object?>();
      expect(liveSync['watchFolder'], p.normalize(p.absolute(watchDir)));
      expect(liveSync['sceneId'], 'main');
      expect(liveSync['autoConnectOnStart'], isTrue);
      expect(Directory(watchDir).existsSync(), isTrue);
    },
  );

  test(
    'ugc dev --dry-run resolves the plan without launching anything',
    () async {
      // A bare Unity-shaped project is enough for resolution.
      final unityProject = Directory(
        p.join(currentHarness().temp.path, 'World'),
      )..createSync(recursive: true);
      Directory(p.join(unityProject.path, 'ProjectSettings')).createSync();
      Directory(p.join(unityProject.path, 'Assets')).createSync();

      final result = await currentHarness().runCli([
        'ugc',
        'dev',
        '--project',
        unityProject.path,
        '--watch',
        p.join(currentHarness().temp.path, 'watch'),
        '--dry-run',
      ]);
      expect(result.exitCode, 0, reason: '${result.stdout}\n${result.stderr}');
      expect(result.stdout.toString(), contains('[dry-run]'));
      expect(result.stdout.toString(), contains('Watch folder'));
      // Dry run leaves the project untouched.
      expect(
        File(
          p.join(
            unityProject.path,
            'ProjectSettings',
            'RobotopiaUgcCompanion.json',
          ),
        ).existsSync(),
        isFalse,
      );
    },
  );

  test('prints help for the update index command', () async {
    final result = await Process.run(Platform.resolvedExecutable, [
      'run',
      'robotopia',
      'updates',
      'index',
      '--help',
    ], workingDirectory: Directory.current.path);

    expect(result.exitCode, 0);
    expect(
      result.stdout.toString(),
      contains('Usage: robotopia updates index'),
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
    expect(output, contains('robotopia world link'));
    expect(output, contains('robotopia world build'));
    expect(output, contains('robotopia world play'));
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
              File(p.join(projectDir, 'robotopia.mod.json')).readAsStringSync(),
            )
            as Map<String, Object?>;
    expect(
      (manifest['vpmDependencies'] as Map).keys,
      containsAll(['robotopia.assets', 'robotopia.worlds']),
    );
    expect(manifest['permissions'], contains('asset-bundles'));
    expect(manifest['permissions'], contains('world-service'));

    // The scaffolded mod code registers the bundle world with the derived bundle name.
    final modSource = File(
      p.join(projectDir, 'TIslandMod.cs'),
    ).readAsStringSync();
    expect(modSource, contains('RegisterWorldFromBundle'));
    expect(modSource, contains('AssetBundles/t-island.bundle'));

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
                p.join(unityProject.path, 'robotopia.world.json'),
              ).readAsStringSync(),
            )
            as Map<String, Object?>;
    expect(config['worldId'], 't.paired');
    expect(config['bundleName'], 't-paired');
    expect(config['worldPrefab'], 'Assets/World/World.prefab');

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
      contains('robotopia unity build-ui-bundle'),
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
      expect(errText, contains('6000.0.x'));
    },
  );

  test('doctor prints a Recommended actions section', () async {
    final result = await currentHarness().runCli(['doctor']);

    expect(result.stdout.toString(), contains('Recommended actions:'));
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
