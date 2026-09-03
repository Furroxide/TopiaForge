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
    root = Directory.systemTemp.createTempSync('topiaforge-archive-security-');
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
    'developer restore rejects traversal and preserves prior cache',
    () async {
      final package = _writeModPackage(
        File(p.join(root.path, 'unsafe.topiaforgemod')),
        extraEntries: [ArchiveFile.string('../escape.txt', 'escape')],
      );
      final project = await _createDeveloperProject(repository, root, package);
      final packageRoot = Directory(
        p.join(project, '.topiaforge', 'packages', 'safe.mod', '1.0.0'),
      )..createSync(recursive: true);
      final marker = File(p.join(packageRoot.path, 'extracted', 'keep.txt'))
        ..createSync(recursive: true)
        ..writeAsStringSync('keep');

      await expectLater(
        repository.resolveDeveloperProject(project),
        throwsA(isA<StateError>()),
      );

      expect(marker.readAsStringSync(), 'keep');
      expect(
        File(p.join(project, '.topiaforge', 'escape.txt')).existsSync(),
        isFalse,
      );
      expect(
        Directory(p.join(project, '.topiaforge', 'staging')).existsSync(),
        isFalse,
      );
    },
  );

  test('percent-encoded dot segments remain literal archive names', () async {
    final package = _writeModPackage(
      File(p.join(root.path, 'encoded.topiaforgemod')),
      extraEntries: [ArchiveFile.string('%2e%2e/escape.txt', 'literal')],
    );
    final project = await _createDeveloperProject(repository, root, package);

    await repository.resolveDeveloperProject(project);

    final packageRoot = p.join(
      project,
      '.topiaforge',
      'packages',
      'safe.mod',
      '1.0.0',
    );
    expect(
      File(
        p.join(packageRoot, 'extracted', '%2e%2e', 'escape.txt'),
      ).readAsStringSync(),
      'literal',
    );
    expect(File(p.join(packageRoot, 'escape.txt')).existsSync(), isFalse);
  });

  test(
    'developer package preflight rejects symlinks and false sizes',
    () async {
      final symlinkPackage = _writeModPackage(
        File(p.join(root.path, 'symlink.topiaforgemod')),
        extraEntries: [_symlinkEntry('linked.dll', '../outside.dll')],
      );
      await expectLater(
        repository.checkPackage(symlinkPackage.path),
        throwsA(isA<StateError>()),
      );

      final oversizedPackage = _writeModPackage(
        File(p.join(root.path, 'oversized.topiaforgemod')),
      );
      final bytes = oversizedPackage.readAsBytesSync();
      _setFirstCentralUncompressedSize(bytes, 1024 * 1024 * 1024 + 1);
      oversizedPackage.writeAsBytesSync(bytes);
      await expectLater(
        repository.checkPackage(oversizedPackage.path),
        throwsA(
          isA<StateError>().having(
            (error) => error.message,
            'message',
            contains('1 GB'),
          ),
        ),
      );
    },
  );

  test(
    'local archives and package catalogs are bounded before reading',
    () async {
      final hugePackage = File(p.join(root.path, 'huge.topiaforgemod'));
      hugePackage.openSync(mode: FileMode.write)
        ..truncateSync(512 * 1024 * 1024 + 1)
        ..closeSync();
      await expectLater(
        repository.checkPackage(hugePackage.path),
        throwsA(
          isA<StateError>().having(
            (error) => error.message,
            'message',
            contains('512 MB'),
          ),
        ),
      );

      final source = File(p.join(root.path, 'huge-source.json'));
      source.openSync(mode: FileMode.write)
        ..truncateSync(16 * 1024 * 1024 + 1)
        ..closeSync();
      final workspace = await repository.createModProject(
        parentDirectory: root.path,
        id: 'source.cap',
        name: 'Source Cap',
      );
      await repository.addProjectPackageSource(
        workspace.projectRoot,
        PackageSource(id: 'huge.source', name: 'Huge', url: source.path),
      );
      final resolved = await repository.resolveDeveloperProject(
        workspace.projectRoot,
        restore: false,
      );
      expect(
        resolved.issues.map((issue) => issue.message).join(' '),
        contains('16 MB limit'),
      );
    },
  );

  test(
    'VPM validates every archive before replacing installed packages',
    () async {
      final indexDir = Directory(p.join(repoRoot.path, 'dist', 'vpm'))
        ..createSync(recursive: true);
      final good = _writeVpmPackage(
        File(p.join(indexDir.path, 'com.test.good.zip')),
        'com.test.good',
        extraEntries: [ArchiveFile.string('new.txt', 'new')],
      );
      final bad = _writeVpmPackage(
        File(p.join(indexDir.path, 'com.test.bad.zip')),
        'com.test.bad',
        extraEntries: [ArchiveFile.string('../escape.txt', 'escape')],
      );
      _writeVpmIndex(indexDir, {
        'com.test.good': _vpmVersion('com.test.good', good),
        'com.test.bad': _vpmVersion('com.test.bad', bad),
      });
      final project = _createUnityProject(root, {
        'com.test.good': '*',
        'com.test.bad': '*',
      });
      final installed = Directory(
        p.join(project.path, 'Packages', 'com.test.good'),
      )..createSync();
      File(p.join(installed.path, 'package.json')).writeAsStringSync(
        jsonEncode({'name': 'com.test.good', 'version': '0.9.0'}),
      );
      final marker = File(p.join(installed.path, 'keep.txt'))
        ..writeAsStringSync('keep');
      final manifestBefore = File(
        p.join(project.path, 'Packages', 'vpm-manifest.json'),
      ).readAsStringSync();

      await expectLater(
        repository.resolveUnityProject(project.path),
        throwsA(isA<StateError>()),
      );

      expect(marker.readAsStringSync(), 'keep');
      expect(
        File(
          p.join(project.path, 'Packages', 'vpm-manifest.json'),
        ).readAsStringSync(),
        manifestBefore,
      );
      expect(
        File(p.join(project.path, 'Packages', 'escape.txt')).existsSync(),
        isFalse,
      );
      expect(
        Directory(project.path)
            .listSync(recursive: true)
            .whereType<Directory>()
            .where(
              (directory) =>
                  p.basename(directory.path).startsWith('.topiaforge-vpm-'),
            ),
        isEmpty,
      );
    },
  );

  test('failed VPM add rejects symlinks and restores manifest', () async {
    final indexDir = Directory(p.join(repoRoot.path, 'dist', 'vpm'))
      ..createSync(recursive: true);
    final bad = _writeVpmPackage(
      File(p.join(indexDir.path, 'com.test.bad.zip')),
      'com.test.bad',
      extraEntries: [_symlinkEntry('linked.txt', '../outside.txt')],
    );
    _writeVpmIndex(indexDir, {
      'com.test.bad': _vpmVersion('com.test.bad', bad),
    });
    final project = _createUnityProject(root, const {});
    final manifestFile = File(
      p.join(project.path, 'Packages', 'vpm-manifest.json'),
    );
    final before = manifestFile.readAsStringSync();

    await expectLater(
      repository.addUnityPackage(project.path, 'com.test.bad', '*'),
      throwsA(isA<StateError>()),
    );

    expect(manifestFile.readAsStringSync(), before);
    expect(
      Directory(p.join(project.path, 'Packages', 'com.test.bad')).existsSync(),
      isFalse,
    );
  });

  test('VPM repositories reject plaintext remote URLs', () async {
    await expectLater(
      repository.addUnityRepo('http://packages.example/index.json'),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          contains('Use HTTPS'),
        ),
      ),
    );
    await expectLater(
      repository.checkPackage('https://packages.example/test.topiaforgemod'),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          contains('require a SHA-256'),
        ),
      ),
    );
  });
}

Future<String> _createDeveloperProject(
  LocalDeveloperRepository repository,
  Directory root,
  File package,
) async {
  final source = File(p.join(root.path, 'source-${package.hashCode}.json'))
    ..writeAsStringSync(
      jsonEncode({
        'packages': {
          'safe.mod': {
            'versions': {
              '1.0.0': {
                'manifest': _modManifest,
                'url': package.uri.toString(),
                'sha256': sha256.convert(package.readAsBytesSync()).toString(),
              },
            },
          },
        },
      }),
    );
  final workspace = await repository.createModProject(
    parentDirectory: root.path,
    id: 'author.mod.${package.hashCode.abs()}',
    name: 'Author',
  );
  await repository.addProjectPackageSource(
    workspace.projectRoot,
    PackageSource(id: 'test.source', name: 'Test', url: source.path),
  );
  await repository.addProjectDependency(
    workspace.projectRoot,
    const ModDependency(id: 'safe.mod'),
  );
  return workspace.projectRoot;
}

File _writeModPackage(File file, {List<ArchiveFile> extraEntries = const []}) {
  final archive = Archive()
    ..addFile(
      ArchiveFile.string('topiaforge.mod.json', jsonEncode(_modManifest)),
    )
    ..addFile(ArchiveFile.string('SafeMod.dll', 'dll'));
  for (final entry in extraEntries) {
    archive.addFile(entry);
  }
  file.writeAsBytesSync(ZipEncoder().encode(archive));
  return file;
}

const _modManifest = <String, Object?>{
  'schemaVersion': ModManifest.currentSchemaVersion,
  'name': 'safe.mod',
  'displayName': 'Safe Mod',
  'version': '1.0.0',
  'author': {'name': 'TopiaForge'},
  'entryAssembly': 'SafeMod.dll',
  'entryType': 'SafeMod.Entry',
  'supportedGameVersionRange': '*',
  'supportedLoaderVersionRange': '*',
  'supportedSdkVersionRange': '*',
};

File _writeVpmPackage(
  File file,
  String id, {
  List<ArchiveFile> extraEntries = const [],
}) {
  final archive = Archive()
    ..addFile(
      ArchiveFile.string(
        'package.json',
        jsonEncode({'name': id, 'version': '1.0.0'}),
      ),
    );
  for (final entry in extraEntries) {
    archive.addFile(entry);
  }
  file.writeAsBytesSync(ZipEncoder().encode(archive));
  return file;
}

Map<String, Object?> _vpmVersion(String id, File file) => {
  'versions': {
    '1.0.0': {
      'name': id,
      'version': '1.0.0',
      'url': p.basename(file.path),
      'zipSHA256': sha256.convert(file.readAsBytesSync()).toString(),
    },
  },
};

void _writeVpmIndex(Directory indexDir, Map<String, Object?> packages) {
  File(p.join(indexDir.path, 'index.json')).writeAsStringSync(
    jsonEncode({'name': 'Local', 'id': 'local', 'packages': packages}),
  );
}

Directory _createUnityProject(
  Directory root,
  Map<String, String> dependencies,
) {
  final project = Directory(p.join(root.path, 'UnityProject'));
  final packages = Directory(p.join(project.path, 'Packages'))
    ..createSync(recursive: true);
  File(
    p.join(packages.path, 'vpm-manifest.json'),
  ).writeAsStringSync(jsonEncode({'dependencies': dependencies, 'locked': {}}));
  return project;
}

void _setFirstCentralUncompressedSize(List<int> bytes, int value) {
  const signature = [0x50, 0x4b, 0x01, 0x02];
  for (var index = 0; index <= bytes.length - signature.length; index++) {
    if (bytes[index] == signature[0] &&
        bytes[index + 1] == signature[1] &&
        bytes[index + 2] == signature[2] &&
        bytes[index + 3] == signature[3]) {
      for (var byte = 0; byte < 4; byte++) {
        bytes[index + 24 + byte] = (value >> (8 * byte)) & 0xff;
      }
      return;
    }
  }
  throw StateError('Central directory not found.');
}

ArchiveFile _symlinkEntry(String name, String target) {
  return ArchiveFile.string(name, target)
    ..symbolicLink = target
    ..mode = 0xa1ff;
}
