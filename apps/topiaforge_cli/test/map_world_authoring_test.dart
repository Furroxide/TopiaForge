import 'dart:convert';
import 'dart:io';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/map_world_authoring.dart';

Map<String, dynamic> source() {
  final fixture =
      jsonDecode(
            File(
              '../../tests/fixtures/gamemode-v6/serialization/manifest/v6-map-published-defaults.json',
            ).readAsStringSync(),
          )
          as Map;
  final manifest = fixture['manifest'] as Map<String, dynamic>;
  manifest['x-preserved'] = {'value': 42};
  manifest['supportedLoaderVersionRange'] = '>=0.1.0 <0.2.0';
  manifest['supportedSdkVersionRange'] = '>=0.1.0 <0.2.0';
  return manifest;
}

Map<String, dynamic> plan(Map<String, dynamic> manifest) => addMapWorld(
  manifest,
  reference: MapReference.parse('ExampleMap'),
  scene: 'courtyard',
  name: 'Courtyard',
);

void main() {
  for (final transitions in [
    null,
    ['additive-arena'],
    ['scene-replacement', 'additive-arena'],
  ]) {
    test('adding a map preserves shared gamemode transitions $transitions', () {
      final original = source();
      final contributions = original['contributions'] as Map;
      final mode = (contributions['gamemodes'] as List).single as Map;
      if (transitions != null) {
        mode['worldRequirements'] = {'transitions': transitions};
      }
      final world = (contributions['worlds'] as List).single as Map;
      world['content'] = {'kind': 'game-scene', 'sceneName': 'UgcPlay'};
      world['transitions'] = ['additive-arena'];
      world['spawn'] = {'kind': 'provider-default'};
      final targets = contributions['launchTargets'] as List;
      final selected = targets.single as Map;
      selected['transition'] = 'additive-arena';
      final other = jsonDecode(jsonEncode(selected)) as Map;
      other['id'] = '${original['name']}.other';
      targets.add(other);
      List<LaunchBlockCode> transitionBlocks(Map<String, dynamic> json) {
        final manifest = ModManifest.fromJson(json);
        final profile = EffectiveProfile(
          profileId: 'example',
          revision: 1,
          packages: [
            ResolvedPackage(
              id: manifest.id,
              version: manifest.version,
              manifest: manifest,
            ),
          ],
        );
        return LaunchResolver.resolve(
              profile,
              LaunchRequest(targetId: other['id'] as String),
              RuntimeObservation.none,
            ).blocks
            .map((block) => block.code)
            .where(
              (code) =>
                  code == LaunchBlockCode.transitionUnsatisfiable ||
                  code == LaunchBlockCode.transitionNotOffered,
            )
            .toList();
      }

      expect(transitionBlocks(original), isEmpty);
      final result = addMapWorld(
        original,
        reference: MapReference.parse('ExampleMap'),
        scene: 'courtyard',
        name: 'Courtyard',
        targetId: selected['id'] as String,
      );
      expect(transitionBlocks(result), isEmpty);
      expect(result['contributions']['launchTargets'].last, other);
      final requirements =
          result['contributions']['gamemodes'].single['worldRequirements']
              as Map?;
      expect(
        requirements?['transitions'],
        transitions?.isNotEmpty == true
            ? containsAll(['additive-arena', 'scene-replacement'])
            : transitions,
      );
    });
  }
  test(
    'map authoring preserves extensions and retargets one mode at stable floors',
    () {
      final original = source();
      final before = jsonEncode(original);
      final result = plan(original);
      expect(jsonEncode(original), before);
      expect(result['schemaVersion'], 6);
      expect(result['x-preserved'], {'value': 42});
      expect(result['supportedLoaderVersionRange'], '>=0.2.0 <0.3.0');
      expect(result['supportedSdkVersionRange'], '>=0.2.0 <0.3.0');
      expect(
        result['dependencies']['io.github.furroxide.topiaforge.worlds'],
        '>=0.2.0 <0.3.0',
      );
      final contributions = result['contributions'];
      expect(contributions['launchTargets'].single['world'], {
        'policy': 'fixed',
        'default': '${result['name']}.map',
      });
      expect(contributions['gamemodes'].single['worldRequirements'], isNull);
      expect(contributions['worlds'].last['content'], {
        'kind': 'map',
        'map': 'ExampleMap',
        'scene': 'courtyard',
      });
      expect(() => plan(result), throwsStateError);
    },
  );
  test(
    'ambiguous targets and old schema refuse; missing target is created',
    () {
      final manifest = source();
      final targets = manifest['contributions']['launchTargets'] as List;
      targets.add(jsonDecode(jsonEncode(targets.single)));
      expect(() => plan(manifest), throwsStateError);
      targets.clear();
      expect(plan(manifest)['contributions']['launchTargets'], hasLength(1));
      manifest['schemaVersion'] = 5;
      expect(() => plan(manifest), throwsStateError);
    },
  );
  test(
    'manifest replacement detects a concurrent edit and cleans its staging file',
    () async {
      final temp = Directory.systemTemp.createTempSync('map-author-');
      try {
        final file = File(p.join(temp.path, 'topiaforge.mod.json'))
          ..writeAsStringSync('changed');
        await expectLater(
          writeMapManifest(file, 'original', plan(source())),
          throwsStateError,
        );
        expect(file.readAsStringSync(), 'changed');
        expect(temp.listSync(), hasLength(1));
        await writeMapManifest(file, 'changed', plan(source()));
        expect(jsonDecode(file.readAsStringSync())['schemaVersion'], 6);
      } finally {
        temp.deleteSync(recursive: true);
      }
    },
  );
  test(
    'offline CLI requires scene and name then writes a fixed target without survey bytes',
    () async {
      final temp = Directory.systemTemp.createTempSync('map-cli-');
      try {
        final file = File(p.join(temp.path, 'topiaforge.mod.json'))
          ..writeAsStringSync(jsonEncode(source()));
        Future<ProcessResult> run(List<String> flags) =>
            Process.run(Platform.resolvedExecutable, [
              p.absolute('bin/topiaforge.dart'),
              'world',
              'add',
              'map',
              'https://robotopia.gg/map/ExampleMap/',
              '--project',
              temp.path,
              '--offline',
              ...flags,
            ], workingDirectory: temp.path);
        final before = file.readAsStringSync();
        final missing = await run([]);
        expect(missing.exitCode, isNot(0));
        expect(file.readAsStringSync(), before);
        final added = await run([
          '--scene',
          'courtyard',
          '--name',
          'Courtyard',
        ]);
        expect(added.exitCode, 0, reason: '${added.stdout}\n${added.stderr}');
        expect(
          jsonDecode(
            file.readAsStringSync(),
          )['contributions']['worlds'].last['content']['map'],
          'ExampleMap',
        );
        expect(Directory(p.join(temp.path, 'obj')).existsSync(), false);
      } finally {
        temp.deleteSync(recursive: true);
      }
    },
  );
}
