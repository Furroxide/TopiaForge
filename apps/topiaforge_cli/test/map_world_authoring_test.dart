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
      expect(
        contributions['gamemodes'].single['worldRequirements']['transitions'],
        ['scene-replacement'],
      );
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
