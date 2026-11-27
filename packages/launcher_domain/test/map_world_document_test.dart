import 'dart:convert';
import 'dart:io';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  test('published references accept only ids and exact public map pages', () {
    expect(MapReference.parse('courtyard-1').id, 'courtyard-1');
    expect(
      MapReference.parse('https://robotopia.gg/map/courtyard-1/').id,
      'courtyard-1',
    );
    for (final value in [
      'http://robotopia.gg/map/a',
      'https://other.example/map/a',
      'https://robotopia.gg:8443/map/a',
      'https://name@robotopia.gg/map/a',
      'https://robotopia.gg/map/a?token=x',
      'https://robotopia.gg/map/a#x',
      'https://robotopia.gg/map/%61',
      'https://robotopia.gg/map/a/extra',
      '',
      'a' * 65,
      '../map',
    ]) {
      expect(() => MapReference.parse(value), throwsFormatException);
    }
  });
  test('synthetic fixture retains authored ids and unique spawn', () {
    final file = File('../../tests/fixtures/maps/courtyard.json');
    final map = MapWorldDocument.read(file.readAsBytesSync());
    expect(map.name, 'Synthetic Courtyard');
    expect(map.select(null).entities, hasLength(296));
    expect(map.select('courtyard').spawns, {'entry': 'Entry'});
    map.select(null).requireSpawn();
    expect(() => map.select('missing'), throwsStateError);
  });
  test(
    'strict JSON rejects duplicate decoded keys, nesting and nonfinite numbers',
    () {
      for (final json in [
        '{"a":1,"a":2}',
        '{"a":1,"\\u0061":2}',
        '{"n":1e999}',
        '{"s":"\\ud800"}',
        '{"s":"\\u 041"}',
        '{"n":01}',
        '{"a":[1,]}',
        '{"a":${'[' * 65}0${']' * 65}}',
      ]) {
        expect(
          () => MapWorldDocument.read(utf8.encode(json)),
          throwsFormatException,
        );
      }
      expect(() => MapWorldDocument.read([255]), throwsFormatException);
    },
  );
  test(
    'invalid identities, missing parents, cycles and excessive entities refuse',
    () {
      for (final entities in <String, Object?>{
        'mismatch': {
          'x': {'id': 'y', 'components': {}},
        },
        'missing': {
          'x': {'parent': 'absent', 'components': {}},
        },
        'cycle': {
          'x': {'parent': 'x', 'components': {}},
        },
        'count': {
          for (var i = 0; i <= 50000; i++) 'e$i': {'components': {}},
        },
      }.values) {
        expect(
          () => MapWorldDocument.read(
            utf8.encode(
              jsonEncode({
                'scenes': {
                  'scene': {'entities': entities},
                },
              }),
            ),
          ),
          throwsFormatException,
        );
      }
    },
  );
  test('multiple scenes and spawns require explicit selection', () {
    final map = MapWorldDocument.read(
      utf8.encode(
        jsonEncode({
          'scenes': {
            for (final scene in ['one', 'two'])
              scene: {
                'entities': {
                  for (final spawn in ['Entry', 'Exit'])
                    spawn: {
                      'name': spawn,
                      'components': {'spawn-location': {}},
                    },
                },
              },
          },
        }),
      ),
    );
    expect(() => map.select(null), throwsStateError);
    expect(() => map.select('one').requireSpawn(), throwsStateError);
    map.select('two').requireSpawn('Exit');
    expect(() => map.select('two').requireSpawn('missing'), throwsStateError);
  });
}
