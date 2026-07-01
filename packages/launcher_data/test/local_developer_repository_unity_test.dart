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
    root = Directory.systemTemp.createTempSync('robotopia-developer-unity-');
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
    'createUnityProject copies the template + companion and registers it',
    () async {
      // Seed a fake Unity world template + companion package under the temp repo root.
      final templateDir = Directory(
        p.join(repoRoot.path, 'templates', 'Robotopia.UnityWorldTemplate'),
      );
      Directory(
        p.join(templateDir.path, 'Packages'),
      ).createSync(recursive: true);
      File(
        p.join(templateDir.path, 'Packages', 'vpm-manifest.json'),
      ).writeAsStringSync(
        '{"dependencies":{"com.robotopia.ugc-companion":"^0.1.0"}}',
      );
      Directory(p.join(templateDir.path, 'ProjectSettings')).createSync();
      File(
        p.join(templateDir.path, 'ProjectSettings', 'ProjectVersion.txt'),
      ).writeAsStringSync('m_EditorVersion: 6000.0.23f1\n');
      File(
        p.join(templateDir.path, 'README.md'),
      ).writeAsStringSync('# Template\n');
      final companionDir = Directory(
        p.join(
          repoRoot.path,
          'templates',
          'Robotopia.ModTemplate',
          'unity-companion',
          'Packages',
          'com.robotopia.ugc-companion',
        ),
      )..createSync(recursive: true);
      File(p.join(companionDir.path, 'package.json')).writeAsStringSync(
        '{"name":"com.robotopia.ugc-companion","version":"0.1.0"}',
      );

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
        File(
          p.join(
            created.path,
            'Packages',
            'com.robotopia.ugc-companion',
            'package.json',
          ),
        ).existsSync(),
        isTrue,
      );
      expect(
        File(p.join(created.path, 'README.md')).readAsStringSync(),
        contains('My World'),
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
          'id': 'robotopia.vpm.local',
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
}
