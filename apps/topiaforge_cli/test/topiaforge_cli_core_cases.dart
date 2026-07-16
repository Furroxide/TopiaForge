part of 'topiaforge_cli_test.dart';

void _coreCliTests(_CliTestHarness Function() currentHarness) {
  test('prints help for the public topiaforge executable', () async {
    final result = await currentHarness().runCli(['help']);

    expect(result.exitCode, 0);
    expect(result.stdout.toString(), contains('topiaforge new mod'));
    expect(result.stdout.toString(), contains('topiaforge restore'));
    expect(result.stdout.toString(), contains('topiaforge dev'));
    expect(result.stdout.toString(), contains('topiaforge updates index'));
    expect(result.stdout.toString(), contains('topiaforge mod set'));
    expect(result.stdout.toString(), contains('topiaforge check scaffold'));
    expect(result.stdout.toString(), contains('topiaforge ugc setup'));
    expect(result.stdout.toString(), contains('topiaforge ugc dev'));
    expect(
      result.stdout.toString(),
      contains('topiaforge release build-package'),
    );
    expect(result.stdout.toString(), contains('Getting started:'));
    expect(result.stdout.toString(), contains('Build & run:'));
    expect(result.stdout.toString(), contains('Project & manifest:'));
  });

  test(
    'unknown command exits 2 with a short pointer, not the full help',
    () async {
      final result = await currentHarness().runCli([
        'definitely-not-a-command',
      ]);

      expect(result.exitCode, 2);
      final errText = result.stderr.toString();
      expect(errText, contains('Unknown command: definitely-not-a-command'));
      expect(errText, contains('topiaforge help'));
      final combined = '${result.stdout}$errText';
      expect(combined, isNot(contains('topiaforge ugc watch')));
    },
  );

  test('unknown command suggests a near-miss command', () async {
    final result = await currentHarness().runCli(['isntall']);

    expect(result.exitCode, 2);
    expect(
      result.stderr.toString(),
      contains('Did you mean: topiaforge install?'),
    );
  });

  test('check without a subcommand prints usage and exits 2', () async {
    final result = await currentHarness().runCli(['check']);

    expect(result.exitCode, 2);
    expect(
      result.stderr.toString(),
      contains('Usage: topiaforge check project|package'),
    );
    expect(result.stderr.toString(), isNot(contains('Bad state:')));
  });

  test('check package on a nonexistent path fails with guidance', () async {
    final result = await currentHarness().runCli([
      'check',
      'package',
      p.join(currentHarness().temp.path, 'does-not-exist'),
    ]);

    expect(result.exitCode, 1);
    expect(result.stderr.toString().trim(), isNotEmpty);
  });

  test('release command usage errors stay concise', () async {
    final result = await currentHarness().runCli(['release']);

    expect(result.exitCode, 2);
    expect(
      result.stderr.toString(),
      contains('Usage: topiaforge release build-package|build-sdk-payload'),
    );
  });

  test('release build-package requires a platform', () async {
    final result = await currentHarness().runCli([
      'release',
      'build-package',
      '--output',
      currentHarness().temp.path,
    ]);

    expect(result.exitCode, 2);
    expect(
      result.stderr.toString(),
      contains('--platform windows|linux|macos'),
    );
  });

  test('custom license file scaffolding is bounded and repeatable', () async {
    const licenseText = 'Custom test grant.\nAll rights reserved.\n';
    final source = File(p.join(currentHarness().temp.path, 'CUSTOM-LICENSE'))
      ..writeAsStringSync(licenseText);
    final parents = [
      p.join(currentHarness().temp.path, 'first'),
      p.join(currentHarness().temp.path, 'second'),
    ];
    for (final parent in parents) {
      Directory(parent).createSync();
      final result = await currentHarness().runCli([
        'new',
        'mod',
        'author.custom',
        '--dir',
        parent,
        '--author',
        'Tester',
        '--license',
        'LicenseRef-Custom',
        '--license-file',
        source.path,
      ]);
      expect(result.exitCode, 0, reason: '${result.stdout}\n${result.stderr}');
      expect(
        File(p.join(parent, 'author.custom', 'LICENSE.md')).readAsStringSync(),
        licenseText,
      );
    }
  });

  test(
    'scaffolds a gamemode mod with flag overrides that passes check package',
    () async {
      final created = await currentHarness().runCli([
        'new',
        'mod',
        't.demo',
        '--template',
        'gamemode',
        '--name',
        'Demo Mode',
        '--dir',
        currentHarness().temp.path,
        '--tag',
        'alpha',
        '--tag',
        'beta',
        '--capability',
        'hud',
        '--dependency',
        'io.github.furroxide.topiaforge.chronos@>=0.1.0',
        '--author',
        'Charl',
        '--license',
        'MIT',
      ]);
      expect(
        created.exitCode,
        0,
        reason: '${created.stdout}\n${created.stderr}',
      );

      final projectDir = p.join(currentHarness().temp.path, 't.demo');
      final manifest =
          jsonDecode(
                File(
                  p.join(projectDir, 'topiaforge.mod.json'),
                ).readAsStringSync(),
              )
              as Map<String, Object?>;
      expect(manifest[r'$schema'], contains('topiaforge.mod.schema.json'));
      expect(manifest['displayName'], 'Demo Mode');
      expect(manifest['tags'], ['alpha', 'beta']);
      expect((manifest['author'] as Map)['name'], 'Charl');
      expect(
        (manifest['dependencies'] as Map).keys,
        containsAll([
          'io.github.furroxide.topiaforge.worlds',
          'io.github.furroxide.topiaforge.robotkit',
          'io.github.furroxide.topiaforge.chronos',
        ]),
      );
      expect(manifest['worldGamemodes'], isNotEmpty);

      final checked = await currentHarness().runCli([
        'check',
        'package',
        projectDir,
      ]);
      expect(
        checked.exitCode,
        0,
        reason: '${checked.stdout}\n${checked.stderr}',
      );
    },
  );

  test('mod set and mod add edit the manifest with validation', () async {
    final created = await currentHarness().runCli([
      'new',
      'mod',
      't.editable',
      '--dir',
      currentHarness().temp.path,
    ]);
    expect(created.exitCode, 0, reason: '${created.stdout}\n${created.stderr}');
    final projectDir = p.join(currentHarness().temp.path, 't.editable');

    final setResult = await currentHarness().runCli([
      'mod',
      'set',
      'version',
      '0.2.0',
      '--project',
      projectDir,
    ]);
    expect(
      setResult.exitCode,
      0,
      reason: '${setResult.stdout}\n${setResult.stderr}',
    );

    final addResult = await currentHarness().runCli([
      'mod',
      'add',
      'capability',
      'time',
      '--project',
      projectDir,
    ]);
    expect(
      addResult.exitCode,
      0,
      reason: '${addResult.stdout}\n${addResult.stderr}',
    );

    final manifest =
        jsonDecode(
              File(
                p.join(projectDir, 'topiaforge.mod.json'),
              ).readAsStringSync(),
            )
            as Map<String, Object?>;
    expect(manifest['version'], '0.2.0');
    expect(manifest['capabilities'], contains('time'));

    final addModule = await currentHarness().runCli([
      'mod',
      'add',
      'robotkit',
      '--project',
      projectDir,
    ]);
    expect(
      addModule.exitCode,
      0,
      reason: '${addModule.stdout}\n${addModule.stderr}',
    );
    final withModule =
        jsonDecode(
              File(
                p.join(projectDir, 'topiaforge.mod.json'),
              ).readAsStringSync(),
            )
            as Map<String, Object?>;
    expect(
      (withModule['dependencies'] as Map).keys,
      contains('io.github.furroxide.topiaforge.robotkit'),
    );
    final project = Directory(projectDir)
        .listSync()
        .whereType<File>()
        .singleWhere((file) => p.extension(file.path) == '.csproj');
    expect(
      project.readAsStringSync(),
      contains(
        '<PackageReference Include="TopiaForge.Mods.RobotKit" Version="1.0.0" />',
      ),
    );

    final addInterop = await currentHarness().runCli([
      'mod',
      'add',
      'interop-unity',
      '--project',
      projectDir,
    ]);
    expect(
      addInterop.exitCode,
      0,
      reason: '${addInterop.stdout}\n${addInterop.stderr}',
    );
    final withInterop =
        jsonDecode(
              File(
                p.join(projectDir, 'topiaforge.mod.json'),
              ).readAsStringSync(),
            )
            as Map<String, Object?>;
    expect(withInterop['capabilities'], contains('unsafe-native'));
    expect(
      project.readAsStringSync(),
      contains('Include="TopiaForge.Mods.Interop.Unity" Version="1.0.0"'),
    );

    final removeModule = await currentHarness().runCli([
      'mod',
      'remove',
      'robotkit',
      '--project',
      projectDir,
    ]);
    expect(removeModule.exitCode, 0);
    final withoutModule =
        jsonDecode(
              File(
                p.join(projectDir, 'topiaforge.mod.json'),
              ).readAsStringSync(),
            )
            as Map<String, Object?>;
    expect(
      (withoutModule['dependencies'] as Map? ?? const {}).keys,
      isNot(contains('io.github.furroxide.topiaforge.robotkit')),
    );
    expect(
      project.readAsStringSync(),
      isNot(contains('TopiaForge.Mods.RobotKit')),
    );

    final restore = await currentHarness().runCli([
      'restore',
      '--project',
      projectDir,
    ]);
    expect(restore.exitCode, 0, reason: '${restore.stdout}\n${restore.stderr}');
    expect(
      File(p.join(projectDir, 'packages.lock.json')).readAsStringSync(),
      contains('TopiaForge.Mods.Interop.Unity'),
    );
    final devProps = File(
      p.join(projectDir, 'topiaforge.dev.props'),
    ).readAsStringSync();
    expect(devProps, contains('<RobotopiaManagedDir'));
    expect(devProps, contains('<RestorePackagesPath>'));
    final feedPath = RegExp(
      r'<TopiaForgeSdkFeed>([^<]+)</TopiaForgeSdkFeed>',
    ).firstMatch(devProps)!.group(1)!;
    final interopPackage = File(
      p.join(feedPath, 'TopiaForge.Mods.Interop.Unity.1.0.0.nupkg'),
    );
    final interopArchive = ZipDecoder().decodeBytes(
      interopPackage.readAsBytesSync(),
      verify: true,
    );
    final interopProps = interopArchive.files.singleWhere(
      (file) =>
          file.name == 'buildTransitive/TopiaForge.Mods.Interop.Unity.props',
    );
    expect(utf8.decode(interopProps.content as List<int>), contains('TF1101'));

    // An invalid edit is refused instead of written.
    final badResult = await currentHarness().runCli([
      'mod',
      'set',
      'version',
      'not-a-version',
      '--project',
      projectDir,
    ]);
    expect(badResult.exitCode, 1);
    final unchanged =
        jsonDecode(
              File(
                p.join(projectDir, 'topiaforge.mod.json'),
              ).readAsStringSync(),
            )
            as Map<String, Object?>;
    expect(unchanged['version'], '0.2.0');
  });

  test(
    'migrate-manifest converts V3 dependency forms to canonical V4',
    () async {
      final created = await currentHarness().runCli([
        'new',
        'mod',
        't.migrate',
        '--dir',
        currentHarness().temp.path,
      ]);
      expect(
        created.exitCode,
        0,
        reason: '${created.stdout}\n${created.stderr}',
      );
      final projectDir = p.join(currentHarness().temp.path, 't.migrate');
      final manifestFile = File(p.join(projectDir, 'topiaforge.mod.json'));
      manifestFile.writeAsStringSync(
        const JsonEncoder.withIndent('  ').convert({
          'schemaVersion': 3,
          'name': 't.migrate',
          'displayName': 'Migration Test',
          'version': '0.1.0',
          'author': {'name': 'Tester'},
          'entryAssembly': 'MigrationTest.dll',
          'entryType': 'MigrationTest.Mod',
          'vpmDependencies': {'required.one': '^1.0.0'},
          'dependencies': [
            {'id': 'required.two', 'version': '>=2.0.0'},
            {'id': 'optional.one', 'versionRange': '*', 'optional': true},
          ],
          'optionalDependencies': [
            {'id': 'optional.two', 'version': '~1.2.0'},
          ],
          'permissions': ['input', 'physics'],
          'conflicts': [
            {'id': 'old.mod', 'version': '<1.0.0'},
          ],
        }),
      );

      final result = await currentHarness().runCli([
        'migrate-manifest',
        '--project',
        projectDir,
      ]);
      expect(result.exitCode, 0, reason: '${result.stdout}\n${result.stderr}');

      final migrated =
          jsonDecode(manifestFile.readAsStringSync()) as Map<String, Object?>;
      expect(migrated['schemaVersion'], 4);
      expect(migrated, isNot(contains('vpmDependencies')));
      expect(migrated, isNot(contains('permissions')));
      expect(migrated['dependencies'], {
        'required.one': '>=1.0.0 <2.0.0',
        'required.two': '>=2.0.0',
      });
      expect(migrated['optionalDependencies'], {
        'optional.one': '*',
        'optional.two': '>=1.2.0 <1.3.0',
      });
      expect(migrated['capabilities'], ['input', 'physics']);
      expect(migrated['supportedGameVersionRange'], '*');
      expect(migrated['supportedLoaderVersionRange'], '*');
      expect(migrated['supportedSdkVersionRange'], '*');
      expect((migrated['conflicts'] as List).single, {
        'id': 'old.mod',
        'versionRange': '<1.0.0',
      });
    },
  );
}
