import 'dart:convert';
import 'dart:io';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  final fixtures = Directory('../../tests/fixtures/maps');
  final project = File(
    '${fixtures.path}/authored-project.json',
  ).readAsBytesSync();
  test(
    'runtime and survey share parent order and all eight anchor outcomes',
    () {
      final scene = MapWorldDocument.read(project).select('workshop');
      final expected =
          jsonDecode(
                File(
                  '${fixtures.path}/authored-expected.json',
                ).readAsStringSync(),
              )
              as Map;
      expect(scene.authored.entities.map((e) => e.id), expected['order']);
      final child = scene.authored.entities.firstWhere((e) => e.id == 'child');
      expect(child.parentId, 'root');
      expect(child.transform.w, closeTo(0.8660254, 0.000001));
      expect(scene.environment, 'day');
      final anchors = SurveyMapAnchor.read(
        File('${fixtures.path}/authored-anchors.json').readAsStringSync(),
      );
      final result = SurveyAnchorResolution.resolve(scene.authored, anchors);
      expect(result.isComplete, expected['isComplete']);
      expect(
        result.anchors
            .map(
              (a) => {
                'key': a.anchor.key,
                'match': a.matchName,
                'entityId': a.entity?.id,
                'changed': a.changed,
              },
            )
            .toList(),
        expected['outcomes'],
      );
      final updated = SurveyMapAnchor.read(jsonEncode(result.updatedFile()));
      expect(updated[2].entityId, 'crate');
      expect(updated[3].entityId, 'oldfar');
      expect(updated[4].entityId, 'oldconsole');
      expect(result.report, contains('re-survey'));
    },
  );
  test('hostile records fail before authoring a snapshot', () {
    final deep = <String, Object?>{};
    for (var i = 0; i < 130; i++) {
      deep['e${i.toString().padLeft(3, '0')}'] = {
        'components': <String, Object?>{},
        if (i > 0) 'parent': 'e${(i - 1).toString().padLeft(3, '0')}',
      };
    }
    expect(() => MapAuthoredScene.read('scene', deep), throwsFormatException);
    final json = utf8.decode(project);
    for (final bad in [
      json.replaceAll('"child":', '"bad/id":'),
      json.replaceAll('"Group"', '"${'n' * 129}"'),
      json.replaceAll('"parent": "root"', '"parent": "child"'),
      json.replaceAll('"parent": "root"', '"parent": "absent"'),
      json.replaceAll('"x": 12', '"x": 1e50'),
      json.replaceAll('"x": 12', '"x": 12, "x": 13'),
      json.replaceAll('"x": 12', '"x": "12"'),
    ]) {
      expect(
        () => MapWorldDocument.read(utf8.encode(bad)),
        throwsFormatException,
      );
    }
  });
  test('anchors refuse duplicate keys, unsafe types and missing identifiers', () {
    for (final text in [
      '{"schemaVersion":1,"anchors":[],"unknown":1e999}',
      '{"schemaVersion":1,"anchors":[],"unknown":"\\ud800"}',
      '{"schemaVersion":2,"anchors":[]}',
      '{"schemaVersion":1,"anchors":[{"key":"x","entityId":"entry","required":3}]}',
      '{"schemaVersion":1,"anchors":[{"key":"x","entityId":"bad/id"}]}',
      '{"schemaVersion":1,"anchors":[{"key":"x","entityId":"entry"},{"key":"x","entityId":"crate"}]}',
    ]) {
      expect(() => SurveyMapAnchor.read(text), throwsFormatException);
    }
  });
}
