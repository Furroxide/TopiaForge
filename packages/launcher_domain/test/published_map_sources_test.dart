import 'dart:convert';
import 'dart:io';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  final cases =
      jsonDecode(
            File(
              '../../tests/fixtures/maps/published-source-cases.json',
            ).readAsStringSync(),
          )
          as List;
  for (final item in cases) {
    test('published sources: ${item['name']}', () {
      final bytes = utf8.encode(item['json'] as String);
      if (item['valid'] == true) {
        final sources = PublishedMapSources.read(bytes);
        expect(
          PublishedMapSources.read(
            PublishedMapSources.write(sources),
          ).map((s) => s.toJson()),
          sources.map((s) => s.toJson()),
        );
      } else {
        expect(() => PublishedMapSources.read(bytes), throwsFormatException);
      }
    });
  }
  test(
    'public references normalize once and never accept live/authenticated sources',
    () {
      final source = PublishedMapSource.fromReference(
        reference: 'https://robotopia.gg/map/example/',
        scene: 'scene',
        name: ' Courtyard ',
      );
      expect(source.map, 'example');
      expect(source.name, 'Courtyard');
      for (final input in [
        'automerge:example',
        'custom://ugc/example',
        'http://robotopia.gg/map/example',
        'https://robotopia.gg/map/example?token=synthetic',
      ]) {
        expect(
          () => PublishedMapSource.fromReference(
            reference: input,
            scene: 'scene',
            name: 'Map',
          ),
          throwsFormatException,
        );
      }
    },
  );
  test('library limits apply before writing', () {
    final valid = utf8.encode('{"schemaVersion":1,"sources":[]}');
    expect(
      () => PublishedMapSources.read([
        ...valid,
        ...List.filled(PublishedMapSources.maximumBytes, 32),
      ]),
      throwsFormatException,
    );
    expect(
      () => PublishedMapSources.write(
        List.generate(
          4097,
          (i) => PublishedMapSource(map: 'm$i', scene: 'scene', name: 'Map'),
        ),
      ),
      throwsFormatException,
    );
  });
}
