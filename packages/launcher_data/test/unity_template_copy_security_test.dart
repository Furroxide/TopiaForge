import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  test(
    'Unity project creation rejects links without a partial target',
    () async {
      if (Platform.isWindows) return;
      final root = Directory.systemTemp.createTempSync('unity-copy-security-');
      addTearDown(() => root.deleteSync(recursive: true));
      final repo = Directory(p.join(root.path, 'repo'))..createSync();
      final template = Directory(
        p.join(repo.path, 'templates', 'Robotopia.UnityWorldTemplate'),
      )..createSync(recursive: true);
      File(
        p.join(template.path, 'Packages', 'vpm-manifest.json'),
      ).createSync(recursive: true);
      File(p.join(template.path, 'ProjectSettings', 'ProjectVersion.txt'))
        ..createSync(recursive: true)
        ..writeAsStringSync('m_EditorVersion: 6000.0.23f1\n');
      final outside = File(p.join(root.path, 'outside.txt'))
        ..writeAsStringSync('secret');
      Link(
        p.join(template.path, 'Assets', 'linked.txt'),
      ).createSync(outside.path, recursive: true);
      final repository = LocalDeveloperRepository(
        dataRoot: p.join(root.path, 'data'),
        repositoryRoot: repo.path,
      );

      await expectLater(
        repository.createUnityProject(
          parentDirectory: root.path,
          name: 'Broken World',
        ),
        throwsA(predicate((error) => error.toString().contains('symlink'))),
      );
      expect(
        Directory(p.join(root.path, 'Broken_World')).existsSync(),
        isFalse,
      );
      expect(
        root.listSync().where(
          (entity) =>
              p.basename(entity.path).startsWith('Broken_World.robotopia-new-'),
        ),
        isEmpty,
      );
    },
  );
}
