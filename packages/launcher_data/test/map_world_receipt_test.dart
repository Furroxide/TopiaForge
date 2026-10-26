import 'dart:convert';
import 'dart:io';
import 'package:archive/archive.dart';
import 'package:crypto/crypto.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  test('launcher receipts classify a map payload as critical', () async {
    final root = Directory.systemTemp.createTempSync('map-receipt-');
    final game = Directory(p.join(root.path, 'game'))..createSync();
    File(p.join(game.path, 'Robotopia.exe')).writeAsStringSync('');
    File(p.join(game.path, 'Robotopia_Data', 'Managed', 'UnityEngine.dll'))
      ..createSync(recursive: true)
      ..writeAsStringSync('');
    // Isolate receipt writing from managed-metadata/toolchain admission, which
    // has separate native package and CLI floor tests. No game is started.
    final repository = LocalLauncherRepository(
      dataRoot: p.join(root.path, 'data'),
      repositoryRoot: root.path,
      packageMetadataValidator: (_) async => const [],
    );
    try {
      final install = await repository.selectGameDirectory(game.path);
      const provider = 'io.github.furroxide.topiaforge.worlds';
      for (final id in [provider, 'example.map']) {
        final isMap = id == 'example.map';
        final raw = {
          'schemaVersion': 6,
          'name': id,
          'displayName': 'Fixture',
          'version': '1.0.0',
          'author': {'name': 'TopiaForge'},
          'entryAssembly': 'Example.dll',
          'entryType': 'Example.Mod',
          'supportedGameVersionRange': '*',
          'supportedSdkVersionRange': '*',
          'supportedLoaderVersionRange': '*',
          if (isMap) 'capabilities': ['world-service'],
          if (isMap) 'dependencies': {provider: '*'},
          if (isMap)
            'contributions': {
              'worlds': [
                {
                  'id': '$id.world',
                  'name': 'Map',
                  'content': {'kind': 'map', 'file': 'Content/world.json'},
                },
              ],
            },
          if (isMap)
            'hashes': {
              'Content/world.json': sha256
                  .convert(utf8.encode('{}'))
                  .toString(),
            },
        };
        final archive = Archive()
          ..addFile(ArchiveFile.string('topiaforge.mod.json', jsonEncode(raw)))
          ..addFile(ArchiveFile.string('Example.dll', 'fixture'));
        if (isMap) {
          archive.addFile(ArchiveFile.string('Content/world.json', '{}'));
        }
        final package = File(p.join(root.path, '$id.topiaforgemod'))
          ..writeAsBytesSync(ZipEncoder().encode(archive));
        await repository.installPackage(package.path, install);
      }
      final receipt =
          jsonDecode(
                File(
                  p.join(
                    game.path,
                    'BepInEx',
                    'TopiaForge',
                    'packages',
                    'example.map',
                    '1.0.0',
                    'topiaforge.install.json',
                  ),
                ).readAsStringSync(),
              )
              as Map;
      final map = (receipt['files'] as List).cast<Map>().singleWhere(
        (file) => file['path'] == 'Content/world.json',
      );
      expect(map['critical'], isTrue);
      expect(map['sha256'], sha256.convert(utf8.encode('{}')).toString());
    } finally {
      await repository.dispose();
      root.deleteSync(recursive: true);
    }
  });
}
