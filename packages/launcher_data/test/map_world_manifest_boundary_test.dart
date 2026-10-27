import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory root;
  late Directory project;
  late LocalDeveloperRepository repository;

  setUp(() {
    root = Directory.systemTemp.createTempSync('map-boundary-');
    project = Directory(p.join(root.path, 'project'))..createSync();
    repository = LocalDeveloperRepository(
      dataRoot: p.join(root.path, 'data'),
      repositoryRoot: root.path,
    );
    File(p.join(project.path, 'Example.dll')).writeAsStringSync('fixture');
  });
  tearDown(() => root.deleteSync(recursive: true));

  for (final content in [
    '"kind":"map","map":"First","map":"Second"',
    r'"kind":"map","map":"First","m\u0061p":"Second"',
    '"kind":"provider","kind":"map","map":"Second"',
    '"kind":"map","map":"Second","scene":"First","scene":"Second"',
    '"kind":"map","file":"first.json","file":"second.json"',
  ]) {
    for (final operation in ['pack', 'directory', 'archive']) {
      test('$operation rejects duplicate map fields: $content', () async {
        final text = _manifest(content);
        final manifest = File(p.join(project.path, 'topiaforge.mod.json'))
          ..writeAsStringSync(text);
        for (final path in ['first.json', 'second.json']) {
          File(p.join(project.path, path)).writeAsStringSync('{}');
        }
        final archive = Archive()
          ..addFile(ArchiveFile.string('topiaforge.mod.json', text))
          ..addFile(ArchiveFile.string('Example.dll', 'fixture'))
          ..addFile(ArchiveFile.string('first.json', '{}'))
          ..addFile(ArchiveFile.string('second.json', '{}'));
        final package = File(p.join(root.path, 'example.topiaforgemod'))
          ..writeAsBytesSync(ZipEncoder().encode(archive));
        final future = switch (operation) {
          'pack' => repository.packModDirectory(project.path),
          'directory' => repository.checkPackage(project.path),
          _ => repository.checkPackage(package.path),
        };
        await expectLater(
          future,
          throwsA(
            isA<FormatException>().having(
              (error) => error.message,
              'diagnostic',
              contains('duplicate property'),
            ),
          ),
        );
        expect(manifest.readAsStringSync(), text);
        expect(Directory(p.join(project.path, 'dist')).existsSync(), isFalse);
      });
    }
  }
}

String _manifest(String content) => jsonEncode({
  'schemaVersion': 6,
  'name': 'example.map',
  'displayName': 'Map',
  'version': '1.0.0',
  'author': {'name': 'Sam'},
  'entryAssembly': 'Example.dll',
  'entryType': 'Example.Mod',
  'supportedGameVersionRange': '*',
  'supportedSdkVersionRange': '*',
  'supportedLoaderVersionRange': '>=0.2.0 <1.0.0',
  'capabilities': ['world-service'],
  'dependencies': {'io.github.furroxide.topiaforge.worlds': '*'},
  'contributions': {
    'worlds': [
      {'id': 'example.map.world', 'name': 'Map', 'content': 'CONTENT'},
    ],
  },
}).replaceFirst('"CONTENT"', '{$content}');
