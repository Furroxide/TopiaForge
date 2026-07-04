import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory tempDir;
  late LocalDeveloperRepository repository;

  setUp(() {
    tempDir = Directory.systemTemp.createTempSync('world_authoring_test');
    repository = LocalDeveloperRepository(
      dataRoot: p.join(tempDir.path, 'data'),
    );
  });

  tearDown(() {
    tempDir.deleteSync(recursive: true);
  });

  Directory createUnityProjectShape(String name) {
    final root = Directory(p.join(tempDir.path, name))
      ..createSync(recursive: true);
    Directory(p.join(root.path, 'ProjectSettings')).createSync();
    Directory(p.join(root.path, 'Assets')).createSync();
    return root;
  }

  Directory createModShape(String name) {
    final root = Directory(p.join(tempDir.path, name))
      ..createSync(recursive: true);
    File(p.join(root.path, 'robotopia.mod.json')).writeAsStringSync(
      jsonEncode({'schemaVersion': 2, 'name': name, 'version': '0.1.0'}),
    );
    return root;
  }

  group('WorldBundleEditorGate.isEligible', () {
    test('accepts the game player stream up to the pinned patch', () {
      expect(WorldBundleEditorGate.isEligible('6000.0.23f1'), isTrue);
      expect(WorldBundleEditorGate.isEligible('6000.0.31f1'), isTrue);
      expect(WorldBundleEditorGate.isEligible('6000.0.0f1'), isTrue);
    });

    test('rejects newer patches, other streams, and junk', () {
      expect(WorldBundleEditorGate.isEligible('6000.0.32f1'), isFalse);
      expect(WorldBundleEditorGate.isEligible('6000.5.1f1'), isFalse);
      expect(WorldBundleEditorGate.isEligible('2022.3.10f1'), isFalse);
      expect(WorldBundleEditorGate.isEligible('custom'), isFalse);
      expect(WorldBundleEditorGate.isEligible(''), isFalse);
    });
  });

  group('WorldAuthoringConfig', () {
    test('derives kebab-case bundle names from mod ids', () {
      expect(WorldAuthoringConfig.deriveBundleName('t.island'), 't-island');
      expect(WorldAuthoringConfig.deriveBundleName('My_World 2'), 'my-world-2');
      expect(WorldAuthoringConfig.deriveBundleName('---'), 'world');
    });

    test('round-trips through robotopia.world.json', () async {
      final project = createUnityProjectShape('World');
      final written = await repository.writeWorldAuthoringConfig(
        project.path,
        const WorldAuthoringConfig(
          worldId: 't.island',
          bundleName: 't-island',
          modPath: '../t.island',
        ),
      );
      expect(written.worldPrefab, WorldAuthoringConfig.defaultWorldPrefab);

      final read = await repository.readWorldAuthoringConfig(project.path);
      expect(read, isNotNull);
      expect(read!.worldId, 't.island');
      expect(read.bundleName, 't-island');
      expect(read.modPath, '../t.island');
    });

    test('reads null when the project has no config', () async {
      final project = createUnityProjectShape('Bare');
      expect(await repository.readWorldAuthoringConfig(project.path), isNull);
    });
  });

  group('buildWorldBundle failure paths (no process spawned)', () {
    test('rejects a non-Unity directory', () async {
      final result = await repository.buildWorldBundle(
        unityProjectPath: p.join(tempDir.path, 'nowhere'),
      );
      expect(result.success, isFalse);
      expect(result.errorMessage, contains('not a Unity project'));
    });

    test('requires a pairing before it will build', () async {
      final project = createUnityProjectShape('Unpaired');
      final result = await repository.buildWorldBundle(
        unityProjectPath: project.path,
      );
      expect(result.success, isFalse);
      expect(result.errorMessage, contains('world link'));
    });

    test('rejects a paired path that is not a mod directory', () async {
      final project = createUnityProjectShape('BadMod');
      final result = await repository.buildWorldBundle(
        unityProjectPath: project.path,
        modPath: p.join(tempDir.path, 'not-a-mod'),
      );
      expect(result.success, isFalse);
      expect(result.errorMessage, contains('robotopia.mod.json'));
    });

    test('requires a bundle name from config or override', () async {
      final project = createUnityProjectShape('NoBundle');
      final mod = createModShape('t.nobundle');
      final result = await repository.buildWorldBundle(
        unityProjectPath: project.path,
        modPath: mod.path,
      );
      expect(result.success, isFalse);
      expect(result.errorMessage, contains('bundle name'));
    });
  });
}
