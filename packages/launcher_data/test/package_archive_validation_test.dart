import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory root;
  late LocalLauncherRepository repository;
  late GameInstall install;

  setUp(() async {
    root = Directory.systemTemp.createTempSync('package-validation-');
    final game = Directory(p.join(root.path, 'TopiaForge'))..createSync();
    File(p.join(game.path, 'Robotopia.exe')).writeAsStringSync('');
    Directory(
      p.join(game.path, 'Robotopia_Data', 'Managed'),
    ).createSync(recursive: true);
    File(
      p.join(game.path, 'Robotopia_Data', 'Managed', 'UnityEngine.dll'),
    ).writeAsStringSync('');
    repository = LocalLauncherRepository(
      dataRoot: p.join(root.path, 'data'),
      repositoryRoot: root.path,
    );
    install = await repository.selectGameDirectory(game.path);
  });

  tearDown(() => root.deleteSync(recursive: true));

  test('rejects duplicate normalized archive paths', () async {
    final archive = _validArchive()
      ..addFile(ArchiveFile.string('TOPIAFORGE.MOD.JSON', '{}'));
    final package = _writeArchive(root, 'duplicate.topiaforgemod', archive);

    await expectLater(
      repository.previewPackage(package.path, install),
      throwsA(
        predicate((error) => error.toString().contains('duplicate path')),
      ),
    );
  });

  test('rejects symbolic links', () async {
    final link = ArchiveFile.string('linked.dll', 'TopiaForge.dll')
      ..symbolicLink = 'TopiaForge.dll'
      ..mode = 0xa1ff;
    final archive = _validArchive()..addFile(link);
    final package = _writeArchive(root, 'symlink.topiaforgemod', archive);

    await expectLater(
      repository.previewPackage(package.path, install),
      throwsA(predicate((error) => error.toString().contains('symbolic link'))),
    );
  });

  test('rejects a bare-string manifest author before install', () async {
    final archive = _archiveWithAuthor('TopiaForge');
    final package = _writeArchive(root, 'string-author.topiaforgemod', archive);

    await expectLater(
      repository.installPackage(package.path, install),
      throwsA(
        predicate(
          (error) => error.toString().contains('author must be an object'),
        ),
      ),
    );
  });

  test('rejects package files with a retired extension', () async {
    final package = _writeArchive(
      root,
      'retired.robo'
      'topiamod',
      _validArchive(),
    );

    await expectLater(
      repository.previewPackage(package.path, install),
      throwsFormatException,
    );
  });

  for (final unsafePath in [
    'C:drive-relative.dll',
    'payload.dll:stream',
    'NUL.txt',
    'folder/trailing. /value.dll',
  ]) {
    test('rejects non-portable archive path $unsafePath', () async {
      final archive = _validArchive()
        ..addFile(ArchiveFile.string(unsafePath, 'bad'));
      final package = _writeArchive(root, 'unsafe-path.topiaforgemod', archive);

      await expectLater(
        repository.previewPackage(package.path, install),
        throwsA(
          predicate((error) => error.toString().contains('non-portable path')),
        ),
      );
    });
  }

  test(
    'rejects an oversized declared expanded entry before extraction',
    () async {
      final encoded = ZipEncoder().encode(_validArchive());
      final central = _signatureOffset(encoded, const [0x50, 0x4b, 0x01, 0x02]);
      _writeUint32(encoded, central + 24, 0x40000001);
      final package = File(p.join(root.path, 'oversized.topiaforgemod'))
        ..writeAsBytesSync(encoded);

      await expectLater(
        repository.previewPackage(package.path, install),
        throwsA(
          predicate((error) => error.toString().contains('1 GB expanded-file')),
        ),
      );
    },
  );

  test('rejects encrypted archive metadata before decoding entries', () async {
    final encoded = ZipEncoder().encode(_validArchive());
    final central = _signatureOffset(encoded, const [0x50, 0x4b, 0x01, 0x02]);
    encoded[central + 8] |= 1;
    final package = File(p.join(root.path, 'encrypted.topiaforgemod'))
      ..writeAsBytesSync(encoded);

    await expectLater(
      repository.previewPackage(package.path, install),
      throwsA(predicate((error) => error.toString().contains('Encrypted'))),
    );
  });

  test('rejects a manifest that expands beyond its declared size', () async {
    final encoded = ZipEncoder().encode(_validArchive());
    final local = _signatureOffset(encoded, const [0x50, 0x4b, 0x03, 0x04]);
    final central = _signatureOffset(encoded, const [0x50, 0x4b, 0x01, 0x02]);
    _writeUint32(encoded, local + 22, 1);
    _writeUint32(encoded, central + 24, 1);
    final package = File(p.join(root.path, 'size-mismatch.topiaforgemod'))
      ..writeAsBytesSync(encoded);

    await expectLater(
      repository.previewPackage(package.path, install),
      throwsA(
        predicate(
          (error) =>
              error.toString().contains('expanded limit') ||
              error.toString().contains('declared'),
        ),
      ),
    );
  });

  test(
    'uninstall rejects traversal ids before touching the filesystem',
    () async {
      final outside = Directory(
        p.join(install.path, 'BepInEx', 'TopiaForge', 'outside'),
      )..createSync(recursive: true);
      final sentinel = File(p.join(outside.path, 'keep.txt'))
        ..writeAsStringSync('keep');

      await expectLater(
        repository.uninstallMod(install, '../outside'),
        throwsArgumentError,
      );

      expect(sentinel.existsSync(), isTrue);
    },
  );

  test(
    'rejects an unsafe expected hash before resolving a cache path',
    () async {
      final sentinel = File(p.join(root.path, 'escape.topiaforgemod'))
        ..writeAsStringSync('keep');

      await expectLater(
        repository.previewPackage(
          'https://packages.example/mod.topiaforgemod',
          install,
          expectedSha256: '../../escape',
        ),
        throwsA(
          predicate((error) => error.toString().contains('64 hex digits')),
        ),
      );

      expect(sentinel.readAsStringSync(), 'keep');
    },
  );
}

Archive _validArchive() => Archive()
  ..addFile(
    ArchiveFile.string(
      'topiaforge.mod.json',
      jsonEncode({
        'schemaVersion': 3,
        'name': 'validation.mod',
        'displayName': 'Validation Mod',
        'version': '1.0.0',
        'author': {'name': 'TopiaForge'},
        'entryAssembly': 'Validation.dll',
        'entryType': 'Validation.Entry',
      }),
    ),
  )
  ..addFile(ArchiveFile.string('Validation.dll', 'dll'));

Archive _archiveWithAuthor(Object author) => Archive()
  ..addFile(
    ArchiveFile.string(
      'topiaforge.mod.json',
      jsonEncode({
        'schemaVersion': 3,
        'name': 'validation.mod',
        'displayName': 'Validation Mod',
        'version': '1.0.0',
        'author': author,
        'entryAssembly': 'Validation.dll',
        'entryType': 'Validation.Entry',
      }),
    ),
  )
  ..addFile(ArchiveFile.string('Validation.dll', 'dll'));

File _writeArchive(Directory root, String name, Archive archive) =>
    File(p.join(root.path, name))
      ..writeAsBytesSync(ZipEncoder().encode(archive));

int _signatureOffset(List<int> bytes, List<int> signature) {
  for (var offset = 0; offset <= bytes.length - signature.length; offset++) {
    var matches = true;
    for (var index = 0; index < signature.length; index++) {
      if (bytes[offset + index] != signature[index]) {
        matches = false;
        break;
      }
    }
    if (matches) return offset;
  }
  throw StateError('ZIP signature was not found.');
}

void _writeUint32(List<int> bytes, int offset, int value) {
  for (var index = 0; index < 4; index++) {
    bytes[offset + index] = (value >> (index * 8)) & 0xff;
  }
}
