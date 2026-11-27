import 'dart:convert';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  test('authored order is independent of JSON insertion order', () {
    final records = <String, dynamic>{
      'z': {'components': <String, dynamic>{}},
      'child': {'parent': 'z', 'components': <String, dynamic>{}},
      'a': {'components': <String, dynamic>{}},
    };
    final forward = MapAuthoredScene.read('scene', records);
    final reverse = MapAuthoredScene.read(
      'scene',
      Map.fromEntries(records.entries.toList().reversed),
    );
    expect(forward.entities.map((e) => e.id), ['a', 'z', 'child']);
    expect(
      reverse.entities.map((e) => e.toSurveyRecord()).toList(),
      forward.entities.map((e) => e.toSurveyRecord()).toList(),
    );
  });

  test(
    'anchor count is bounded at 1024 and optional misses retain evidence',
    () {
      final text = jsonEncode({
        'schemaVersion': 1,
        'anchors': [
          for (var i = 0; i < 1024; i++)
            {'key': 'key$i', 'entityId': 'missing', 'required': false},
        ],
      });
      final anchors = SurveyMapAnchor.read(text);
      final resolution = SurveyAnchorResolution.resolve(
        MapAuthoredScene.read('scene', {}),
        anchors,
      );
      expect(resolution.isComplete, isTrue);
      expect(
        resolution.updatedFile()['anchors'],
        anchors.map((anchor) => anchor.toJson()).toList(),
      );
      final excess = jsonDecode(text) as Map<String, dynamic>;
      (excess['anchors'] as List).add({'key': 'overflow', 'entityId': 'entry'});
      expect(
        () => SurveyMapAnchor.read(jsonEncode(excess)),
        throwsFormatException,
      );
    },
  );

  test('scene identities accept 128 ASCII characters and reject 129', () {
    MapWorldDocument parse(int length) => MapWorldDocument.read(
      utf8.encode(
        jsonEncode({
          'scenes': {
            's' * length: {'entities': <String, dynamic>{}},
          },
        }),
      ),
    );
    expect(parse(128).select(null).id.length, 128);
    expect(() => parse(129), throwsFormatException);
  });
}
