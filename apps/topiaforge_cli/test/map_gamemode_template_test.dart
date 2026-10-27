import 'dart:convert';
import 'dart:io';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/map_gamemode_template.dart';

void main() {
  test(
    'map variant enables real template and fake test with one surveyed id',
    () async {
      final root = Directory.systemTemp.createTempSync('map-template-');
      try {
        final repository = LocalDeveloperRepository(
          repositoryRoot: p.absolute('../..'),
          dataRoot: p.join(root.path, 'data'),
        );
        final workspace = await repository.createModProject(
          parentDirectory: root.path,
          id: 'example.map-mode',
          name: 'Map mode',
          options: const ModScaffoldOptions(
            template: 'gamemode',
            license: 'MIT',
            authorName: 'Example Author',
          ),
        );
        final project = Directory(workspace.projectRoot);
        final snapshot = File(
          p.join(project.path, 'obj', 'topiaforge-map-survey.json'),
        )..createSync(recursive: true);
        snapshot.writeAsStringSync(
          jsonEncode({
            'records': [
              {'id': 'entry', 'modelId': null},
              {'id': 'crate', 'modelId': 'kit/box'},
            ],
          }),
        );
        await enableMapGamemodeTemplate(project.path);
        expect(
          File(p.join(project.path, 'topiaforge.map.props')).readAsStringSync(),
          contains('TOPIAFORGE_AUTHORED_MAP'),
        );
        final sources = project
            .listSync(recursive: true)
            .whereType<File>()
            .where((f) => f.path.endsWith('.cs'))
            .toList();
        expect(sources, hasLength(2));
        for (final file in sources) {
          expect(file.readAsStringSync(), contains('"crate"'));
          expect(
            file.readAsStringSync(),
            isNot(contains('"replace-with-surveyed-id"')),
          );
        }
        expect(
          sources.map((f) => f.readAsStringSync()).join(),
          contains('RequireMap()'),
        );
        expect(
          sources.map((f) => f.readAsStringSync()).join(),
          contains('GamemodeSessionRunner'),
        );
      } finally {
        root.deleteSync(recursive: true);
      }
    },
  );
}
