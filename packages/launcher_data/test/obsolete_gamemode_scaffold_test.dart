import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  for (final template in ['minimal', 'gamemode']) {
    test('obsolete $template gamemode input fails before any write', () async {
      final root = Directory.systemTemp.createTempSync('topiaforge-obsolete-');
      addTearDown(() => root.deleteSync(recursive: true));
      final parent = p.join(root.path, 'new-parent');
      final data = p.join(root.path, 'new-data');
      final repository = LocalDeveloperRepository(
        dataRoot: data,
        repositoryRoot: p.join(root.path, 'no-sdk-or-templates'),
      );
      Object? failure;
      try {
        await repository.createModProject(
          parentDirectory: parent,
          id: 'sample.mode',
          name: 'Mode',
          includeUnityCompanion: true,
          options: ModScaffoldOptions(
            template: template,
            gamemodes: const [
              GamemodeDefinition(id: 'sample.mode.round', name: 'Round'),
            ],
          ),
        );
      } on Object catch (error) {
        failure = error;
      }
      expect(
        [Directory(parent).existsSync(), Directory(data).existsSync()],
        [false, false],
        reason:
            'Rejection must precede project, companion, SDK and registry writes.',
      );
      expect(
        failure,
        isA<ArgumentError>().having(
          (error) => error.message.toString(),
          'repair guidance',
          allOf(
            contains('--template gamemode'),
            contains('contributions.gamemodes'),
          ),
        ),
      );
    });
  }
}
