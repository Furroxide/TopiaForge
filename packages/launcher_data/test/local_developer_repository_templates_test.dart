import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

/// Template scaffolding against the real repo templates (templates/mod/*), with data + output in temp dirs.
void main() {
  final repoRoot = p.normalize(p.join(Directory.current.path, '..', '..'));
  late Directory root;
  late LocalDeveloperRepository repository;

  setUp(() {
    root = Directory.systemTemp.createTempSync('robotopia-templates-');
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

  test('lists the seven built-in mod templates', () async {
    final templates = await repository.listModTemplates();
    expect(templates.map((template) => template.id).toList(), [
      'asset',
      'gamemode',
      'gameplay',
      'minimal',
      'service',
      'ui',
      'world',
    ]);
  });

  test('every template scaffolds a valid manifest with no leftover tokens', () async {
    for (final template in await repository.listModTemplates()) {
      final workspace = await repository.createModProject(
        parentDirectory: p.join(root.path, template.id),
        id: 'test.${template.id}',
        name: 'Test ${template.id}',
        options: ModScaffoldOptions(template: template.id),
      );

      final manifestFile = File(
        p.join(workspace.projectRoot, 'robotopia.mod.json'),
      );
      expect(manifestFile.existsSync(), isTrue, reason: template.id);
      final manifest = ModManifest.fromJson(
        jsonDecode(manifestFile.readAsStringSync()) as Map<String, Object?>,
      );
      expect(
        manifest.validate().where((issue) => issue.isBlocking),
        isEmpty,
        reason: '${template.id}: ${manifest.validate().map((i) => i.message)}',
      );
      expect(manifest.id, 'test.${template.id}');

      // No unresolved {{TOKEN}} markers anywhere in the scaffolded text files.
      for (final entity in Directory(
        workspace.projectRoot,
      ).listSync(recursive: true).whereType<File>()) {
        if (!const {
          '.cs',
          '.csproj',
          '.json',
          '.md',
        }.contains(p.extension(entity.path).toLowerCase())) {
          continue;
        }
        expect(
          entity.readAsStringSync().contains('{{'),
          isFalse,
          reason: '${template.id}: ${entity.path} has unresolved tokens',
        );
      }

      // The entry source and csproj exist under the substituted names.
      final assembly = manifest.entryAssembly.replaceAll('.dll', '');
      expect(
        File(p.join(workspace.projectRoot, '$assembly.csproj')).existsSync(),
        isTrue,
        reason: template.id,
      );
    }
  });

  test('template defaults land in the manifest (gamemode)', () async {
    final workspace = await repository.createModProject(
      parentDirectory: root.path,
      id: 'test.waves',
      name: 'Waves',
      options: const ModScaffoldOptions(template: 'gamemode'),
    );
    final manifest = await repository.readModManifest(workspace.projectRoot);
    expect(manifest.category, 'Gameplay');
    expect(manifest.permissions, contains('world-service'));
    expect(
      manifest.dependencies.map((dependency) => dependency.id),
      containsAll(['robotopia.worlds', 'robotopia.robotkit']),
    );
    expect(manifest.loadAfter, contains('robotopia.worlds'));
    expect(manifest.worldGamemodes, hasLength(1));
    expect(manifest.worldGamemodes.first.id, 'test.waves.mode');
    expect(manifest.worldGamemodes.first.name, 'Waves');
  });

  test('scaffold flag overrides beat template defaults', () async {
    final workspace = await repository.createModProject(
      parentDirectory: root.path,
      id: 'test.overrides',
      name: 'Overrides',
      options: ModScaffoldOptions(
        template: 'gameplay',
        description: 'Custom description',
        license: 'Apache-2.0',
        category: 'DevTool',
        authorName: 'Charl',
        tags: const ['custom-tag'],
        permissions: const ['time'],
        dependencies: [
          ModDependency(
            id: 'robotopia.chronos',
            versionRange: VersionRange.parse('>=0.1.0'),
          ),
        ],
        conflicts: const [ModConflict(id: 'other.mod')],
        gameVersionRange: VersionRange.parse('>=0.1.0 <0.2.0'),
      ),
    );
    final manifest = await repository.readModManifest(workspace.projectRoot);
    expect(manifest.description, 'Custom description');
    expect(manifest.license, 'Apache-2.0');
    expect(manifest.category, 'DevTool');
    expect(manifest.author.name, 'Charl');
    expect(manifest.tags, ['custom-tag']);
    // Repeatable list flags merge with template defaults instead of clobbering them.
    expect(manifest.permissions, containsAll(['input', 'physics', 'time']));
    expect(
      manifest.dependencies.map((dependency) => dependency.id),
      contains('robotopia.chronos'),
    );
    expect(manifest.conflicts.map((conflict) => conflict.id), ['other.mod']);
    expect(manifest.gameVersionRange.toString(), '>=0.1.0 <0.2.0');
  });

  test('asset template scaffolds the unity companion by default', () async {
    final workspace = await repository.createModProject(
      parentDirectory: root.path,
      id: 'test.assetpack',
      name: 'Asset Pack',
      options: const ModScaffoldOptions(template: 'asset'),
    );
    expect(workspace.project!.unityCompanion.enabled, isTrue);
    expect(
      Directory(
        p.join(
          workspace.projectRoot,
          'unity-companion',
          'Packages',
          'com.robotopia.ugc-companion',
        ),
      ).existsSync(),
      isTrue,
    );
  });

  test('unknown template fails loudly', () async {
    expect(
      () => repository.createModProject(
        parentDirectory: root.path,
        id: 'test.unknown',
        name: 'Unknown',
        options: const ModScaffoldOptions(template: 'does-not-exist'),
      ),
      throwsA(isA<StateError>()),
    );
  });

  test('live sync scaffold stores settings and implies the companion', () async {
    final workspace = await repository.createModProject(
      parentDirectory: root.path,
      id: 'test.live',
      name: 'Live',
      options: const ModScaffoldOptions(
        liveSync: UgcLiveSyncSettings(watchFolder: r'C:\ugc-watch'),
      ),
    );
    expect(workspace.project!.unityCompanion.enabled, isTrue);
    expect(
      workspace.project!.unityCompanion.liveSync.watchFolder,
      r'C:\ugc-watch',
    );
  });

  test('updateModManifest round-trips schema fields and validates', () async {
    final workspace = await repository.createModProject(
      parentDirectory: root.path,
      id: 'test.roundtrip',
      name: 'Roundtrip',
      options: const ModScaffoldOptions(),
    );
    final manifest = await repository.readModManifest(workspace.projectRoot);
    final map = manifest.toJson();
    map['version'] = '0.2.0';
    map['legacyPackages'] = ['old.package'];
    final issues = await repository.updateModManifest(
      workspace.projectRoot,
      ModManifest.fromJson(map),
    );
    expect(issues.where((issue) => issue.isBlocking), isEmpty);

    final reread = await repository.readModManifest(workspace.projectRoot);
    expect(reread.version, '0.2.0');
    expect(reread.legacyPackages, ['old.package']);
  });

  test('ensureUgcCompanionPackage copies and is idempotent', () async {
    final project = Directory(p.join(root.path, 'UnityWorld'))
      ..createSync(recursive: true);
    expect(await repository.ensureUgcCompanionPackage(project.path), isTrue);
    final marker = File(
      p.join(
        project.path,
        'Packages',
        'com.robotopia.ugc-companion',
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
    expect(p.basename(path), 'RobotopiaUgcCompanion.json');
    expect(seed['watchFolder'], r'C:\ugc-watch');
    expect(seed['projectName'], 'Seed World');
    expect(seed['sceneId'], 'main');
    expect(seed['liveSync'], isTrue);
    expect(seed['seededUtc'], isNotEmpty);
  });
}
