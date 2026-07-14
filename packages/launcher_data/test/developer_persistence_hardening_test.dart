import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory root;
  late LocalDeveloperRepository repository;

  setUp(() {
    root = Directory.systemTemp.createTempSync(
      'robotopia-developer-persistence-',
    );
    repository = LocalDeveloperRepository(
      dataRoot: p.join(root.path, 'data'),
      repositoryRoot: root.path,
    );
  });

  tearDown(() {
    if (root.existsSync()) {
      root.deleteSync(recursive: true);
    }
  });

  test('recovers an interrupted project-file swap from its backup', () async {
    final workspace = await repository.createModProject(
      parentDirectory: root.path,
      id: 'test.recovery',
      name: 'Recovery',
    );
    final projectFile = File(
      p.join(workspace.projectRoot, 'robotopia.project.json'),
    );
    final backup = await projectFile.rename('${projectFile.path}.bak');

    final recovered = await repository.loadDeveloperWorkspace(
      projectPath: workspace.projectRoot,
    );

    expect(recovered.project?.id, 'test.recovery');
    expect(projectFile.existsSync(), isTrue);
    expect(backup.existsSync(), isFalse);
  });

  test('rejects oversized project metadata before decoding JSON', () async {
    final projectRoot = Directory(p.join(root.path, 'oversized'))
      ..createSync(recursive: true);
    await File(
      p.join(projectRoot.path, 'robotopia.project.json'),
    ).writeAsBytes(List<int>.filled(1024 * 1024 + 1, 0x20));

    await expectLater(
      repository.loadDeveloperWorkspace(projectPath: projectRoot.path),
      throwsA(predicate((error) => error.toString().contains('1 MB limit'))),
    );
  });

  test('refuses to read a project manifest through a symbolic link', () async {
    if (Platform.isWindows) {
      return;
    }
    final workspace = await repository.createModProject(
      parentDirectory: root.path,
      id: 'test.symlink',
      name: 'Symlink',
    );
    final manifest = File(p.join(workspace.projectRoot, 'robotopia.mod.json'));
    final outside = File(p.join(root.path, 'outside.json'));
    await manifest.rename(outside.path);
    await Link(manifest.path).create(outside.path);

    await expectLater(
      repository.readModManifest(workspace.projectRoot),
      throwsA(predicate((error) => error.toString().contains('symbolic link'))),
    );
  });
}
