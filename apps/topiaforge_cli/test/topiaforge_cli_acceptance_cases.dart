part of 'topiaforge_cli_test.dart';

void _acceptanceCliTests(_CliTestHarness Function() currentHarness) {
  test('acceptance command publishes help and is listed globally', () async {
    final global = await currentHarness().runCli(['help']);
    final command = await currentHarness().runCli([
      'acceptance',
      'run',
      '--help',
    ]);

    expect(global.stdout.toString(), contains('topiaforge acceptance run'));
    expect(command.exitCode, 0);
    expect(command.stdout.toString(), contains('--required-log-marker'));
    expect(command.stdout.toString(), contains('every case'));
  });

  test('acceptance command rejects malformed timeout as usage', () async {
    final result = await currentHarness().runCli([
      'acceptance',
      'run',
      '--timeout-seconds',
      '29',
    ]);

    expect(result.exitCode, 2);
    expect(result.stderr.toString(), contains('30 through 3600'));
  });

  test('acceptance command reports unknown cases with TFACCEPT104', () async {
    final result = await currentHarness().runCli([
      'acceptance',
      'run',
      '--game-dir',
      currentHarness().temp.path,
      '--case',
      'not-a-live-case',
      '--skip-runtime-install',
      '--skip-launch',
    ]);

    expect(result.exitCode, 1);
    expect(result.stderr.toString(), contains('TFACCEPT104:'));
    expect(result.stderr.toString(), contains('Remediation:'));
    expect(result.stderr.toString(), isNot(contains('Bad state:')));
  });

  test('acceptance command validates release journey as one unit', () async {
    final result = await currentHarness().runCli([
      'acceptance',
      'run',
      '--game-dir',
      currentHarness().temp.path,
      '--dev-cli',
      p.join(currentHarness().temp.path, 'topiaforge'),
      '--skip-runtime-install',
    ]);

    expect(result.exitCode, 1);
    expect(result.stderr.toString(), contains('TFACCEPT105:'));
  });
}
