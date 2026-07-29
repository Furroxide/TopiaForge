part of 'topiaforge_cli_test.dart';

void _devCliTests(_CliTestHarness Function() currentHarness) {
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
      ).writeAsStringSync(jsonEncode({'id': '2309'}));

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
    },
    timeout: const Timeout(Duration(minutes: 2)),
  );
}
