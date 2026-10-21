part of 'topiaforge_cli_test.dart';

void _restoreCliTests(_CliTestHarness Function() currentHarness) {
  test('restore fails, saying why, when NuGet resolves an SDK package other '
      'than the pin', () async {
    final created = await currentHarness().runCli([
      'new',
      'mod',
      't.restorecheck',
      '--dir',
      currentHarness().temp.path,
    ]);
    expect(created.exitCode, 0, reason: '${created.stdout}\n${created.stderr}');
    final projectDir = p.join(currentHarness().temp.path, 't.restorecheck');

    final clean = await currentHarness().runCli([
      'restore',
      '--project',
      projectDir,
    ]);
    expect(clean.exitCode, 0, reason: '${clean.stdout}\n${clean.stderr}');
    expect(
      '${clean.stdout}',
      contains('SDK NuGet packages and lock files are current.'),
    );

    // An item the pin cannot see, after the project's own.
    File(p.join(projectDir, 'Directory.Build.targets')).writeAsStringSync('''
<Project>
  <ItemGroup>
    <PackageReference Remove="TopiaForge.Mods.Abstractions" />
  </ItemGroup>
</Project>
''');
    final restore = await currentHarness().runCli([
      'restore',
      '--project',
      projectDir,
    ]);

    expect(restore.exitCode, 1, reason: '${restore.stdout}\n${restore.stderr}');
    expect(
      '${restore.stdout}',
      contains(
        'error: TF1102: NuGet resolved no TopiaForge.Mods.Abstractions for ',
      ),
    );
    expect('${restore.stdout}', contains('Directory.Build.targets'));
    expect('${restore.stdout}', isNot(contains('lock files are current')));
  }, timeout: const Timeout(Duration(minutes: 5)));
}
