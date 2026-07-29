import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  final repoRoot = p.normalize(p.join(Directory.current.path, '..', '..'));
  late Directory root;
  late LocalDeveloperRepository repository;

  setUp(() {
    root = Directory.systemTemp.createTempSync('topiaforge-ugc-companion-');
    repository = LocalDeveloperRepository(
      dataRoot: p.join(root.path, 'data'),
      repositoryRoot: repoRoot,
    );
  });

  tearDown(() {
    if (root.existsSync()) {
      root.deleteSync(recursive: true);
    }
  });

  test('ensureUgcCompanionPackage copies and is idempotent', () async {
    final project = Directory(p.join(root.path, 'UnityWorld'))
      ..createSync(recursive: true);
    expect(await repository.ensureUgcCompanionPackage(project.path), isTrue);
    final marker = File(
      p.join(
        project.path,
        'Packages',
        'io.github.furroxide.topiaforge.ugc-companion',
        'Editor',
        'UgcCompanionSeed.cs',
      ),
    );
    expect(marker.existsSync(), isTrue);

    // A second call without update leaves local edits alone.
    marker.writeAsStringSync('// modified');
    expect(await repository.ensureUgcCompanionPackage(project.path), isTrue);
    expect(marker.readAsStringSync(), '// modified');

    // update: true re-copies from the template.
    expect(
      await repository.ensureUgcCompanionPackage(project.path, update: true),
      isTrue,
    );
    expect(marker.readAsStringSync(), isNot('// modified'));
  });

  test('writeUgcCompanionSeed writes the ProjectSettings seed', () async {
    final project = Directory(p.join(root.path, 'SeedWorld'))
      ..createSync(recursive: true);
    final path = await repository.writeUgcCompanionSeed(
      project.path,
      watchFolder: r'C:\ugc-watch',
      projectName: 'Seed World',
      sceneId: 'main',
    );
    final seed =
        jsonDecode(File(path).readAsStringSync()) as Map<String, Object?>;
    expect(p.basename(path), 'TopiaForgeUgcCompanion.json');
    expect(seed['watchFolder'], r'C:\ugc-watch');
    expect(seed['projectName'], 'Seed World');
    expect(seed['sceneId'], 'main');
    expect(seed['liveSync'], isTrue);
    expect(seed['seededUtc'], isNotEmpty);
  });
}
