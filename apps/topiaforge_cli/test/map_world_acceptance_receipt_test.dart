import 'dart:convert';
import 'dart:io';
import 'package:archive/archive.dart';
import 'package:crypto/crypto.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/live_acceptance_models.dart';
import 'package:topiaforge/src/live_acceptance_trust.dart';

void main() {
  test('acceptance retains a portable map path longer than 512 characters', () {
    final root = Directory.systemTemp.createTempSync('map-long-path-');
    try {
      final path = '${List.filled(6, 'a' * 100).join('/')}/world.json';
      final archive = Archive()
        ..addFile(
          ArchiveFile.string(
            'topiaforge.mod.json',
            jsonEncode({
              'name': 'example.map',
              'entryAssembly': 'Example.dll',
              'contributions': {
                'worlds': [
                  {
                    'content': {'kind': 'map', 'file': path},
                  },
                ],
              },
            }),
          ),
        )
        ..addFile(ArchiveFile.string('Example.dll', 'fixture'))
        ..addFile(ArchiveFile.string(path, '{}'));
      final package = File(p.join(root.path, 'example.topiaforgemod'))
        ..writeAsBytesSync(ZipEncoder().encode(archive));
      final receipt = readLiveAcceptancePackageReceipt(package.path);
      expect(
        receipt.criticalFiles.singleWhere((file) => file.path == path).sha256,
        sha256.convert(utf8.encode('{}')).toString(),
      );
    } finally {
      root.deleteSync(recursive: true);
    }
  });

  test('acceptance refuses duplicate map source fields before decoding', () {
    final root = Directory.systemTemp.createTempSync('map-duplicate-');
    try {
      final archive = Archive()
        ..addFile(
          ArchiveFile.string(
            'topiaforge.mod.json',
            '{"name":"example.map","entryAssembly":"Example.dll",'
                '"contributions":{"worlds":[{"content":{"kind":"map",'
                '"file":"first.json","file":"second.json"}}]}}',
          ),
        )
        ..addFile(ArchiveFile.string('Example.dll', 'fixture'))
        ..addFile(ArchiveFile.string('first.json', '{}'))
        ..addFile(ArchiveFile.string('second.json', '{}'));
      final package = File(p.join(root.path, 'example.topiaforgemod'))
        ..writeAsBytesSync(ZipEncoder().encode(archive));
      expect(
        () => readLiveAcceptancePackageReceipt(package.path),
        throwsA(
          isA<LiveAcceptanceError>().having(
            (error) => error.toString(),
            'diagnostic',
            contains('duplicate property'),
          ),
        ),
      );
    } finally {
      root.deleteSync(recursive: true);
    }
  });

  test('acceptance binds map payload bytes as critical receipt content', () {
    final root = Directory.systemTemp.createTempSync('map-evidence-');
    try {
      final raw = {
        'name': 'example.map',
        'entryAssembly': 'Example.dll',
        'contributions': {
          'worlds': [
            {
              'content': {'kind': 'map', 'file': 'Content/world.json'},
            },
          ],
        },
      };
      final archive = Archive()
        ..addFile(ArchiveFile.string('topiaforge.mod.json', jsonEncode(raw)))
        ..addFile(ArchiveFile.string('Example.dll', 'fixture'))
        ..addFile(ArchiveFile.string('Content/world.json', '{}'));
      final package = File(p.join(root.path, 'example.topiaforgemod'))
        ..writeAsBytesSync(ZipEncoder().encode(archive));
      final receipt = readLiveAcceptancePackageReceipt(package.path);
      final map = receipt.criticalFiles.singleWhere(
        (file) => file.path == 'Content/world.json',
      );
      expect(map.sha256, sha256.convert(utf8.encode('{}')).toString());
    } finally {
      root.deleteSync(recursive: true);
    }
  });
}
