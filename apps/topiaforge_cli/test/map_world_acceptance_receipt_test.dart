import 'dart:convert';
import 'dart:io';
import 'package:archive/archive.dart';
import 'package:crypto/crypto.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/live_acceptance_trust.dart';

void main() {
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
