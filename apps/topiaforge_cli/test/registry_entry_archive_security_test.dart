import 'dart:convert';

import 'package:archive/archive.dart';
import 'package:topiaforge/src/registry_entry_builder.dart';
import 'package:test/test.dart';

void main() {
  test('registry reader rejects traversal and portable case collisions', () {
    for (final extra in <ArchiveFile>[
      ArchiveFile.string('../outside.txt', 'secret'),
      ArchiveFile.string('TOPIAFORGE.MOD.JSON', '{}'),
    ]) {
      expect(
        () => readModPackage(_package(extraEntries: [extra])),
        throwsA(isA<StateError>()),
        reason: extra.name,
      );
    }
  });

  test('registry reader rejects symbolic links', () {
    final link = ArchiveFile.string('linked.dll', '../outside.dll')
      ..symbolicLink = '../outside.dll'
      ..mode = 0xa1ff;

    expect(
      () => readModPackage(_package(extraEntries: [link])),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          contains('symbolic link'),
        ),
      ),
    );
  });

  test('registry reader rejects oversized and inconsistent ZIP metadata', () {
    final oversized = _package();
    final oversizedCentral = _signatureOffset(oversized, 0x02014b50);
    _writeUint32(oversized, oversizedCentral + 24, 0x40000001);

    final inconsistent = _package();
    final central = _signatureOffset(inconsistent, 0x02014b50);
    _writeUint32(inconsistent, central + 24, 1);

    for (final bytes in [oversized, inconsistent]) {
      expect(() => readModPackage(bytes), throwsA(isA<StateError>()));
    }
  });
}

List<int> _package({List<ArchiveFile> extraEntries = const []}) {
  final manifest = {
    'schemaVersion': 3,
    'name': 'test.secure',
    'displayName': 'Secure test',
    'version': '1.0.0',
    'author': {'name': 'Tester'},
    'license': 'MIT',
    'licenseFiles': ['LICENSE'],
    'entryAssembly': 'Mod.dll',
    'entryType': 'Test.Mod',
  };
  final archive = Archive()
    ..addFile(ArchiveFile.string('topiaforge.mod.json', jsonEncode(manifest)))
    ..addFile(ArchiveFile.string('LICENSE', 'fixture license'))
    ..addFile(ArchiveFile.string('Mod.dll', 'assembly'));
  for (final entry in extraEntries) {
    archive.addFile(entry);
  }
  return ZipEncoder().encode(archive);
}

int _signatureOffset(List<int> bytes, int signature) {
  for (var index = 0; index <= bytes.length - 4; index++) {
    final value =
        bytes[index] |
        (bytes[index + 1] << 8) |
        (bytes[index + 2] << 16) |
        (bytes[index + 3] << 24);
    if (value == signature) return index;
  }
  throw StateError('ZIP signature was not found.');
}

void _writeUint32(List<int> bytes, int offset, int value) {
  for (var index = 0; index < 4; index++) {
    bytes[offset + index] = (value >> (8 * index)) & 0xff;
  }
}
