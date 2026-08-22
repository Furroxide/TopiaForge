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
    root = Directory.systemTemp.createTempSync('topiaforge-developer-unity-');
    dataRoot = Directory(p.join(root.path, 'data'))..createSync();
    repoRoot = Directory(p.join(root.path, 'repo'))..createSync();
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

  test(
    'createUnityProject copies the template and registers it',
    () async {
      final templateDir = Directory(
        p.join(repoRoot.path, 'templates', 'TopiaForge.UnityWorldTemplate'),
      );
      Directory(
        p.join(templateDir.path, 'Packages'),
      ).createSync(recursive: true);
      File(
        p.join(templateDir.path, 'Packages', 'vpm-manifest.json'),
      ).writeAsStringSync(
        '{"dependencies":{"io.github.furroxide.topiaforge.world-companion":"^0.1.0"}}',
      );
      Directory(p.join(templateDir.path, 'ProjectSettings')).createSync();
      File(
        p.join(templateDir.path, 'ProjectSettings', 'ProjectVersion.txt'),
      ).writeAsStringSync('m_EditorVersion: 6000.0.23f1\n');
      File(
        p.join(templateDir.path, 'README.md'),
      ).writeAsStringSync('# Template\n');
      File(
        p.join(templateDir.path, '.gitignore'),
      ).writeAsStringSync('Library/\n');
      File(
        p.join(templateDir.path, 'Assets', 'keep.txt'),
      ).createSync(recursive: true);
      for (final generated
          in 'Library/cache.bin,Build/game.bin,Logs/editor.log,'
                  'UserSettings/settings.asset,Generated.csproj,Generated.sln'
              .split(',')) {
        File(p.join(templateDir.path, generated))
          ..createSync(recursive: true)
          ..writeAsStringSync('generated');
      }
      final projects = await repository.createUnityProject(
        parentDirectory: root.path,
        name: 'My World',
      );

      expect(projects, hasLength(1));
      final created = projects.single;
      expect(created.kind, ProjectKind.unityWorld);
      expect(created.unityVersion, '6000.0.23f1');
      expect(
        File(
          p.join(created.path, 'Packages', 'vpm-manifest.json'),
        ).existsSync(),
        isTrue,
      );
      expect(
        File(p.join(created.path, 'README.md')).readAsStringSync(),
        contains('My World'),
      );
      expect(File(p.join(created.path, '.gitignore')).existsSync(), isTrue);
      expect(
        File(p.join(created.path, 'Assets', 'keep.txt')).existsSync(),
        isTrue,
      );
      for (final generated
          in 'Library,Build,Logs,UserSettings,Generated.csproj,Generated.sln'
              .split(',')) {
        expect(
          FileSystemEntity.typeSync(
            p.join(created.path, generated),
            followLinks: false,
          ),
          FileSystemEntityType.notFound,
          reason: generated,
        );
      }
      expect(
        File(
          p.join(created.path, 'Packages', 'vpm-resolver-repos.json'),
        ).existsSync(),
        isFalse,
        reason: 'new projects must not embed a machine-local repository path',
      );

      expect(
        () => repository.createUnityProject(
          parentDirectory: root.path,
          name: 'Other',
          template: 'avatar',
        ),
        throwsA(isA<StateError>()),
      );
    },
  );

  test(
    'resolveUnityProject rejects malformed VPM state without replacing it',
    () async {
      final project = Directory(p.join(root.path, 'MalformedVpm'));
      final packages = Directory(p.join(project.path, 'Packages'))
        ..createSync(recursive: true);
      final manifest = File(p.join(packages.path, 'vpm-manifest.json'));
      const original = '{"dependencies":{"com.test.pkg":';
      manifest.writeAsStringSync(original);

      await expectLater(
        repository.resolveUnityProject(project.path),
        throwsA(
          predicate(
            (error) =>
                error is StateError &&
                error.message.toString().contains(
                  'Invalid Packages/vpm-manifest.json',
                ),
          ),
        ),
      );

      expect(manifest.readAsStringSync(), original);
      expect(
        File(p.join(packages.path, 'vpm-resolver-repos.json')).existsSync(),
        isFalse,
      );
      expect(
        packages.listSync().whereType<Directory>().where(
          (entry) => p.basename(entry.path).startsWith('.topiaforge-vpm-'),
        ),
        isEmpty,
      );
    },
  );

  test('resolveUnityProject rejects an oversized VPM manifest', () async {
    final project = Directory(p.join(root.path, 'OversizedVpm'));
    final packages = Directory(p.join(project.path, 'Packages'))
      ..createSync(recursive: true);
    final manifest = File(p.join(packages.path, 'vpm-manifest.json'));
    const oversizedLength = 1024 * 1024 + 1;
    manifest.writeAsBytesSync(List<int>.filled(oversizedLength, 0x20));

    await expectLater(
      repository.resolveUnityProject(project.path),
      throwsA(
        predicate(
          (error) =>
              error is StateError &&
              error.message.toString().contains('larger than the 1 MB limit'),
        ),
      ),
    );

    expect(manifest.lengthSync(), oversizedLength);
    expect(
      File(p.join(packages.path, 'vpm-resolver-repos.json')).existsSync(),
      isFalse,
    );
  });

  test(
    'resolveUnityProject downloads + extracts packages and writes locked',
    () async {
      final indexDir = Directory(p.join(repoRoot.path, 'dist', 'vpm'))
        ..createSync(recursive: true);
      final zip = File(p.join(indexDir.path, 'com.test.pkg-1.0.0.zip'));
      final archive = Archive()
        ..addFile(
          ArchiveFile.string(
            'package.json',
            jsonEncode({'name': 'com.test.pkg', 'version': '1.0.0'}),
          ),
        );
      zip.writeAsBytesSync(ZipEncoder().encode(archive));
      final sha = sha256.convert(zip.readAsBytesSync()).toString();

      File(p.join(indexDir.path, 'index.json')).writeAsStringSync(
        jsonEncode({
          'name': 'Local',
          'id': 'io.github.furroxide.topiaforge.vpm.local',
          'packages': {
            'com.test.pkg': {
              'versions': {
                '1.0.0': {
                  'name': 'com.test.pkg',
                  'version': '1.0.0',
                  'displayName': 'Test Pkg',
                  'url': p.basename(zip.path),
                  'zipSHA256': sha,
                },
              },
            },
          },
        }),
      );

      final proj = Directory(p.join(root.path, 'UnityProj'));
      Directory(p.join(proj.path, 'Packages')).createSync(recursive: true);
      File(
        p.join(proj.path, 'Packages', 'vpm-manifest.json'),
      ).writeAsStringSync('{"dependencies":{"com.test.pkg":"^1.0.0"}}');
      final spoofed = Directory(p.join(proj.path, 'Packages', 'com.test.pkg'))
        ..createSync();
      File(p.join(spoofed.path, 'package.json')).writeAsStringSync(
        jsonEncode({'name': 'com.attacker.pkg', 'version': '1.0.0'}),
      );
      File(p.join(spoofed.path, 'stale.txt')).writeAsStringSync('old');

      final resolved = await repository.resolveUnityProject(proj.path);
      expect(resolved, hasLength(1));
      expect(resolved.single.id, 'com.test.pkg');
      expect(resolved.single.version, '1.0.0');
      expect(
        File(
          p.join(proj.path, 'Packages', 'com.test.pkg', 'package.json'),
        ).existsSync(),
        isTrue,
      );
      final installedManifest =
          jsonDecode(
                File(
                  p.join(proj.path, 'Packages', 'com.test.pkg', 'package.json'),
                ).readAsStringSync(),
              )
              as Map<String, Object?>;
      expect(installedManifest['name'], 'com.test.pkg');
      expect(
        File(
          p.join(proj.path, 'Packages', 'com.test.pkg', 'stale.txt'),
        ).existsSync(),
        isFalse,
      );
      final manifest =
          jsonDecode(
                File(
                  p.join(proj.path, 'Packages', 'vpm-manifest.json'),
                ).readAsStringSync(),
              )
              as Map<String, Object?>;
      expect((manifest['locked'] as Map).containsKey('com.test.pkg'), isTrue);
      expect(
        File(
          p.join(proj.path, 'Packages', 'vpm-resolver-repos.json'),
        ).existsSync(),
        isTrue,
      );

      final available = await repository.listAvailableUnityPackages();
      expect(available.map((info) => info.name), contains('com.test.pkg'));

      await repository.removeUnityPackage(proj.path, 'com.test.pkg');
      expect(
        Directory(p.join(proj.path, 'Packages', 'com.test.pkg')).existsSync(),
        isFalse,
      );

      File(p.join(indexDir.path, 'index.json')).writeAsStringSync(
        jsonEncode({
          'packages': {
            'com.test.pkg': {
              'versions': {
                '1.0.0': {
                  'name': 'com.test.pkg',
                  'version': '1.0.0',
                  'url': p.basename(zip.path),
                  'zipSHA256': 'deadbeef',
                },
              },
            },
          },
        }),
      );
      await expectLater(
        repository.addUnityPackage(proj.path, 'com.test.pkg', '^1.0.0'),
        throwsA(isA<StateError>()),
      );
    },
  );

  test('packUnityPackages produces deterministic zips and index', () async {
    final packageDir = Directory(
      p.join(
        repoRoot.path,
        'templates',
        'sample',
        'io.github.furroxide.topiaforge.sample',
      ),
    )..createSync(recursive: true);
    File(p.join(packageDir.path, 'z.txt')).writeAsStringSync('z');
    File(p.join(packageDir.path, 'a.txt')).writeAsStringSync('a');
    File(p.join(packageDir.path, '.gitkeep')).writeAsStringSync('');
    File(p.join(packageDir.path, 'package.json')).writeAsStringSync(
      jsonEncode({
        'name': 'io.github.furroxide.topiaforge.sample',
        'version': '1.2.3',
        'displayName': 'Sample',
      }),
    );

    final firstDir = p.join(root.path, 'first-vpm');
    final secondDir = p.join(root.path, 'second-vpm');
    await repository.packUnityPackages(outputDir: firstDir);
    await repository.packUnityPackages(outputDir: secondDir);

    final zipName = 'io.github.furroxide.topiaforge.sample-1.2.3.zip';
    final firstZip = File(p.join(firstDir, zipName)).readAsBytesSync();
    final secondZip = File(p.join(secondDir, zipName)).readAsBytesSync();
    expect(firstZip, secondZip);
    expect(
      File(p.join(firstDir, 'index.json')).readAsBytesSync(),
      File(p.join(secondDir, 'index.json')).readAsBytesSync(),
    );
    expect(
      ZipDecoder().decodeBytes(firstZip).files.map((file) => file.name),
      orderedEquals(['a.txt', 'package.json', 'z.txt']),
    );
  });

  group('openProjectInUnity', () {
    test(
      'launches the exact TopiaForge editor instead of the newest editor',
      () async {
        final project = _createUnityProject(root, 'World', '6000.0.23f1');
        final editor23 = p.join(root.path, 'Hub', '6000.0.23f1', 'Unity.exe');
        final editor31 = p.join(root.path, 'Hub', '6000.0.31f1', 'Unity.exe');
        String? launchedExecutable;
        List<String>? launchedArguments;
        final openRepository = LocalDeveloperRepository(
          dataRoot: dataRoot.path,
          repositoryRoot: repoRoot.path,
          unityEditorScanner: () async => [
            UnityEditor(version: '6000.0.31f1', path: editor31),
            UnityEditor(version: '6000.0.23f1', path: editor23),
          ],
          unityEditorLauncher: (executable, arguments) async {
            launchedExecutable = executable;
            launchedArguments = arguments;
          },
        );

        final launched = await openRepository.openProjectInUnity(project.path);

        expect(launched, editor23);
        expect(launchedExecutable, editor23);
        expect(launchedArguments, [
          '-projectPath',
          p.normalize(p.absolute(project.path)),
        ]);
      },
    );

    test(
      'blocks Unity projects pinned to a different editor version',
      () async {
        final project = _createUnityProject(root, 'NewerWorld', '6000.0.31f1');
        var launched = false;
        final openRepository = LocalDeveloperRepository(
          dataRoot: dataRoot.path,
          repositoryRoot: repoRoot.path,
          unityEditorScanner: () async => [
            UnityEditor(
              version: '6000.0.23f1',
              path: p.join(root.path, 'Unity.exe'),
            ),
          ],
          unityEditorLauncher: (_, _) async => launched = true,
        );

        await expectLater(
          openRepository.openProjectInUnity(project.path),
          throwsA(
            predicate(
              (error) =>
                  error.toString().contains('pinned to Unity 6000.0.31f1'),
            ),
          ),
        );
        expect(launched, isFalse);
      },
    );

    test('blocks when the required editor is not installed', () async {
      final project = _createUnityProject(root, 'MissingEditor', '6000.0.23f1');
      var launched = false;
      final openRepository = LocalDeveloperRepository(
        dataRoot: dataRoot.path,
        repositoryRoot: repoRoot.path,
        unityEditorScanner: () async => [
          UnityEditor(
            version: '6000.0.31f1',
            path: p.join(root.path, 'Unity31.exe'),
          ),
        ],
        unityEditorLauncher: (_, _) async => launched = true,
      );

      await expectLater(
        openRepository.openProjectInUnity(project.path),
        throwsA(
          predicate(
            (error) => error.toString().contains(
              'Unity 6000.0.23f1 (1c4764c07fb4) is required',
            ),
          ),
        ),
      );
      expect(launched, isFalse);
    });

    test('blocks package-only roots without ProjectVersion.txt', () async {
      final packageRoot = Directory(p.join(root.path, 'UnityPackage'))
        ..createSync();
      File(p.join(packageRoot.path, 'package.json')).writeAsStringSync(
        jsonEncode({'name': 'com.test.package', 'unity': '2022.3'}),
      );
      var launched = false;
      final openRepository = LocalDeveloperRepository(
        dataRoot: dataRoot.path,
        repositoryRoot: repoRoot.path,
        unityEditorScanner: () async => [
          UnityEditor(
            version: '6000.0.23f1',
            path: p.join(root.path, 'Unity23.exe'),
          ),
        ],
        unityEditorLauncher: (_, _) async => launched = true,
      );

      await expectLater(
        openRepository.openProjectInUnity(packageRoot.path),
        throwsA(
          predicate(
            (error) =>
                error.toString().contains('ProjectSettings/ProjectVersion.txt'),
          ),
        ),
      );
      expect(launched, isFalse);
    });
  });
}

Directory _createUnityProject(Directory parent, String name, String version) {
  final project = Directory(p.join(parent.path, name))..createSync();
  Directory(p.join(project.path, 'Assets')).createSync();
  Directory(p.join(project.path, 'ProjectSettings')).createSync();
  File(
    p.join(project.path, 'ProjectSettings', 'ProjectVersion.txt'),
  ).writeAsStringSync('m_EditorVersion: $version\n');
  return project;
}
