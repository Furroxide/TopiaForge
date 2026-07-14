part of 'robotopia_cli_test.dart';

void _registryCliTests(_CliTestHarness Function() currentHarness) {
  test('help covers registry, bump, and exit codes', () async {
    final result = await currentHarness().runCli(['help']);

    expect(result.exitCode, 0);
    final output = result.stdout.toString();
    expect(output, contains('robotopia registry add-entry'));
    expect(output, contains('robotopia registry index'));
    expect(output, contains('robotopia mod bump'));
    expect(output, contains('Exit codes: 0 ok, 1 failure, 2 usage error.'));
  });

  test('mod bump increments the manifest version', () async {
    final created = await currentHarness().runCli([
      'new',
      'mod',
      't.bumpy',
      '--dir',
      currentHarness().temp.path,
    ]);
    expect(created.exitCode, 0, reason: '${created.stdout}\n${created.stderr}');
    final projectDir = p.join(currentHarness().temp.path, 't.bumpy');

    String version() =>
        (jsonDecode(
                  File(
                    p.join(projectDir, 'robotopia.mod.json'),
                  ).readAsStringSync(),
                )
                as Map<String, Object?>)['version']
            as String;
    final initial = version();

    final patch = await currentHarness().runCli([
      'mod',
      'bump',
      '--project',
      projectDir,
    ]);
    expect(patch.exitCode, 0, reason: '${patch.stdout}\n${patch.stderr}');
    expect(patch.stdout.toString(), contains('version: $initial ->'));

    final minor = await currentHarness().runCli([
      'mod',
      'bump',
      'minor',
      '--project',
      projectDir,
    ]);
    expect(minor.exitCode, 0, reason: '${minor.stdout}\n${minor.stderr}');
    final afterMinor = version();
    expect(afterMinor, endsWith('.0'));

    final major = await currentHarness().runCli([
      'mod',
      'bump',
      'major',
      '--project',
      projectDir,
    ]);
    expect(major.exitCode, 0, reason: '${major.stdout}\n${major.stderr}');
    final majorPart = int.parse(version().split('.').first);
    expect(majorPart, int.parse(afterMinor.split('.').first) + 1);

    final bad = await currentHarness().runCli([
      'mod',
      'bump',
      'gigantic',
      '--project',
      projectDir,
    ]);
    expect(bad.exitCode, 2);
  });

  test('check package on a zip prints sha256 and structured issues', () async {
    final good = _writeTestPackage(
      currentHarness().temp,
      manifest: {
        'schemaVersion': 2,
        'name': 'cli.checkable',
        'displayName': 'Checkable',
        'version': '1.0.0',
        'author': {'name': 'Tester'},
        'entryAssembly': 'Mod.dll',
        'entryType': 'Test.Mod',
      },
    );

    final checked = await currentHarness().runCli([
      'check',
      'package',
      good.path,
    ]);
    expect(checked.exitCode, 0, reason: '${checked.stdout}\n${checked.stderr}');
    expect(checked.stdout.toString(), contains('sha256='));

    final shaOk = await currentHarness().runCli([
      'check',
      'package',
      good.path,
      '--sha256',
      sha256.convert(good.readAsBytesSync()).toString(),
    ]);
    expect(shaOk.exitCode, 0, reason: '${shaOk.stdout}\n${shaOk.stderr}');
    expect(shaOk.stdout.toString(), contains('sha256 matches.'));

    final shaBad = await currentHarness().runCli([
      'check',
      'package',
      good.path,
      '--sha256',
      'f' * 64,
    ]);
    expect(shaBad.exitCode, 1);
    expect(shaBad.stdout.toString(), contains('sha256 mismatch'));

    // A schema-v1 manifest fails with structured issues, not a crash dump.
    final legacy = _writeTestPackage(
      currentHarness().temp,
      fileName: 'legacy.robotopiamod',
      manifest: {
        'schemaVersion': 1,
        'id': 'cli.legacy',
        'name': 'Legacy',
        'version': '1.0.0',
        'author': 'Old Timer',
        'entryAssembly': 'Mod.dll',
        'entryType': 'Test.Mod',
      },
    );
    final failed = await currentHarness().runCli([
      'check',
      'package',
      legacy.path,
    ]);
    expect(failed.exitCode, 1);
    expect(
      failed.stdout.toString(),
      contains('error: schemaVersion must be 2.'),
    );
    expect(failed.stderr.toString(), isNot(contains('Bad state:')));
  });

  test(
    'registry add-entry writes a valid entry and enforces the bar',
    () async {
      final package = _writeTestPackage(
        currentHarness().temp,
        manifest: {
          'schemaVersion': 2,
          'name': 'cli.publishable',
          'displayName': 'Publishable',
          'version': '1.0.0',
          'author': {'name': 'Tester'},
          'license': 'MIT',
          'licenseFiles': ['LICENSE'],
          'entryAssembly': 'Mod.dll',
          'entryType': 'Test.Mod',
        },
      );
      final registryDir = p.join(currentHarness().temp.path, 'registry');

      final missingUrl = await currentHarness().runCli([
        'registry',
        'add-entry',
        package.path,
      ]);
      expect(missingUrl.exitCode, 2);

      final added = await currentHarness().runCli([
        'registry',
        'add-entry',
        package.path,
        '--url',
        'https://example.com/cli.publishable-1.0.0.robotopiamod',
        '--changelog',
        'First release',
        '--output',
        registryDir,
      ]);
      expect(added.exitCode, 0, reason: '${added.stdout}\n${added.stderr}');
      final entryFile = File(p.join(registryDir, 'cli.publishable.json'));
      expect(entryFile.existsSync(), isTrue);
      final entry =
          jsonDecode(entryFile.readAsStringSync()) as Map<String, Object?>;
      final versions = (entry['versions'] as List).cast<Map>();
      expect(
        versions.single['packageSha256'],
        sha256.convert(package.readAsBytesSync()).toString(),
      );
      expect(versions.single['changelog'], 'First release');

      // Re-publishing the same version is refused (immutable releases).
      final duplicate = await currentHarness().runCli([
        'registry',
        'add-entry',
        package.path,
        '--url',
        'https://example.com/other-url.robotopiamod',
        '--output',
        registryDir,
      ]);
      expect(duplicate.exitCode, 1);
      expect(duplicate.stdout.toString(), contains('already published'));

      // A manifest with any finding (unknown permission = warning) is refused.
      final warned = _writeTestPackage(
        currentHarness().temp,
        fileName: 'warned.robotopiamod',
        manifest: {
          'schemaVersion': 2,
          'name': 'cli.warned',
          'displayName': 'Warned',
          'version': '1.0.0',
          'author': {'name': 'Tester'},
          'license': 'MIT',
          'licenseFiles': ['LICENSE'],
          'entryAssembly': 'Mod.dll',
          'entryType': 'Test.Mod',
          'permissions': ['not-a-real-permission'],
        },
      );
      final refused = await currentHarness().runCli([
        'registry',
        'add-entry',
        warned.path,
        '--url',
        'https://example.com/cli.warned-1.0.0.robotopiamod',
        '--output',
        registryDir,
      ]);
      expect(refused.exitCode, 1);
      expect(refused.stdout.toString(), contains('zero validation findings'));
      expect(
        File(p.join(registryDir, 'cli.warned.json')).existsSync(),
        isFalse,
      );
    },
  );
}
