import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  for (final template in ['minimal', 'gamemode']) {
    for (final field in ['worldGamemodes', 'gamemodes']) {
      for (final value in <Object?>[
        null,
        [],
        [
          {'id': 'sample.mode.round', 'name': 'Round'},
        ],
      ]) {
        test('obsolete $template $field=$value fails before any write', () async {
          final root = Directory.systemTemp.createTempSync(
            'topiaforge-obsolete-',
          );
          addTearDown(() => root.deleteSync(recursive: true));
          final parent = p.join(root.path, 'new-parent');
          final data = p.join(root.path, 'new-data');
          final source = p.join(root.path, 'source');
          final metadata =
              File(p.join(source, 'templates/mod', template, 'template.json'))
                ..createSync(recursive: true)
                ..writeAsStringSync(
                  jsonEncode({
                    'id': template,
                    'manifestDefaults': {field: value},
                  }),
                );
          final original = metadata.readAsStringSync();
          final repository = LocalDeveloperRepository(
            dataRoot: data,
            repositoryRoot: source,
          );
          await expectLater(
            repository.createModProject(
              parentDirectory: parent,
              id: 'sample.mode',
              name: 'Mode',
              includeUnityCompanion: true,
              options: ModScaffoldOptions(template: template),
            ),
            throwsA(
              isA<ArgumentError>().having(
                (error) => error.message.toString(),
                'repair guidance',
                allOf(
                  contains('--template gamemode'),
                  contains('contributions.gamemodes'),
                ),
              ),
            ),
          );
          expect(
            [Directory(parent).existsSync(), Directory(data).existsSync()],
            [false, false],
            reason:
                'Rejection must precede project, companion, SDK and registry writes.',
          );
          expect(metadata.readAsStringSync(), original);
        });
      }
    }
  }
}
