import 'dart:convert';
import 'dart:io';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

const _worlds = 'io.github.furroxide.topiaforge.worlds';

void main() {
  final root = Directory.current.parent.parent;
  ModManifest read(String name) => ModManifest.fromJson(
    jsonDecode(
          File(
            '${root.path}/mods/TopiaForge.$name/topiaforge.mod.json',
          ).readAsStringSync(),
        )
        as Map<String, Object?>,
  );
  final worlds = read('Worlds');

  test('Open Sandbox uses generated provider and both discovery sources', () {
    final declarations = worlds.contributions?.worlds ?? [];
    expect(declarations, hasLength(3));
    final byId = {for (final world in declarations) world.id: world};
    final sandbox = byId['$_worlds.open_sandbox']!;
    expect(sandbox.content!.kind, 'provider');
    expect(
      sandbox.content!.implementation!.type,
      'TopiaForge.Worlds.OpenSandboxProvider',
    );
    expect(sandbox.transitions, ['additive-arena']);
    for (final entry in {
      'level': 'CuratedLevelDiscoverySource',
      'first-party': 'BuildSceneDiscoverySource',
    }.entries) {
      final family = byId['$_worlds.${entry.key}']!;
      expect(family.content!.kind, 'discovered');
      expect(
        family.content!.implementation!.type,
        'TopiaForge.Worlds.${entry.value}',
      );
      expect(family.openToAnyCompatible, isTrue);
    }
    expect(sandbox.spawn!.kind, 'provider-default');
    expect(sandbox.openToAnyCompatible, isTrue);
  });

  for (final packageName in ['Worlds', 'Sandbox', 'Zombies']) {
    test('$packageName target resolves using its declared factory and world', () {
      final manifest = read(packageName);
      final contributions = manifest.contributions;
      expect(contributions, isNotNull);
      final mode = contributions!.gamemodes.single;
      final target = contributions.launchTargets.single;
      expect(
        mode.implementation!.type,
        'TopiaForge.$packageName.${packageName == 'Worlds' ? 'FreePlay' : packageName}Gamemode',
      );
      final profile = EffectiveProfile(
        profileId: 'activation',
        revision: 1,
        packages: [
          for (final item in [worlds, if (packageName != 'Worlds') manifest])
            ResolvedPackage(id: item.id, version: item.version, manifest: item),
        ],
      );
      final result = LaunchResolver.resolve(
        profile,
        LaunchRequest(targetId: target.id),
      );
      expect(result.blocks, isEmpty);
      expect(result.plan!.worldId, '$_worlds.open_sandbox');
      expect(result.plan!.gamemodeId, mode.id);
      expect(result.plan!.transition, 'additive-arena');
      expect(mode.sceneChangePolicy, 'end-session');
    });
  }
}
