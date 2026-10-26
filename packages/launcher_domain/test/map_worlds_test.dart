import 'dart:convert';
import 'dart:io';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  test('V6 map migration preserves source text without inserting defaults', () {
    var root = Directory.current;
    while (!File('${root.path}/TopiaForge.slnx').existsSync()) {
      root = root.parent;
    }
    for (final name in ['published-defaults', 'file-unhashed']) {
      final fixture =
          jsonDecode(
                File(
                  '${root.path}/tests/fixtures/gamemode-v6/serialization/manifest/v6-map-$name.json',
                ).readAsStringSync(),
              )
              as Map;
      final source =
          '${const JsonEncoder.withIndent('  ').convert(fixture['manifest'])}\n';
      final plan = const ManifestMigrationPlanner().plan(
        source,
        sourceLabel: 'topiaforge.mod.json',
      );
      expect(plan.disposition, ManifestMigrationDisposition.unchanged);
      expect(plan.sourceText, source);
      expect(plan.outputText, isNull);
      expect(plan.diagnostics, isEmpty);
    }
  });
  test('typed map defaults and source identity survive serialization', () {
    const world = ModWorldDeclaration(
      id: 'example.map.world',
      name: 'Map',
      content: ModWorldContent(
        kind: ModWorldContent.mapKind,
        map: 'Map_01',
        scene: 'Scene_2',
      ),
    );
    expect(world.transitions, [ModTransitions.sceneReplacement]);
    expect(world.spawn!.kind, ModSpawnPolicy.providerDefaultKind);
    final copy = ModWorldDeclaration.fromJson(world.toJson());
    expect(copy.toJson(), world.toJson());
    expect(copy.content!.map, 'Map_01');
    expect(copy.content!.scene, 'Scene_2');
    expect(const ModWorldDeclaration().transitions, isEmpty);
    expect(const ModWorldDeclaration().spawn, isNull);
  });
  test('map feature floor uses the real production predicate', () {
    for (final range in [
      '*',
      '>=0.1.0 <1.0.0',
      '>=0.2.0-rc.1 <1.0.0',
      '>=0.2.0 <1.0.0',
    ]) {
      final manifest = ModManifest.fromJson({
        'schemaVersion': 6,
        'name': 'example.map',
        'displayName': 'Map',
        'version': '1.0.0',
        'author': {'name': 'TopiaForge'},
        'entryAssembly': 'Example.dll',
        'entryType': 'Example.Mod',
        'supportedGameVersionRange': '*',
        'supportedSdkVersionRange': '*',
        'supportedLoaderVersionRange': range,
        'contributions': {
          'worlds': [
            {
              'id': 'example.map.world',
              'name': 'Map',
              'content': {'kind': 'map', 'map': 'Example'},
            },
          ],
        },
      });
      final issues = ManifestFeatureRequirements.current.validate(manifest);
      expect(issues.isEmpty, range == '>=0.2.0 <1.0.0');
      if (issues.isNotEmpty) {
        expect(issues.single.message, contains('TFPKG180'));
      }
    }
  });
}
