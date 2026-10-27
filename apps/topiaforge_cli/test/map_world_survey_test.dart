import 'dart:convert';
import 'dart:io';
import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/map_world_survey.dart';

void main() {
  final projectBytes = File(
    '../../tests/fixtures/maps/authored-project.json',
  ).readAsBytesSync();
  late Directory root;
  late File manifestFile, anchorsFile;
  late Map<String, dynamic> manifest;
  late String worldId;
  setUp(() {
    root = Directory.systemTemp.createTempSync('map-survey-');
    final fixture =
        jsonDecode(
              File(
                '../../tests/fixtures/gamemode-v6/serialization/manifest/v6-map-published-defaults.json',
              ).readAsStringSync(),
            )
            as Map;
    manifest = fixture['manifest'] as Map<String, dynamic>;
    final world = manifest['contributions']['worlds'][0] as Map;
    worldId = world['id'] as String;
    world['content'] = {'kind': 'map', 'file': 'map.json', 'scene': 'workshop'};
    manifestFile = File(p.join(root.path, 'topiaforge.mod.json'))
      ..writeAsStringSync(jsonEncode(manifest));
    File(p.join(root.path, 'map.json')).writeAsBytesSync(projectBytes);
    anchorsFile = File(p.join(root.path, 'map-anchors.json'))
      ..writeAsStringSync(
        jsonEncode({
          'schemaVersion': 1,
          'anchors': [
            {
              'key': 'supply',
              'entityId': 'oldcrate',
              'name': 'Supply crate',
              'modelId': 'kit/box',
              'editorPosition': {'x': 11.5, 'y': 2, 'z': 3},
            },
          ],
        }),
      );
  });
  tearDown(() => root.deleteSync(recursive: true));
  test(
    'packaged survey refreshes records, reports drift and updates only anchors',
    () async {
      final before = manifestFile.readAsStringSync();
      final first = await surveyMapWorld(root.path, worldId);
      expect(first.entityCount, 7);
      expect(first.report, contains('NameModelAndPosition'));
      expect(first.complete, isTrue);
      expect(anchorsFile.readAsStringSync(), contains('oldcrate'));
      final snapshot = Directory(
        p.join(root.path, 'obj'),
      ).listSync().whereType<File>().single;
      final data = jsonDecode(snapshot.readAsStringSync()) as Map;
      expect(data['world'], worldId);
      expect(data['map'], 'file:map.json');
      expect(data['records'], hasLength(7));
      expect(data.toString(), isNot(contains(root.path)));
      expect(
        (await surveyMapWorld(root.path, worldId, update: true)).updated,
        isTrue,
      );
      expect(
        jsonDecode(anchorsFile.readAsStringSync())['anchors'][0]['entityId'],
        'crate',
      );
      expect(manifestFile.readAsStringSync(), before);
    },
  );
  test(
    'ambiguous and required missing anchors refuse update byte for byte',
    () async {
      anchorsFile.writeAsBytesSync(
        File(
          '../../tests/fixtures/maps/authored-anchors.json',
        ).readAsBytesSync(),
      );
      final before = anchorsFile.readAsBytesSync();
      final result = await surveyMapWorld(root.path, worldId);
      expect(result.complete, isFalse);
      expect(result.report, contains('Ambiguous'));
      await expectLater(
        surveyMapWorld(root.path, worldId, update: true),
        throwsStateError,
      );
      expect(anchorsFile.readAsBytesSync(), before);
    },
  );
  test(
    'published refresh and offline snapshot preserve matching map identity',
    () async {
      manifest['contributions']['worlds'][0]['content'] = {
        'kind': 'map',
        'map': 'example-map',
        'scene': 'workshop',
      };
      manifestFile.writeAsStringSync(jsonEncode(manifest));
      var requests = 0;
      await surveyMapWorld(
        root.path,
        worldId,
        readPublished: (ref) async {
          requests++;
          expect(ref.id, 'example-map');
          return MapProjectSurvey.read(projectBytes);
        },
      );
      final offline = await surveyMapWorld(
        root.path,
        worldId,
        offline: true,
        readPublished: (_) async => throw StateError('No request expected'),
      );
      expect(requests, 1);
      expect(offline.report, contains('not refreshed'));
      manifest['contributions']['worlds'][0]['content']['map'] = 'other-map';
      manifestFile.writeAsStringSync(jsonEncode(manifest));
      await expectLater(
        surveyMapWorld(root.path, worldId, offline: true),
        throwsStateError,
      );
    },
  );
  test('unsafe anchors paths and malformed map leave no snapshot', () async {
    await expectLater(
      surveyMapWorld(root.path, worldId, anchorsPath: '../outside.json'),
      throwsStateError,
    );
    File(p.join(root.path, 'map.json')).writeAsStringSync('{bad');
    await expectLater(
      surveyMapWorld(root.path, worldId),
      throwsFormatException,
    );
    expect(Directory(p.join(root.path, 'obj')).existsSync(), isFalse);
  });
  test(
    'expanded anchors obey the runtime byte limit without rewriting',
    () async {
      final document = jsonDecode(utf8.decode(projectBytes)) as Map;
      final entity = document['scenes']['workshop']['entities']['crate'] as Map;
      entity['name'] = 'n' * 128;
      entity['components']['model-renderer']['model'] = 'm' * 256;
      File(
        p.join(root.path, 'map.json'),
      ).writeAsStringSync(jsonEncode(document));
      anchorsFile.writeAsStringSync(
        jsonEncode({
          'schemaVersion': 1,
          'anchors': [
            for (var i = 0; i < 1024; i++)
              {'key': 'key$i', 'entityId': 'crate'},
          ],
        }),
      );
      final before = anchorsFile.readAsBytesSync();
      expect(before.length, lessThan(256 * 1024));
      await expectLater(
        surveyMapWorld(root.path, worldId, update: true),
        throwsA(
          isA<StateError>().having(
            (e) => e.message,
            'message',
            contains('256 KiB'),
          ),
        ),
      );
      expect(anchorsFile.readAsBytesSync(), before);
      expect(Directory(p.join(root.path, 'obj')).existsSync(), isFalse);
    },
  );
  test('CLI dispatch surveys a packaged world without network', () async {
    final result = await Process.run(Platform.resolvedExecutable, [
      p.absolute('bin/topiaforge.dart'),
      'world',
      'survey',
      worldId,
      '--project',
      root.path,
      '--update',
    ]);
    expect(result.exitCode, 0, reason: '${result.stdout}\n${result.stderr}');
    expect(result.stdout, contains('7 authored entities'));
    expect(result.stdout, isNot(contains(root.path)));
  });
}
