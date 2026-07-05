part of 'robotopia_cli_test.dart';

void _coreCliTests(_CliTestHarness Function() currentHarness) {
  test('prints help for the public robotopia executable', () async {
    final result = await currentHarness().runCli(['help']);

    expect(result.exitCode, 0);
    expect(result.stdout.toString(), contains('robotopia new mod'));
    expect(result.stdout.toString(), contains('robotopia restore'));
    expect(result.stdout.toString(), contains('robotopia updates index'));
    expect(result.stdout.toString(), contains('robotopia mod set'));
    expect(result.stdout.toString(), contains('robotopia ugc setup'));
    expect(result.stdout.toString(), contains('robotopia ugc dev'));
    expect(
      result.stdout.toString(),
      contains('robotopia release build-package'),
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
      expect(errText, contains('robotopia help'));
      final combined = '${result.stdout}$errText';
      expect(combined, isNot(contains('robotopia ugc watch')));
    },
  );

  test('unknown command suggests a near-miss command', () async {
    final result = await currentHarness().runCli(['isntall']);

    expect(result.exitCode, 2);
    expect(
      result.stderr.toString(),
      contains('Did you mean: robotopia install?'),
    );
  });

  test('check without a subcommand prints usage and exits 2', () async {
    final result = await currentHarness().runCli(['check']);

    expect(result.exitCode, 2);
    expect(
      result.stderr.toString(),
      contains('Usage: robotopia check project|package'),
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
      contains('Usage: robotopia release build-package|test-package'),
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
        '--permission',
        'hud',
        '--dependency',
        'robotopia.chronos@>=0.1.0',
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
                  p.join(projectDir, 'robotopia.mod.json'),
                ).readAsStringSync(),
              )
              as Map<String, Object?>;
      expect(manifest[r'$schema'], contains('robotopia.mod.schema.json'));
      expect(manifest['displayName'], 'Demo Mode');
      expect(manifest['tags'], ['alpha', 'beta']);
      expect((manifest['author'] as Map)['name'], 'Charl');
      expect(
        (manifest['vpmDependencies'] as Map).keys,
        containsAll([
          'robotopia.worlds',
          'robotopia.robotkit',
          'robotopia.chronos',
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
      'permission',
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
              File(p.join(projectDir, 'robotopia.mod.json')).readAsStringSync(),
            )
            as Map<String, Object?>;
    expect(manifest['version'], '0.2.0');
    expect(manifest['permissions'], contains('time'));

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
              File(p.join(projectDir, 'robotopia.mod.json')).readAsStringSync(),
            )
            as Map<String, Object?>;
    expect(unchanged['version'], '0.2.0');
  });
}
