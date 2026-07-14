import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:crypto/crypto.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  test(
    'verifies every dependency before changing package directories',
    () async {
      final root = Directory.systemTemp.createTempSync('package-atomicity-');
      addTearDown(() => root.deleteSync(recursive: true));
      final game = Directory(p.join(root.path, 'TopiaForge'))..createSync();
      _createGame(game);
      final repository = LocalLauncherRepository(
        dataRoot: p.join(root.path, 'data'),
        repositoryRoot: root.path,
      );
      final install = await repository.selectGameDirectory(game.path);
      final oldFirst = _package(root, 'first.dep', '1.0.0');
      await repository.installPackage(oldFirst.path, install);

      final newFirst = _package(root, 'first.dep', '2.0.0');
      final badLater = _package(root, 'later.dep', '1.0.0');
      final source = File(p.join(root.path, 'source.json'))
        ..writeAsStringSync(
          jsonEncode({
            'packages': {
              'first.dep': {
                'versions': {
                  '2.0.0': {
                    ..._manifest('first.dep', '2.0.0'),
                    'url': newFirst.uri.toString(),
                    'zipSHA256': _sha(newFirst),
                  },
                },
              },
              'later.dep': {
                'versions': {
                  '1.0.0': {
                    ..._manifest('later.dep', '1.0.0'),
                    'url': badLater.uri.toString(),
                    'zipSHA256': List.filled(64, '0').join(),
                  },
                },
              },
            },
          }),
        );
      await repository.savePackageSources([
        PackageSource(
          id: 'atomicity.test',
          name: 'Atomicity Test',
          url: source.uri.toString(),
        ),
      ]);
      final rootPackage = _package(
        root,
        'main.mod',
        '1.0.0',
        dependencies: {'first.dep': '>=2.0.0', 'later.dep': '>=1.0.0'},
      );

      await expectLater(
        repository.installPackage(rootPackage.path, install),
        throwsA(predicate((error) => error.toString().contains('SHA-256'))),
      );

      expect(
        Directory(
          p.join(
            game.path,
            'BepInEx',
            'TopiaForge',
            'packages',
            'first.dep',
            '2.0.0',
          ),
        ).existsSync(),
        isFalse,
      );
      final installed = await repository.loadSnapshot();
      expect(installed.installedMods.single.id, 'first.dep');
      expect(installed.installedMods.single.version, '1.0.0');
    },
  );

  test('rolls back committed package directories when commit fails', () async {
    final root = Directory.systemTemp.createTempSync('package-rollback-');
    addTearDown(() => root.deleteSync(recursive: true));
    final game = Directory(p.join(root.path, 'TopiaForge'))..createSync();
    _createGame(game);
    final dataRoot = p.join(root.path, 'data');
    final repository = LocalLauncherRepository(
      dataRoot: dataRoot,
      repositoryRoot: root.path,
    );
    final install = await repository.selectGameDirectory(game.path);
    await repository.installPackage(
      _package(root, 'first.dep', '1.0.0').path,
      install,
    );
    final firstV2 = _package(root, 'first.dep', '2.0.0');
    final later = _package(root, 'later.dep', '1.0.0');
    final source = File(p.join(root.path, 'rollback-source.json'))
      ..writeAsStringSync(
        jsonEncode({
          'packages': {
            'first.dep': {
              'versions': {
                '2.0.0': {
                  ..._manifest('first.dep', '2.0.0'),
                  'url': firstV2.uri.toString(),
                  'zipSHA256': _sha(firstV2),
                },
              },
            },
            'later.dep': {
              'versions': {
                '1.0.0': {
                  ..._manifest('later.dep', '1.0.0'),
                  'url': later.uri.toString(),
                  'zipSHA256': _sha(later),
                },
              },
            },
          },
        }),
      );
    await repository.savePackageSources([
      PackageSource(
        id: 'rollback.test',
        name: 'Rollback Test',
        url: source.uri.toString(),
      ),
    ]);
    final failingRepository = LocalLauncherRepository(
      dataRoot: dataRoot,
      repositoryRoot: root.path,
      packageInstallCommitHook: (count) {
        if (count == 1) {
          throw StateError('injected commit failure');
        }
      },
    );
    final rootPackage = _package(
      root,
      'main.mod',
      '1.0.0',
      dependencies: {'first.dep': '>=2.0.0', 'later.dep': '>=1.0.0'},
    );

    await expectLater(
      failingRepository.installPackage(rootPackage.path, install),
      throwsA(
        predicate((error) => error.toString().contains('injected commit')),
      ),
    );

    final packages = p.join(game.path, 'BepInEx', 'TopiaForge', 'packages');
    expect(
      Directory(p.join(packages, 'first.dep', '2.0.0')).existsSync(),
      isFalse,
    );
    expect(Directory(p.join(packages, 'later.dep')).existsSync(), isFalse);
    final snapshot = await failingRepository.loadSnapshot();
    expect(snapshot.installedMods.single.id, 'first.dep');
    expect(snapshot.installedMods.single.version, '1.0.0');
    Directory(p.join(packages, 'stranded.empty')).createSync(recursive: true);
    final snapshotWithEmptyDirectory = await failingRepository.loadSnapshot();
    expect(snapshotWithEmptyDirectory.installedMods.single.id, 'first.dep');
    final staging = Directory(
      p.join(game.path, 'BepInEx', 'TopiaForge', 'staging'),
    );
    expect(staging.existsSync() ? staging.listSync() : const [], isEmpty);
  });

  test('rejects an invalid downloaded dependency before staging', () async {
    final root = Directory.systemTemp.createTempSync('package-validation-');
    addTearDown(() => root.deleteSync(recursive: true));
    final game = Directory(p.join(root.path, 'TopiaForge'))..createSync();
    _createGame(game);
    final repository = LocalLauncherRepository(
      dataRoot: p.join(root.path, 'data'),
      repositoryRoot: root.path,
    );
    final install = await repository.selectGameDirectory(game.path);
    final invalidDependency = _package(
      root,
      'invalid.dep',
      '1.0.0',
      dependencies: {'../outside': '>=1.0.0'},
    );
    final source = File(p.join(root.path, 'invalid-source.json'))
      ..writeAsStringSync(
        jsonEncode({
          'packages': {
            'invalid.dep': {
              'versions': {
                '1.0.0': {
                  ..._manifest('invalid.dep', '1.0.0'),
                  'url': invalidDependency.uri.toString(),
                  'zipSHA256': _sha(invalidDependency),
                },
              },
            },
          },
        }),
      );
    await repository.savePackageSources([
      PackageSource(
        id: 'invalid.test',
        name: 'Invalid Dependency Test',
        url: source.uri.toString(),
      ),
    ]);
    final rootPackage = _package(
      root,
      'main.mod',
      '1.0.0',
      dependencies: {'invalid.dep': '>=1.0.0'},
    );

    await expectLater(
      repository.installPackage(rootPackage.path, install),
      throwsA(
        predicate((error) => error.toString().contains('invalid manifest')),
      ),
    );

    final packages = p.join(game.path, 'BepInEx', 'TopiaForge', 'packages');
    expect(Directory(p.join(packages, 'invalid.dep')).existsSync(), isFalse);
    expect(Directory(p.join(packages, 'main.mod')).existsSync(), isFalse);
  });

  test('retains older registry versions for dependency resolution', () async {
    final root = Directory.systemTemp.createTempSync('package-versions-');
    addTearDown(() => root.deleteSync(recursive: true));
    final game = Directory(p.join(root.path, 'TopiaForge'))..createSync();
    _createGame(game);
    final repository = LocalLauncherRepository(
      dataRoot: p.join(root.path, 'data'),
      repositoryRoot: root.path,
    );
    final install = await repository.selectGameDirectory(game.path);
    final dependencyV1 = _package(root, 'versioned.dep', '1.0.0');
    final dependencyV2 = _package(root, 'versioned.dep', '2.0.0');
    final source = File(p.join(root.path, 'version-source.json'))
      ..writeAsStringSync(
        jsonEncode({
          'packages': {
            'versioned.dep': {
              'versions': {
                '2.0.0': {
                  ..._manifest('versioned.dep', '2.0.0'),
                  'url': dependencyV2.uri.toString(),
                  'zipSHA256': _sha(dependencyV2),
                },
                '1.0.0': {
                  ..._manifest('versioned.dep', '1.0.0'),
                  'url': dependencyV1.uri.toString(),
                  'zipSHA256': _sha(dependencyV1),
                },
              },
            },
          },
        }),
      );
    await repository.savePackageSources([
      PackageSource(
        id: 'versions.test',
        name: 'Version Selection Test',
        url: source.uri.toString(),
      ),
    ]);
    final rootPackage = _package(
      root,
      'main.mod',
      '1.0.0',
      dependencies: {'versioned.dep': '<2.0.0'},
    );

    final installed = await repository.installPackage(
      rootPackage.path,
      install,
    );

    expect(
      installed.singleWhere((mod) => mod.id == 'versioned.dep').version,
      '1.0.0',
    );
    final staging = Directory(
      p.join(game.path, 'BepInEx', 'TopiaForge', 'staging'),
    );
    expect(staging.existsSync() ? staging.listSync() : const [], isEmpty);
  });

  test('retains older directory-source versions for resolution', () async {
    final root = Directory.systemTemp.createTempSync(
      'package-directory-versions-',
    );
    addTearDown(() => root.deleteSync(recursive: true));
    final game = Directory(p.join(root.path, 'TopiaForge'))..createSync();
    _createGame(game);
    final repository = LocalLauncherRepository(
      dataRoot: p.join(root.path, 'data'),
      repositoryRoot: root.path,
    );
    final install = await repository.selectGameDirectory(game.path);
    final source = Directory(p.join(root.path, 'packages'))..createSync();
    _package(source, 'versioned.dep', '1.0.0');
    _package(source, 'versioned.dep', '2.0.0');
    await repository.savePackageSources([
      PackageSource(
        id: 'directory.versions.test',
        name: 'Directory Version Selection Test',
        url: source.uri.toString(),
      ),
    ]);
    final rootPackage = _package(
      root,
      'main.mod',
      '1.0.0',
      dependencies: {'versioned.dep': '<2.0.0'},
    );

    final installed = await repository.installPackage(
      rootPackage.path,
      install,
    );

    expect(
      installed.singleWhere((mod) => mod.id == 'versioned.dep').version,
      '1.0.0',
    );
  });

  test('refuses to replace a package target symbolic link', () async {
    if (Platform.isWindows) {
      return;
    }
    final root = Directory.systemTemp.createTempSync('package-target-link-');
    addTearDown(() => root.deleteSync(recursive: true));
    final game = Directory(p.join(root.path, 'TopiaForge'))..createSync();
    _createGame(game);
    final repository = LocalLauncherRepository(
      dataRoot: p.join(root.path, 'data'),
      repositoryRoot: root.path,
    );
    final install = await repository.selectGameDirectory(game.path);
    final outside = Directory(p.join(root.path, 'outside'))..createSync();
    final sentinel = File(p.join(outside.path, 'keep.txt'))
      ..writeAsStringSync('keep');
    final target = p.join(
      game.path,
      'BepInEx',
      'TopiaForge',
      'packages',
      'linked.mod',
      '1.0.0',
    );
    await Directory(p.dirname(target)).create(recursive: true);
    await Link(target).create(outside.path);

    await expectLater(
      repository.installPackage(
        _package(root, 'linked.mod', '1.0.0').path,
        install,
      ),
      throwsA(predicate((error) => error.toString().contains('symbolic link'))),
    );

    expect(sentinel.readAsStringSync(), 'keep');
    expect(Link(target).existsSync(), isTrue);
  });
}

void _createGame(Directory game) {
  File(p.join(game.path, 'Robotopia.exe')).writeAsStringSync('');
  Directory(
    p.join(game.path, 'Robotopia_Data', 'Managed'),
  ).createSync(recursive: true);
  File(
    p.join(game.path, 'Robotopia_Data', 'Managed', 'UnityEngine.dll'),
  ).writeAsStringSync('');
}

File _package(
  Directory root,
  String id,
  String version, {
  Map<String, String> dependencies = const {},
}) {
  final file = File(p.join(root.path, '$id-$version.topiaforgemod'));
  final archive = Archive()
    ..addFile(
      ArchiveFile.string(
        'topiaforge.mod.json',
        jsonEncode(_manifest(id, version, dependencies: dependencies)),
      ),
    )
    ..addFile(ArchiveFile.string('${_assembly(id)}.dll', version));
  file.writeAsBytesSync(ZipEncoder().encode(archive));
  return file;
}

Map<String, Object?> _manifest(
  String id,
  String version, {
  Map<String, String> dependencies = const {},
}) => {
  'schemaVersion': 3,
  'name': id,
  'displayName': id,
  'version': version,
  'author': {'name': 'TopiaForge'},
  'entryAssembly': '${_assembly(id)}.dll',
  'entryType': '$id.Entry',
  if (dependencies.isNotEmpty) 'vpmDependencies': dependencies,
};

String _assembly(String id) => id
    .split('.')
    .map((part) => part[0].toUpperCase() + part.substring(1))
    .join();

String _sha(File file) => sha256.convert(file.readAsBytesSync()).toString();
