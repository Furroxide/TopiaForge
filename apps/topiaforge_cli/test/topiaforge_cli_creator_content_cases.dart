part of 'topiaforge_cli_test.dart';

void _creatorContentCliTests(_CliTestHarness Function() currentHarness) {
  test('mod add creatorcontent links the runtime and SDK contracts', () async {
    final created = await currentHarness().runCli([
      'new',
      'mod',
      't.creator-content',
      '--dir',
      currentHarness().temp.path,
    ]);
    expect(created.exitCode, 0, reason: '${created.stdout}\n${created.stderr}');
    final projectDir = p.join(currentHarness().temp.path, 't.creator-content');

    final added = await currentHarness().runCli([
      'mod',
      'add',
      'creatorcontent',
      '--project',
      projectDir,
    ]);

    expect(added.exitCode, 0, reason: '${added.stdout}\n${added.stderr}');
    final manifest =
        jsonDecode(
              File(
                p.join(projectDir, 'topiaforge.mod.json'),
              ).readAsStringSync(),
            )
            as Map<String, Object?>;
    expect(
      (manifest['dependencies'] as Map).keys,
      contains('io.github.furroxide.topiaforge.creatorcontent'),
    );
    final project = Directory(projectDir)
        .listSync()
        .whereType<File>()
        .singleWhere((file) => p.extension(file.path) == '.csproj');
    expect(
      project.readAsStringSync(),
      contains(
        '<PackageReference Include="TopiaForge.Mods.CreatorContent" Version="0.1.0-rc.1" />',
      ),
    );
  });
}
