part of 'topiaforge_cli_test.dart';

void _migrationCliTests(_CliTestHarness Function() currentHarness) {
  Map<String, Object?> manifest(int version) => {
    'schemaVersion': version,
    'name': 'test.migration',
    'displayName': 'Migration',
    'version': '1.0.0',
    'author': {'name': 'Tester'},
    'entryAssembly': 'Test.dll',
    'entryType': 'Test.Mod',
    'supportedGameVersionRange': '*',
    'supportedLoaderVersionRange': '*',
    'supportedSdkVersionRange': '*',
  };
  File write(Map<String, Object?> json) {
    final file = File(
      p.join(currentHarness().temp.path, 'topiaforge.mod.json'),
    );
    file.writeAsStringSync(jsonEncode(json));
    return file;
  }

  for (final version in [3, 4, 5]) {
    test(
      'migration V$version preserves optional presence and untouched values',
      () async {
        final input = {
          ...manifest(version),
          'description': '',
          'tags': <Object?>[],
          'optionalDependencies': <String, Object?>{},
          'x-test': {
            'null': null,
            'values': [true, 1, 'text'],
          },
        };
        final file = write(input);
        final result = await currentHarness().runCli([
          'migrate-manifest',
          '--project',
          file.parent.path,
        ]);
        expect(result.exitCode, 0, reason: '${result.stdout} ${result.stderr}');
        final output = jsonDecode(file.readAsStringSync()) as Map;
        expect(output['schemaVersion'], 6);
        for (final field in [
          'description',
          'tags',
          'optionalDependencies',
          'x-test',
        ]) {
          expect(output.containsKey(field), isTrue, reason: field);
          expect(output[field], input[field], reason: field);
        }
        expect(output.containsKey('contributions'), isFalse);
      },
    );
    for (final stub in [false, true]) {
      test(
        'migration V$version malformed legacy entry refuses stub=$stub without writes',
        () async {
          final file = write({
            ...manifest(version),
            'worldGamemodes': [42],
          });
          final before = file.readAsBytesSync();
          final result = await currentHarness().runCli([
            'migrate-manifest',
            file.parent.path,
            if (stub) '--stub',
          ]);
          expect(result.exitCode, isNot(0));
          expect(file.readAsBytesSync(), before);
          expect(
            '${result.stdout} ${result.stderr}',
            contains('worldGamemodes'),
          );
          expect('${result.stdout} ${result.stderr}', contains('0'));
        },
      );
    }
    test(
      'migration V$version explicit incomplete stub stays invalid',
      () async {
        final file = write({
          ...manifest(version),
          'worldGamemodes': [
            {'id': 'test.migration.mode', 'name': 'Mode', 'description': ''},
          ],
        });
        final result = await currentHarness().runCli([
          'migrate-manifest',
          file.parent.path,
          '--stub',
        ]);
        expect(result.exitCode, 1, reason: '${result.stdout} ${result.stderr}');
        final output = jsonDecode(file.readAsStringSync()) as Map;
        expect(output['schemaVersion'], 6);
        expect(output.containsKey('worldGamemodes'), isFalse);
        expect(output.containsKey('x-migration-todo'), isTrue);
        final modes = (output['contributions'] as Map)['gamemodes'] as List;
        expect((modes.single as Map)['id'], 'test.migration.mode');
        expect((modes.single as Map).containsKey('implementation'), isFalse);
        expect((modes.single as Map).containsKey('worldRequirements'), isFalse);
        final validate = await currentHarness().runCli([
          'check',
          'package',
          file.parent.path,
        ]);
        expect(validate.exitCode, isNot(0));
        expect(
          '${validate.stdout} ${validate.stderr}',
          contains('implementation'),
        );
      },
    );
  }
  for (final args in [
    ['--unknown'],
    ['--project'],
    ['--stub', '--stub'],
    ['--project', 'one', '--project', 'two'],
    ['one', 'two'],
  ]) {
    test(
      'migration rejects ambiguous or unknown arguments $args before writes',
      () async {
        final file = write(manifest(5));
        final before = file.readAsBytesSync();
        final result = await currentHarness().runCli([
          'migrate-manifest',
          ...args,
        ], workingDirectory: file.parent.path);
        expect(result.exitCode, isNot(0));
        expect(file.readAsBytesSync(), before);
      },
    );
  }
  for (final action in ['add', 'remove']) {
    test(
      'mod $action gamemode gives retirement guidance before filesystem access',
      () async {
        final result = await currentHarness().runCli([
          'mod',
          action,
          'gamemode',
          '--project',
          p.join(currentHarness().temp.path, 'absent'),
        ]);
        expect(result.exitCode, isNot(0));
        final text = '${result.stdout} ${result.stderr}';
        expect(text, contains('--template gamemode'));
        expect(text, contains('contributions'));
        expect(
          Directory(p.join(currentHarness().temp.path, 'absent')).existsSync(),
          isFalse,
        );
      },
    );
  }
  test(
    'migrate-manifest converts V3 dependency forms to canonical V6',
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
          'supportedGameVersionRange': '*',
          'supportedLoaderVersionRange': '*',
          'supportedSdkVersionRange': '*',
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
      expect(migrated['schemaVersion'], 6);
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

  test(
    'migrate-manifest upgrades retired V4 without adding multiplayer',
    () async {
      final created = await currentHarness().runCli([
        'new',
        'mod',
        't.migrate-v4',
        '--dir',
        currentHarness().temp.path,
      ]);
      expect(
        created.exitCode,
        0,
        reason: '${created.stdout}\n${created.stderr}',
      );
      final projectDir = p.join(currentHarness().temp.path, 't.migrate-v4');
      final manifestFile = File(p.join(projectDir, 'topiaforge.mod.json'));
      final manifest =
          jsonDecode(manifestFile.readAsStringSync()) as Map<String, Object?>;
      manifest['schemaVersion'] = 4;
      manifestFile.writeAsStringSync(jsonEncode(manifest));

      final result = await currentHarness().runCli([
        'migrate-manifest',
        '--project',
        projectDir,
      ]);
      expect(result.exitCode, 0, reason: '${result.stdout}\n${result.stderr}');
      final migrated =
          jsonDecode(manifestFile.readAsStringSync()) as Map<String, Object?>;
      expect(migrated['schemaVersion'], 6);
      expect(migrated, isNot(contains('multiplayer')));
      expect(result.stdout, contains('schema V4 to V6'));
    },
  );
}
