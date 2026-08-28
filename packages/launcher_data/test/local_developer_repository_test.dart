import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:crypto/crypto.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory root;
  late Directory dataRoot;
  late Directory repoRoot;
  late LocalDeveloperRepository repository;

  setUp(() {
    root = Directory.systemTemp.createTempSync('topiaforge-developer-data-');
    dataRoot = Directory(p.join(root.path, 'data'))..createSync();
    repoRoot = Directory(p.join(root.path, 'repo'))..createSync();
    // The built-in local source derives from dist/ packages; with no dist/ here it simply yields an
    // empty catalog, so these tests drive their own custom package source instead.
    repository = LocalDeveloperRepository(
      dataRoot: dataRoot.path,
      repositoryRoot: repoRoot.path,
    );
  });

  tearDown(() {
    if (root.existsSync()) {
      root.deleteSync(recursive: true);
    }
  });

  test('restores developer dependencies and writes C# reference props', () async {
    final basePackage = _createPackage(
      root,
      id: 'base.mod',
      version: '1.0.0',
      apiAssemblies: ['ref/Base.Api.dll'],
    );
    final featurePackage = _createPackage(
      root,
      id: 'feature.mod',
      version: '1.0.0',
    );
    final sourceFile = File(p.join(root.path, 'developer-source.json'))
      ..writeAsStringSync(
        jsonEncode({
          'packages': {
            'base.mod': {
              'displayName': 'Base API',
              'versions': {
                '1.0.0': {
                  'manifest': _manifestJson(
                    'base.mod',
                    '1.0.0',
                    apiAssemblies: ['ref/Base.Api.dll'],
                  ),
                  'url': basePackage.uri.toString(),
                  'sha256': sha256Of(basePackage),
                },
              },
            },
            'feature.mod': {
              'displayName': 'Feature',
              'versions': {
                '1.0.0': {
                  'manifest': {
                    ..._manifestJson('feature.mod', '1.0.0'),
                    'dependencies': {'base.mod': '>=1.0.0'},
                  },
                  'url': featurePackage.uri.toString(),
                  'sha256': sha256Of(featurePackage),
                },
              },
            },
          },
        }),
      );

    final workspace = await repository.createModProject(
      parentDirectory: root.path,
      id: 'creator.mod',
      name: 'Creator Mod',
      includeUnityCompanion: true,
    );
    final fallbackEntry = File(
      p.join(workspace.projectRoot, 'CreatorModMod.cs'),
    ).readAsStringSync();
    expect(fallbackEntry, contains('CreatorModMod : TopiaForgeMod'));
    expect(fallbackEntry, contains('protected override void OnLoad()'));
    expect(fallbackEntry, contains('Context.Logger.Info'));
    expect(fallbackEntry, isNot(contains('ITopiaForgeMod')));
    expect(workspace.project!.packageSources, isEmpty);
    // The Unity companion is scaffolded with a README, and the project records it.
    expect(workspace.project!.unityCompanion.enabled, isTrue);
    expect(
      File(
        p.join(workspace.projectRoot, 'unity-companion', 'README.md'),
      ).existsSync(),
      isTrue,
    );
    await repository.addProjectPackageSource(
      workspace.projectRoot,
      PackageSource(
        id: 'test.source',
        name: 'Test Source',
        url: sourceFile.uri.toString(),
      ),
    );
    await repository.addProjectDependency(
      workspace.projectRoot,
      ModDependency(id: 'feature.mod', versionRange: VersionRange.parse('1.x')),
    );

    final restored = await repository.resolveDeveloperProject(
      workspace.projectRoot,
    );

    expect(restored.hasProject, isTrue);
    expect(restored.issues.where((issue) => issue.isBlocking), isEmpty);
    expect(restored.lock!.packages.map((package) => package.id), [
      'base.mod',
      'feature.mod',
    ]);
    expect(
      File(p.join(workspace.projectRoot, 'topiaforge.lock.json')).existsSync(),
      isTrue,
    );
    final props = File(
      p.join(workspace.projectRoot, 'topiaforge.dev.props'),
    ).readAsStringSync();
    expect(props, contains('Base.Api'));
    expect(props, contains('<Private>false</Private>'));
    expect(
      File(
        p.join(
          workspace.projectRoot,
          '.topiaforge',
          'packages',
          'base.mod',
          '1.0.0',
          'extracted',
          'ref',
          'Base.Api.dll',
        ),
      ).existsSync(),
      isTrue,
    );
    final gitignore = File(
      p.join(workspace.projectRoot, '.gitignore'),
    ).readAsStringSync();
    expect(gitignore, contains('.topiaforge/packages/'));
    expect(gitignore, contains('topiaforge.dev.props'));
  });

  test('blocks developer restore when package SHA does not match', () async {
    final package = _createPackage(root, id: 'bad.mod', version: '1.0.0');
    final sourceFile = File(p.join(root.path, 'bad-source.json'))
      ..writeAsStringSync(
        jsonEncode({
          'packages': {
            'bad.mod': {
              'versions': {
                '1.0.0': {
                  'manifest': _manifestJson('bad.mod', '1.0.0'),
                  'url': package.uri.toString(),
                  'sha256': 'not-the-real-sha',
                },
              },
            },
          },
        }),
      );
    final workspace = await repository.createModProject(
      parentDirectory: root.path,
      id: 'sha.mod',
      name: 'SHA Mod',
    );
    await repository.addProjectPackageSource(
      workspace.projectRoot,
      PackageSource(
        id: 'bad.source',
        name: 'Bad Source',
        url: sourceFile.uri.toString(),
      ),
    );
    await repository.addProjectDependency(
      workspace.projectRoot,
      const ModDependency(id: 'bad.mod'),
    );

    expect(
      () => repository.resolveDeveloperProject(workspace.projectRoot),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          contains('SHA-256 mismatch'),
        ),
      ),
    );
  });

  test(
    'project registry tracks created + added projects and sniffs kind',
    () async {
      // Creating a mod project auto-registers it as a modCSharp entry.
      final workspace = await repository.createModProject(
        parentDirectory: root.path,
        id: 'reg.mod',
        name: 'Registry Mod',
      );
      var projects = await repository.listProjects();
      expect(projects, hasLength(1));
      expect(projects.single.kind, ProjectKind.modCSharp);
      expect(projects.single.name, 'Registry Mod');
      expect(
        p.canonicalize(projects.single.path),
        p.canonicalize(workspace.projectRoot),
      );

      // Adding an existing Unity world project sniffs unityWorld + reads ProjectVersion.txt.
      final unityDir = Directory(p.join(root.path, 'MyWorld'))..createSync();
      Directory(p.join(unityDir.path, 'Packages')).createSync();
      File(
        p.join(unityDir.path, 'Packages', 'vpm-manifest.json'),
      ).writeAsStringSync('{"dependencies":{}}');
      Directory(p.join(unityDir.path, 'ProjectSettings')).createSync();
      File(
        p.join(unityDir.path, 'ProjectSettings', 'ProjectVersion.txt'),
      ).writeAsStringSync('m_EditorVersion: 6000.0.23f1\n');

      projects = await repository.addExistingProject(unityDir.path);
      expect(projects, hasLength(2));
      final unity = projects.firstWhere(
        (pr) => pr.kind == ProjectKind.unityWorld,
      );
      expect(unity.unityVersion, '6000.0.23f1');

      // Adding an unrecognized directory throws.
      final bogus = Directory(p.join(root.path, 'bogus'))..createSync();
      expect(
        () => repository.addExistingProject(bogus.path),
        throwsA(isA<StateError>()),
      );

      // Re-adding the same path dedupes (no duplicate entry).
      await repository.addExistingProject(unityDir.path);
      expect(await repository.listProjects(), hasLength(2));

      // Removing untracks without deleting files.
      projects = await repository.removeProject(unityDir.path);
      expect(projects, hasLength(1));
      expect(unityDir.existsSync(), isTrue);

      // touchProjectOpened stamps lastOpenedUtc.
      projects = await repository.touchProjectOpened(workspace.projectRoot);
      expect(projects.single.lastOpenedUtc, isNotEmpty);
    },
  );

  test('project registry rejects an old mod project schema', () async {
    final oldProject = Directory(p.join(root.path, 'old-project'))
      ..createSync();
    File(p.join(oldProject.path, 'topiaforge.project.json')).writeAsStringSync(
      jsonEncode({
        'schemaVersion': 1,
        'id': 'author.old',
        'name': 'Old Project',
      }),
    );

    await expectLater(
      repository.addExistingProject(oldProject.path),
      throwsFormatException,
    );
    expect(await repository.listProjects(), isEmpty);
  });

  test('runSetup performs safe fixes and returns an environment', () async {
    final result = await repository.runSetup();

    // checkEnvironment ran (the .NET SDK is always probed) and an action log is produced.
    expect(result.environment.checks.any((c) => c.name == '.NET SDK'), isTrue);
    expect(result.actions, isNotEmpty);
    // The developer data root is ensured.
    expect(Directory(repository.developerDataRoot).existsSync(), isTrue);
  });

  test('Unity Editor check falls back to the Hub install-root scan', () async {
    // Machine-dependent by nature: the invariant is that editor detection never
    // throws, and that a Hub-scanned editor implies the check is not "missing"
    // even when Unity is absent from PATH.
    final environment = await repository.checkEnvironment();
    final unityCheck = environment.checks.firstWhere(
      (check) => check.name == 'Unity Editor',
    );
    final editors = await repository.listUnityEditors();
    if (editors.isNotEmpty) {
      expect(unityCheck.status, isNot(ToolStatus.missing));
    } else {
      expect(unityCheck.status, ToolStatus.missing);
    }
  });
}

File _createPackage(
  Directory root, {
  required String id,
  required String version,
  List<String> apiAssemblies = const [],
}) {
  final package = File(p.join(root.path, '$id-$version.topiaforgemod'));
  final archive = Archive()
    ..addFile(
      ArchiveFile.string(
        'topiaforge.mod.json',
        jsonEncode(_manifestJson(id, version, apiAssemblies: apiAssemblies)),
      ),
    )
    ..addFile(ArchiveFile.string('${_assemblyName(id)}.dll', 'dll'));
  for (final assembly in apiAssemblies) {
    archive.addFile(ArchiveFile.string(assembly, 'api'));
  }
  package.writeAsBytesSync(ZipEncoder().encode(archive));
  return package;
}

Map<String, Object?> _manifestJson(
  String id,
  String version, {
  List<String> apiAssemblies = const [],
}) => {
  'schemaVersion': 5,
  'name': id,
  'displayName': id,
  'version': version,
  'author': {'name': 'TopiaForge'},
  'entryAssembly': '${_assemblyName(id)}.dll',
  'entryType': '$id.Entry',
  'supportedGameVersionRange': '*',
  'supportedLoaderVersionRange': '*',
  'supportedSdkVersionRange': '*',
  if (apiAssemblies.isNotEmpty) 'apiAssemblies': apiAssemblies,
};

String sha256Of(File file) => sha256.convert(file.readAsBytesSync()).toString();

String _assemblyName(String id) {
  return id
      .split(RegExp(r'[^A-Za-z0-9]+'))
      .where((part) => part.isNotEmpty)
      .map((part) => part[0].toUpperCase() + part.substring(1))
      .join();
}
