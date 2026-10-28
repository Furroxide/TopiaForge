part of 'topiaforge_cli_test.dart';

void _devCliTests(_CliTestHarness Function() currentHarness) {
  test('dev peer options refuse invalid combinations before restore', () async {
    for (final options in [
      ['--peers', '1'],
      ['--peers', '9'],
      ['--peers', 'two'],
      ['--peers', '2', '--peers', '3'],
      ['--peers', '2', '--no-launch'],
      ['--peers', '2', '--no-wait'],
    ]) {
      final result = await currentHarness().runCli(['dev', ...options]);
      expect(result.exitCode, 2, reason: options.join(' '));
      expect(result.stdout.toString(), isNot(contains('[restore]')));
    }
  });
  test(
    'dev failures expose a stable code, remediation, and docs link',
    () async {
      final missing = p.join(currentHarness().temp.path, 'missing-project');
      final result = await currentHarness().runCli([
        'dev',
        '--project',
        missing,
        '--no-launch',
        '--no-tail',
      ]);

      expect(result.exitCode, 1);
      final error = result.stderr.toString();
      expect(error, contains('TFDEV100: restore failed.'));
      expect(error, contains('Cause:'));
      expect(error, contains('Remediation:'));
      expect(
        error,
        contains('https://docs.topiaforge.dev/diagnostics/TFDEV100'),
      );
    },
  );

  test(
    'dev runs restore through install without launching in automation',
    () async {
      _disableRemoteCacheTestSource(currentHarness());
      final created = await currentHarness().runCli([
        'new',
        'mod',
        'test.dev.loop',
        '--template',
        'minimal',
        '--dir',
        currentHarness().temp.path,
        '--author',
        'CLI Test',
        '--license',
        'MIT',
      ]);
      expect(
        created.exitCode,
        0,
        reason: '${created.stdout}\n${created.stderr}',
      );
      final project = p.join(currentHarness().temp.path, 'test.dev.loop');
      final game = Directory(p.join(currentHarness().temp.path, 'game'))
        ..createSync();
      File(p.join(game.path, 'Robotopia.exe')).writeAsStringSync('game');
      final managed = Directory(p.join(game.path, 'Robotopia_Data', 'Managed'))
        ..createSync(recursive: true);
      File(p.join(managed.path, 'UnityEngine.dll')).writeAsStringSync('unity');
      File(
        p.join(game.path, 'installed-build.json'),
      ).writeAsStringSync(jsonEncode({'id': '2545'}));

      final result = await currentHarness().runCli([
        'dev',
        '--project',
        project,
        '--game-dir',
        game.path,
        '--no-launch',
        '--no-tail',
      ]);
      expect(result.exitCode, 0, reason: '${result.stdout}\n${result.stderr}');
      final output = result.stdout.toString();
      for (final stage in const [
        'restore',
        'toolchain',
        'build',
        'contract',
        'test',
        'pack',
        'validate',
        'install',
      ]) {
        expect(output, contains('[$stage] complete.'), reason: stage);
      }
      expect(output, contains('[launch] skipped'));
      expect(output, contains('[tail] skipped'));
      expect(output, contains('TopiaForge dev loop completed:'));
      expect(
        Directory(
          p.join(
            game.path,
            'BepInEx',
            'TopiaForge',
            'packages',
            'test.dev.loop',
          ),
        ).existsSync(),
        isTrue,
      );
      final packages = Directory(project)
          .listSync(recursive: true, followLinks: false)
          .whereType<File>()
          .where((file) => p.extension(file.path) == '.topiaforgemod');
      expect(packages, isNotEmpty);
      for (final package in packages) {
        final bytes = package.readAsBytesSync();
        final digest = sha256.convert(bytes).toString();
        final cached = File(
          p.join(
            currentHarness().temp.path,
            'data',
            'package-cache',
            '$digest.topiaforgemod',
          ),
        );
        expect(cached.existsSync(), isTrue);
        expect(cached.readAsBytesSync(), bytes);
      }
    },
    timeout: const Timeout(Duration(minutes: 2)),
  );

  test('install from a file keeps its exact archive for hosting', () async {
    final harness = currentHarness();
    _disableRemoteCacheTestSource(harness);
    const assembly = 'TopiaForge.ValidTestMod.dll';
    final built = File(
      p.join(
        '..',
        '..',
        'tests',
        'TopiaForge.ValidTestMod',
        'bin',
        'Release',
        'netstandard2.1',
        assembly,
      ),
    );
    expect(
      built.existsSync(),
      isTrue,
      reason: 'Build TopiaForge.slnx -c Release before running CLI tests.',
    );
    final package = _writeTestPackage(
      harness.temp,
      manifest: {
        'schemaVersion': 6,
        'name': 'cli.cached',
        'displayName': 'Cached CLI mod',
        'version': '1.0.0',
        'author': {'name': 'TopiaForge Tests'},
        'entryAssembly': assembly,
        'entryType': 'TopiaForge.ValidTestMod.ValidMod',
        'supportedGameVersionRange': '*',
        'supportedLoaderVersionRange': '*',
        'supportedSdkVersionRange': '*',
      },
      entryAssembly: assembly,
      entryAssemblyBytes: built.readAsBytesSync(),
    );
    final game = Directory(p.join(harness.temp.path, 'game'))..createSync();
    File(p.join(game.path, 'Robotopia.exe')).writeAsStringSync('game');
    final managed = Directory(p.join(game.path, 'Robotopia_Data', 'Managed'))
      ..createSync(recursive: true);
    File(p.join(managed.path, 'UnityEngine.dll')).writeAsStringSync('unity');
    File(
      p.join(game.path, 'installed-build.json'),
    ).writeAsStringSync(jsonEncode({'id': '2545'}));
    final result = await harness.runCli([
      'install',
      package.path,
      '--game-dir',
      game.path,
    ]);
    expect(result.exitCode, 0, reason: '${result.stdout}\n${result.stderr}');
    final bytes = package.readAsBytesSync();
    final digest = sha256.convert(bytes).toString();
    final cached = File(
      p.join(
        harness.temp.path,
        'data',
        'package-cache',
        '$digest.topiaforgemod',
      ),
    );
    expect(cached.existsSync(), isTrue);
    expect(cached.readAsBytesSync(), bytes);
  });
}

void _disableRemoteCacheTestSource(_CliTestHarness harness) {
  final file = File(p.join(harness.temp.path, 'data', 'package_sources.json'));
  file.parent.createSync(recursive: true);
  file.writeAsStringSync(
    jsonEncode({
      'formatVersion': 2,
      'sources': [
        {
          'id': 'io.github.furroxide.topiaforge.official',
          'name': 'Official',
          'url': '.',
          'enabled': false,
          'builtIn': true,
        },
      ],
    }),
  );
}
