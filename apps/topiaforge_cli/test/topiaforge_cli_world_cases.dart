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

    // The scaffold uses the owner-bound Worlds contract and SDK asset service.
    final modSource = File(
      p.join(projectDir, 'TIslandMod.cs'),
    ).readAsStringSync();
    expect(modSource, contains('new BundleWorldContent('));
    expect(modSource, contains('worlds.RegisterWorld('));
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
                p.join(unityProject.path, 'topiaforge.world.json'),
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
    final result = await currentHarness().runCli(['doctor']);

    expect(result.stdout.toString(), contains('Recommended actions:'));
  });

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
