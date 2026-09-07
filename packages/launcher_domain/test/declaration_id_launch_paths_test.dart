import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

String declarationId(int length) => 'mode.${'x' * (length - 5)}';

Map<String, Object?> catalogJson(String id) => {
  'worlds': [
    {'id': 'base.world', 'name': 'Base'},
    {'id': id, 'name': 'Boundary world'},
  ],
  'gamemodes': [
    {'id': 'base.mode', 'name': 'Base'},
    {'id': id, 'name': 'Boundary mode'},
  ],
  'menuEntries': [
    {'id': id, 'title': 'Boundary target', 'gamemodeId': id, 'worldId': id},
  ],
};

void main() {
  for (final length in [65, 96]) {
    final id = declarationId(length);
    test('catalog retains $length-character declaration identities', () {
      final catalog = WorldCatalog.fromJson(catalogJson(id));
      expect(catalog.worlds.map((world) => world.id), contains(id));
      expect(catalog.gamemodes.map((mode) => mode.id), contains(id));
      final entry = catalog.menuEntries.single;
      expect(entry.id, id);
      expect(entry.gamemodeId, id);
      expect(entry.worldId, id);
    });

    test('profile persists and writes $length-character launch IDs', () {
      final profile = LauncherProfile(
        id: 'boundary',
        name: 'Boundary',
        launchSelection: LaunchSelection.target(
          LaunchRequest(targetId: id, worldOverride: id),
        ),
      );
      final restored = LauncherProfile.fromJson(profile.toJson());
      expect(restored.launchSelection.request!.targetId, id);
      expect(restored.launchSelection.request!.worldOverride, id);
      final plan = LaunchPlanDescriptor(
        targetId: id,
        gamemodeId: id,
        worldId: id,
        transition: ModTransitions.additiveArena,
        request: restored.launchSelection.request!,
        packages: [],
      );
      final wire = ProfileLaunchConfigurationV4(
        profileId: profile.id,
        profileRevision: 0,
        requestId: 'boundary-request',
        command: 'launch-target',
        safeMode: false,
        inheritManagerModState: false,
        enabledMods: [],
        selectedVersions: {},
        packages: [],
        plan: plan,
      );
      final read = LaunchTransport.readProfile(
        LaunchTransport.writeProfile(wire),
      );
      expect(read.plan!.targetId, id);
      expect(read.plan!.worldId, id);
      expect(read.plan!.gamemodeId, id);
    });
  }

  for (final id in [declarationId(97), 'mode.é']) {
    test('invalid declaration $id is rejected across launch paths', () {
      final catalog = WorldCatalog.fromJson(catalogJson(id));
      expect(catalog.worlds.map((world) => world.id), isNot(contains(id)));
      expect(catalog.gamemodes.map((mode) => mode.id), isNot(contains(id)));
      expect(catalog.menuEntries, isEmpty);
      for (final selection in [
        WorldSelection(worldId: id),
        WorldSelection(gamemodeId: id),
      ]) {
        expect(
          () => WorldSelection.fromJson(selection.toJson()),
          throwsFormatException,
        );
        expect(
          () => LaunchRequest(targetId: 'valid.target', worldOverride: id),
          throwsFormatException,
        );
        expect(() => LaunchRequest(targetId: id), throwsFormatException);
      }
    });
    test('invalid menu world reference $id is filtered independently', () {
      final catalog = WorldCatalog.fromJson({
        ...catalogJson('base.valid'),
        'menuEntries': [
          {
            'id': 'base.target',
            'title': 'Target',
            'gamemodeId': 'base.mode',
            'worldId': id,
          },
        ],
      });
      expect(catalog.menuEntries, isEmpty);
    });
  }

  test('package identities retain their 64-character boundary', () {
    final legal = declarationId(64);
    final illegal = declarationId(65);
    expect(ModManifest.isValidId(legal), isTrue);
    expect(ModManifest.isValidId(illegal), isFalse);
    for (final profile in [
      LauncherProfile(id: 'enabled', name: 'Enabled', enabledMods: {illegal}),
      LauncherProfile(
        id: 'pinned',
        name: 'Pinned',
        selectedVersions: {illegal: '1.0.0'},
      ),
    ]) {
      expect(
        () => ProfileLaunchConfigurationV4(
          profileId: profile.id,
          profileRevision: 0,
          requestId: 'boundary-request',
          command: 'main-menu',
          safeMode: false,
          inheritManagerModState: false,
          enabledMods: profile.enabledMods,
          selectedVersions: profile.selectedVersions,
          packages: [],
        ),
        throwsFormatException,
      );
    }
  });
}
