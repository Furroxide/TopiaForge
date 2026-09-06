part of 'topiaforge_cli_test.dart';

void _scaffoldCliTests(_CliTestHarness Function() currentHarness) {
  for (final flags in [
    ['--gamemode', 't.obsolete.round:Round'],
    ['--gamemode'],
    ['--gamemode='],
  ]) {
    test(
      'obsolete new-mod input ${flags.join(' ')} refuses before writes',
      () async {
        final parent = p.join(currentHarness().temp.path, 'missing-parent');
        final result = await currentHarness().runCli([
          'new',
          'mod',
          't.obsolete',
          '--dir',
          parent,
          '--unity-companion',
          ...flags,
        ]);
        expect(
          Directory(parent).existsSync(),
          isFalse,
          reason: 'No project or Unity companion may be created.',
        );
        expect(
          Directory(p.join(currentHarness().temp.path, 'data')).existsSync(),
          isFalse,
          reason: 'No SDK cache or project registry may be created.',
        );
        expect(result.exitCode, 2);
        expect(
          result.stderr.toString(),
          allOf(
            contains('--template gamemode'),
            contains('contributions.gamemodes'),
          ),
        );
        expect(result.stderr.toString(), isNot(contains('Stack trace')));
      },
    );
  }

  for (final malformed in [false, true]) {
    test(
      'migrate current V6 ${malformed ? 'rejects malformed input' : 'is a no-op'} without writes',
      () async {
        final created = await currentHarness().runCli([
          'new',
          'mod',
          't.current',
          '--dir',
          currentHarness().temp.path,
        ]);
        expect(
          created.exitCode,
          0,
          reason: '${created.stdout}\n${created.stderr}',
        );
        final project = p.join(currentHarness().temp.path, 't.current');
        final file = File(p.join(project, 'topiaforge.mod.json'));
        if (malformed) {
          final map =
              jsonDecode(file.readAsStringSync()) as Map<String, Object?>;
          map['contributions'] = {'gamemodes': 'invalid-scalar'};
          file.writeAsStringSync(jsonEncode(map));
        }
        final before = file.readAsBytesSync();
        final result = await currentHarness().runCli([
          'migrate-manifest',
          '--project',
          project,
        ]);
        expect(file.readAsBytesSync(), before);
        expect(
          result.exitCode,
          malformed ? 1 : 0,
          reason: '${result.stdout}\n${result.stderr}',
        );
        if (malformed) {
          expect(result.stdout, isNot(contains('already uses')));
          expect(result.stderr, contains('gamemodes'));
        } else {
          expect(result.stdout, contains('supported schema V6'));
        }
      },
    );
  }
  test('check scaffold requires paired install evidence paths', () async {
    final result = await currentHarness().runCli([
      'check',
      'scaffold',
      currentHarness().temp.path,
      '--package',
      p.join(currentHarness().temp.path, 'example.topiaforgemod'),
    ]);

    expect(result.exitCode, 2);
    expect(result.stderr.toString(), contains('--installed-packages'));
    expect(result.stderr.toString(), isNot(contains('Bad state:')));
  });

  test('check scaffold helper failures have remediation and docs', () async {
    final isolated = Directory(
      p.join(currentHarness().temp.path, 'isolated-scaffold-check'),
    )..createSync();
    final result = await currentHarness().runCli(
      ['check', 'scaffold', isolated.path],
      workingDirectory: isolated.path,
      environment: {
        'TOPIAFORGE_PACKAGE_VALIDATOR_PATH': p.join(
          isolated.path,
          'missing-validator.dll',
        ),
      },
    );

    expect(result.exitCode, 1);
    final diagnostics = result.stderr.toString();
    expect(diagnostics, contains('TFSCF171'));
    expect(diagnostics, contains('Cause:'));
    expect(diagnostics, contains('Remediation:'));
    expect(
      diagnostics,
      contains('https://docs.topiaforge.dev/diagnostics/TFSCF171'),
    );
    expect(diagnostics, isNot(contains('Bad state:')));
  });
}
