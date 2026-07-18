part of 'topiaforge_cli_test.dart';

void _scaffoldCliTests(_CliTestHarness Function() currentHarness) {
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
