part of 'topiaforge_cli_test.dart';

void _worldContractCliTests(_CliTestHarness Function() currentHarness) {
  for (final invalidManifest in <String>['schema', 'id']) {
    test('world link rejects an old $invalidManifest contract', () async {
      final created = await currentHarness().runCli([
        'new',
        'mod',
        't.old-world-$invalidManifest',
        '--template',
        'world',
        '--dir',
        currentHarness().temp.path,
      ]);
      expect(created.exitCode, 0);
      final modDir = p.join(
        currentHarness().temp.path,
        't.old-world-$invalidManifest',
      );
      final manifestFile = File(p.join(modDir, 'topiaforge.mod.json'));
      final manifest =
          jsonDecode(manifestFile.readAsStringSync()) as Map<String, Object?>;
      if (invalidManifest == 'schema') {
        manifest['schemaVersion'] = 2;
      } else {
        manifest['name'] =
            'robo'
            'topia.world.old';
      }
      manifestFile.writeAsStringSync(jsonEncode(manifest));
      final unityProject = Directory(
        p.join(currentHarness().temp.path, 'OldWorld-$invalidManifest'),
      )..createSync();
      Directory(p.join(unityProject.path, 'ProjectSettings')).createSync();
      Directory(p.join(unityProject.path, 'Assets')).createSync();

      final linked = await currentHarness().runCli([
        'world',
        'link',
        '--project',
        unityProject.path,
        '--mod',
        modDir,
      ]);

      expect(linked.exitCode, 1);
      expect(
        File(p.join(unityProject.path, 'topiaforge.world.json')).existsSync(),
        isFalse,
      );
    });
  }
}
