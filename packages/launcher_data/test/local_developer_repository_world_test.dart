import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
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
    File(p.join(root.path, 'topiaforge.mod.json')).writeAsStringSync(
      jsonEncode({
        'schemaVersion': ModManifest.currentSchemaVersion,
        'name': name,
        'version': '0.1.0',
      }),
    );
    return root;
  }

  group('WorldBundleEditorGate.isEligible', () {
    test('accepts only the game player editor version', () {
      expect(WorldBundleEditorGate.isEligible('6000.0.23f1'), isTrue);
    });

    test('rejects other patches, streams, and junk', () {
      expect(WorldBundleEditorGate.isEligible('6000.0.31f1'), isFalse);
      expect(WorldBundleEditorGate.isEligible('6000.0.0f1'), isFalse);
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

    test('round-trips through topiaforge.world.json', () async {
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

    test('rejects missing and old world config discriminators', () async {
      final project = createUnityProjectShape('OldConfig');
      final file = File(p.join(project.path, WorldAuthoringConfig.fileName));
      for (final json in [
        {'worldId': 'old.world'},
        {'schemaVersion': 1, 'worldId': 'old.world'},
      ]) {
        file.writeAsStringSync(jsonEncode(json));
        await expectLater(
          repository.readWorldAuthoringConfig(project.path),
          throwsFormatException,
        );
      }
    });

    test('rejects a retired world id in a current config', () async {
      final project = createUnityProjectShape('RetiredWorld');
      File(
        p.join(project.path, WorldAuthoringConfig.fileName),
      ).writeAsStringSync(
        jsonEncode({
          'schemaVersion': 2,
          'worldId':
              'robo'
              'topia.world.old',
        }),
      );

      await expectLater(
        repository.readWorldAuthoringConfig(project.path),
        throwsFormatException,
      );
    });

    test('write rejects old schema and retired in-memory world ids', () async {
      final project = createUnityProjectShape('UnsafeWrite');
      final retired =
          'robo'
          'topia.world.retired';
      for (final config in [
        const WorldAuthoringConfig(schemaVersion: 1, worldId: 'safe.world'),
        WorldAuthoringConfig(worldId: retired),
      ]) {
        await expectLater(
          repository.writeWorldAuthoringConfig(project.path, config),
          throwsFormatException,
        );
      }
      expect(
        File(p.join(project.path, WorldAuthoringConfig.fileName)).existsSync(),
        isFalse,
      );
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
      expect(result.errorMessage, contains('topiaforge.mod.json'));
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

    test('rejects a project pinned to a different Unity editor', () async {
      final project = createUnityProjectShape('WrongProjectEditor');
      File(
        p.join(project.path, 'ProjectSettings', 'ProjectVersion.txt'),
      ).writeAsStringSync('m_EditorVersion: 6000.0.31f1\n');
      final mod = createModShape('t.wrong-project-editor');

      final result = await repository.buildWorldBundle(
        unityProjectPath: project.path,
        modPath: mod.path,
        bundleName: 'wrong-project-editor',
      );

      expect(result.success, isFalse);
      expect(result.errorMessage, contains('pinned to Unity 6000.0.31f1'));
    });

    test('rejects an explicit editor from a different Unity version', () async {
      final project = createUnityProjectShape('WrongExplicitEditor');
      File(
        p.join(project.path, 'ProjectSettings', 'ProjectVersion.txt'),
      ).writeAsStringSync('m_EditorVersion: 6000.0.23f1\n');
      final mod = createModShape('t.wrong-explicit-editor');
      final editor = File(
        p.join(tempDir.path, '6000.0.31f1', 'Editor', 'Unity'),
      )..createSync(recursive: true);

      final result = await repository.buildWorldBundle(
        unityProjectPath: project.path,
        modPath: mod.path,
        bundleName: 'wrong-explicit-editor',
        unityExePath: editor.path,
      );

      expect(result.success, isFalse);
      expect(result.errorMessage, contains('No eligible Unity editor'));
    });

    test('probes an explicit editor instead of trusting its folder', () async {
      final project = createUnityProjectShape('SpoofedExplicitEditor');
      File(
        p.join(project.path, 'ProjectSettings', 'ProjectVersion.txt'),
      ).writeAsStringSync('m_EditorVersion: 6000.0.23f1\n');
      final mod = createModShape('t.spoofed-explicit-editor');
      final editor = File(
        p.join(tempDir.path, '6000.0.23f1', 'Editor', 'Unity'),
      )..createSync(recursive: true);
      final probingRepository = LocalDeveloperRepository(
        dataRoot: p.join(tempDir.path, 'data'),
        repositoryRoot: tempDir.path,
        unityEditorVersionProbe: (_) async => '6000.0.31f1',
      );

      final result = await probingRepository.buildWorldBundle(
        unityProjectPath: project.path,
        modPath: mod.path,
        bundleName: 'spoofed-explicit-editor',
        unityExePath: editor.path,
      );

      expect(result.success, isFalse);
      expect(result.errorMessage, contains('No eligible Unity editor'));
    });

    test('times out a hung explicit editor version probe', () async {
      final project = createUnityProjectShape('HungEditorProbe');
      File(
        p.join(project.path, 'ProjectSettings', 'ProjectVersion.txt'),
      ).writeAsStringSync('m_EditorVersion: 6000.0.23f1\n');
      final mod = createModShape('t.hung-editor-probe');
      final editor = File(p.join(tempDir.path, 'hung', 'Unity'))
        ..createSync(recursive: true);
      final probingRepository = LocalDeveloperRepository(
        dataRoot: p.join(tempDir.path, 'data'),
        repositoryRoot: tempDir.path,
        unityEditorVersionProbe: (_) =>
            Future.delayed(const Duration(seconds: 1), () => '6000.0.23f1'),
        unityEditorProbeTimeout: const Duration(milliseconds: 10),
      );

      final result = await probingRepository.buildWorldBundle(
        unityProjectPath: project.path,
        modPath: mod.path,
        bundleName: 'hung-editor-probe',
        unityExePath: editor.path,
      );

      expect(result.success, isFalse);
      expect(result.errorMessage, contains('No eligible Unity editor'));
    });
  });

  test('world output attestation verifies provenance and rejects links', () {
    final mod = createModShape('AttestedWorld');
    final assets = Directory(p.join(mod.path, 'AssetBundles'))..createSync();
    final bundle = File(p.join(assets.path, 'attested.bundle'))
      ..writeAsStringSync('bundle bytes');
    final digest = sha256.convert(bundle.readAsBytesSync()).toString();
    final manifest = File(p.join(assets.path, 'attested.manifest.json'))
      ..writeAsStringSync(
        jsonEncode({
          'bundle': 'attested.bundle',
          'worldPrefab': 'Assets/World/World.prefab',
          'editorVersion': '6000.0.23f1',
          'sha256': digest,
          'assets': ['Assets/World/World.prefab'],
        }),
      );

    final attested = repository.attestWorldBundleOutput(
      modPath: mod.path,
      bundleName: 'attested',
      worldPrefab: 'Assets/World/World.prefab',
    );
    expect(attested.sha256, digest);
    expect(attested.sizeBytes, bundle.lengthSync());
    expect(
      () => repository.attestWorldBundleOutput(
        modPath: mod.path,
        bundleName: '../attested',
        worldPrefab: 'Assets/World/World.prefab',
      ),
      throwsStateError,
    );

    manifest.writeAsStringSync(
      jsonEncode({
        'bundle': 'attested.bundle',
        'worldPrefab': 'Assets/World/World.prefab',
        'editorVersion': '6000.0.31f1',
        'sha256': digest,
        'assets': ['Assets/World/World.prefab'],
      }),
    );
    expect(
      () => repository.attestWorldBundleOutput(
        modPath: mod.path,
        bundleName: 'attested',
        worldPrefab: 'Assets/World/World.prefab',
      ),
      throwsStateError,
    );

    if (!Platform.isWindows) {
      manifest.deleteSync();
      Link(manifest.path).createSync(bundle.path);
      expect(
        () => repository.attestWorldBundleOutput(
          modPath: mod.path,
          bundleName: 'attested',
          worldPrefab: 'Assets/World/World.prefab',
        ),
        throwsStateError,
      );
    }
  });
}
