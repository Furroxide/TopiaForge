part of 'topiaforge_cli_test.dart';

void _registryCliTests(_CliTestHarness Function() currentHarness) {
  test('help covers registry, bump, and exit codes', () async {
    final result = await currentHarness().runCli(['help']);

    expect(result.exitCode, 0);
    final output = result.stdout.toString();
    expect(output, contains('topiaforge registry add-entry'));
    expect(output, contains('topiaforge registry index'));
    expect(output, contains('topiaforge mod bump'));
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
                    p.join(projectDir, 'topiaforge.mod.json'),
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
    const entryAssembly = 'TopiaForge.ValidTestMod.dll';
    final fixtureAssembly = File(
      p.join(
        Directory.current.path,
        '..',
        '..',
        'tests',
        'TopiaForge.ValidTestMod',
        'bin',
        'Release',
        'netstandard2.1',
        entryAssembly,
      ),
    );
    expect(
      fixtureAssembly.existsSync(),
      isTrue,
      reason: 'Build TopiaForge.slnx -c Release before running CLI tests.',
    );
    final good = _writeTestPackage(
      currentHarness().temp,
      manifest: {
        'schemaVersion': ModManifest.currentSchemaVersion,
        'name': 'cli.checkable',
        'displayName': 'Checkable',
        'version': '1.0.0',
        'author': {'name': 'Tester'},
        'entryAssembly': entryAssembly,
        'entryType': 'TopiaForge.ValidTestMod.ValidMod',
        'supportedGameVersionRange': '*',
        'supportedLoaderVersionRange': '*',
        'supportedSdkVersionRange': '*',
      },
      entryAssembly: entryAssembly,
      entryAssemblyBytes: fixtureAssembly.readAsBytesSync(),
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

    final badPe = _writeTestPackage(
      currentHarness().temp,
      fileName: 'bad-pe.topiaforgemod',
      manifest: {
        'schemaVersion': ModManifest.currentSchemaVersion,
        'name': 'cli.bad-pe',
        'displayName': 'Bad PE',
        'version': '1.0.0',
        'author': {'name': 'Tester'},
        'entryAssembly': 'Mod.dll',
        'entryType': 'Test.Mod',
        'supportedGameVersionRange': '*',
        'supportedLoaderVersionRange': '*',
        'supportedSdkVersionRange': '*',
      },
    );
    final rejectedPe = await currentHarness().runCli([
      'check',
      'package',
      badPe.path,
    ]);
    expect(rejectedPe.exitCode, 1);
    expect(rejectedPe.stderr.toString(), contains('TFPKG160'));
    expect(rejectedPe.stderr.toString(), contains('not a valid PE image'));

    // A retired schema fails at dispatch with actionable migration guidance.
    final oldContract = _writeTestPackage(
      currentHarness().temp,
      fileName: 'old-contract.topiaforgemod',
      manifest: {
        'schemaVersion': 4,
        'name': 'cli.old_contract',
        'displayName': 'Old Contract',
        'version': '1.0.0',
        'author': 'Old Timer',
        'entryAssembly': 'Mod.dll',
        'entryType': 'Test.Mod',
      },
    );
    final failed = await currentHarness().runCli([
      'check',
      'package',
      oldContract.path,
    ]);
    expect(failed.exitCode, 1);
    expect(failed.stderr.toString(), contains('retired before TopiaForge 1.0'));
    expect(failed.stderr.toString(), contains('topiaforge migrate-manifest'));
    expect(failed.stderr.toString(), isNot(contains('Bad state:')));
  });

  test(
    'registry add-entry writes a valid entry and enforces the bar',
    () async {
      final package = _writeTestPackage(
        currentHarness().temp,
        manifest: {
          'schemaVersion': ModManifest.currentSchemaVersion,
          'name': 'cli.publishable',
          'displayName': 'Publishable',
          'version': '1.0.0',
          'author': {'name': 'Tester'},
          'license': 'MIT',
          'licenseFiles': ['LICENSE'],
          'entryAssembly': 'Mod.dll',
          'entryType': 'Test.Mod',
          'supportedGameVersionRange': '*',
          'supportedLoaderVersionRange': '*',
          'supportedSdkVersionRange': '*',
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
        'https://example.com/cli.publishable-1.0.0.topiaforgemod',
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
        'https://example.com/other-url.topiaforgemod',
        '--output',
        registryDir,
      ]);
      expect(duplicate.exitCode, 1);
      expect(duplicate.stdout.toString(), contains('already published'));

      // A manifest with any finding (unknown capability) is refused.
      final warned = _writeTestPackage(
        currentHarness().temp,
        fileName: 'warned.topiaforgemod',
        manifest: {
          'schemaVersion': ModManifest.currentSchemaVersion,
          'name': 'cli.warned',
          'displayName': 'Warned',
          'version': '1.0.0',
          'author': {'name': 'Tester'},
          'license': 'MIT',
          'licenseFiles': ['LICENSE'],
          'entryAssembly': 'Mod.dll',
          'entryType': 'Test.Mod',
          'supportedGameVersionRange': '*',
          'supportedLoaderVersionRange': '*',
          'supportedSdkVersionRange': '*',
          'capabilities': ['not-a-real-capability'],
        },
      );
      final refused = await currentHarness().runCli([
        'registry',
        'add-entry',
        warned.path,
        '--url',
        'https://example.com/cli.warned-1.0.0.topiaforgemod',
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
