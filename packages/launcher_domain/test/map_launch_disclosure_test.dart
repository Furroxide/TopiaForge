import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';
import 'launch_preview_test_helpers.dart';

void main() {
  test(
    'published map disclosure follows the selected world and names its source',
    () {
      final world = ModWorldDeclaration(
        id: worldId,
        name: 'Courtyard',
        content: const ModWorldContent(
          kind: 'map',
          map: 'ExampleMap',
          scene: 'courtyard',
        ),
      );
      final preview = buildPreview(
        previewProfile([
          previewPackage(worlds: [world]),
        ]),
      );
      expect(preview.publishedMapDisclosure, contains('Courtyard'));
      expect(preview.publishedMapDisclosure, contains('robotopia.gg'));
      expect(
        preview.publishedMapDisclosure,
        contains('downloaded when you play'),
      );
    },
  );
  test(
    'bundled and native worlds have no published-map download disclosure',
    () {
      for (final content in [
        const ModWorldContent(kind: 'map', file: 'maps/courtyard.json'),
        const ModWorldContent(kind: 'game-scene', sceneName: 'UgcPlay'),
      ]) {
        final world = ModWorldDeclaration(
          id: worldId,
          name: 'Courtyard',
          content: content,
          transitions: const ['scene-replacement'],
          spawn: const ModSpawnPolicy(kind: 'provider-default'),
        );
        expect(
          buildPreview(
            previewProfile([
              previewPackage(worlds: [world]),
            ]),
          ).publishedMapDisclosure,
          isNull,
        );
      }
    },
  );
}
