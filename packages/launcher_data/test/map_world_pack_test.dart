import 'dart:convert';
import 'dart:io';
import 'package:archive/archive.dart';
import 'package:crypto/crypto.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  for (final extension in [
    'roboworld',
    'json',
    'json.gz',
    'missing',
    'compiled',
  ]) {
    test(
      'pack includes exact map bytes and derives its hash: $extension',
      () async {
        final root = Directory.systemTemp.createTempSync('map-pack-');
        try {
          final project = Directory(p.join(root.path, 'project'))..createSync();
          final file = extension == 'compiled'
              ? 'Maps/world.json'
              : 'Content/world.$extension';
          final bytes = utf8.encode('{"scenes":[]}');
          if (extension != 'missing') {
            File(p.join(project.path, file))
              ..createSync(recursive: true)
              ..writeAsBytesSync(bytes);
          }
          final declared = extension == 'missing'
              ? 'Content/absent.json'
              : file;
          final raw = {
            'schemaVersion': 6,
            'name': 'example.map',
            'displayName': 'Map',
            'version': '1.0.0',
            'author': {'name': 'TopiaForge'},
            'entryAssembly': 'Example.dll',
            'entryType': 'Example.Mod',
            'supportedGameVersionRange': '*',
            'supportedSdkVersionRange': '*',
            'supportedLoaderVersionRange': '>=0.2.0 <1.0.0',
            'capabilities': ['world-service'],
            'dependencies': {'io.github.furroxide.topiaforge.worlds': '*'},
            'contributions': {
              'worlds': [
                {
                  'id': 'example.map.world',
                  'name': 'Map',
                  'content': {'kind': 'map', 'file': declared},
                },
              ],
            },
          };
          File(
            p.join(project.path, 'topiaforge.mod.json'),
          ).writeAsStringSync(jsonEncode(raw));
          File(
            p.join(project.path, 'Example.dll'),
          ).writeAsStringSync('packing fixture');
          RepositoryDotnetSdkResolver? resolver;
          if (extension == 'compiled') {
            File(
              p.join(project.path, 'Example.csproj'),
            ).writeAsStringSync('<Project />');
            File(
                p.join(
                  project.path,
                  'bin',
                  'Release',
                  'netstandard2.1',
                  'Example.dll',
                ),
              )
              ..createSync(recursive: true)
              ..writeAsStringSync('compiled fixture');
            final stub =
                File(
                  p.join(
                    root.path,
                    Platform.isWindows ? 'build.cmd' : 'build.sh',
                  ),
                )..writeAsStringSync(
                  Platform.isWindows
                      ? '@echo off\nexit /b 0\n'
                      : '#!/bin/sh\nexit 0\n',
                );
            if (!Platform.isWindows) {
              expect(
                (await Process.run('chmod', ['700', stub.path])).exitCode,
                0,
              );
            }
            resolver = (_) async => DotnetSdkSelection(
              executable: stub.path,
              version: '10.0.301',
              requiredVersion: '10.0.301',
            );
          }
          final repository = LocalDeveloperRepository(
            dataRoot: p.join(root.path, 'data'),
            repositoryRoot: root.path,
            dotnetSdkResolver: resolver,
          );
          final pack = repository.packModDirectory(
            project.path,
            outputDir: p.join(root.path, 'out'),
          );
          if (extension == 'missing') {
            await expectLater(
              pack,
              throwsA(
                isA<StateError>().having(
                  (e) => e.message,
                  'reason',
                  contains('Map file was not included'),
                ),
              ),
            );
          } else {
            final archive = ZipDecoder().decodeBytes(
              File(await pack).readAsBytesSync(),
            );
            final manifest = ModManifest.fromJson(
              jsonDecode(
                    utf8.decode(
                      archive.findFile('topiaforge.mod.json')!.content
                          as List<int>,
                    ),
                  )
                  as Map<String, Object?>,
            );
            expect(manifest.hashes[declared], sha256.convert(bytes).toString());
            expect(manifest.validate().where((i) => i.isBlocking), isEmpty);
            expect(archive.findFile(declared)!.content, bytes);
          }
        } finally {
          root.deleteSync(recursive: true);
        }
      },
    );
  }
}
