part of 'launcher_data_test.dart';

/// What the launcher offers as launchable, and where each source of it comes from.
void _registerWorldCatalogTests({
  required Directory Function() root,
  required Directory Function() gameRoot,
  required LocalLauncherRepository Function() repository,
}) {
  test('adds installed manifest gamemodes to world catalog', () async {
    final install = await repository().selectGameDirectory(gameRoot().path);
    final package = _createPackage(
      root(),
      id: 'mode.mod',
      version: '1.0.0',
      worldGamemodes: [
        {
          'id': 'mode.mod.survival',
          'name': 'Survival',
          'description': 'Static gamemode metadata.',
        },
      ],
    );

    await repository().installPackage(package.path, install);
    final snapshot = await repository().loadSnapshot();

    expect(
      snapshot.worldCatalog.gamemodes.map((mode) => mode.id),
      contains('mode.mod.survival'),
    );
  });

  test('reads the world catalog the runtime actually publishes', () async {
    final install = await repository().selectGameDirectory(gameRoot().path);
    // The runtime writes this through its mod-scoped data service, which keys the directory by the
    // raw mod id. The launcher used to look under the shortened config-file name instead, found
    // nothing, and silently fell back to a one-world built-in catalog -- so every real Robotopia
    // level the runtime published was unreachable from the launcher, with no error anywhere.
    final catalogFile = File(
      p.join(
        gameRoot().path,
        'BepInEx',
        'TopiaForge',
        'data',
        'io.github.furroxide.topiaforge.worlds',
        'catalog.json',
      ),
    );
    catalogFile.parent.createSync(recursive: true);
    catalogFile.writeAsStringSync(
      jsonEncode({
        'worlds': [
          {
            'id': 'io.github.furroxide.topiaforge.worlds.level.introsewer',
            'name': 'The Sewer',
            'sceneName': 'IntroSewer',
            'firstParty': true,
            'supportsSceneReplacement': true,
            'supportsAdditiveArena': false,
          },
        ],
        'gamemodes': [
          {
            'id': 'io.github.furroxide.topiaforge.worlds.sandbox',
            'name': 'Sandbox',
          },
        ],
        'menuEntries': [
          {
            'id': 'io.github.furroxide.topiaforge.zombies.menu',
            'title': 'Zombies',
            'gamemodeId': 'io.github.furroxide.topiaforge.zombies.survival',
            'worldId': 'io.github.furroxide.topiaforge.worlds.level.introsewer',
          },
        ],
      }),
    );

    final snapshot = await repository().loadSnapshot();

    expect(
      snapshot.worldCatalog.worlds.map((world) => world.id),
      contains('io.github.furroxide.topiaforge.worlds.level.introsewer'),
      reason: 'the published world list must reach the launcher',
    );
    final entry = snapshot.worldCatalog.menuEntryFor(
      'io.github.furroxide.topiaforge.zombies.survival',
    );
    expect(entry, isNotNull);
    expect(
      entry!.worldId,
      'io.github.furroxide.topiaforge.worlds.level.introsewer',
      reason: 'a menu entry names the world its gamemode wants to start in',
    );
    expect(install.path, gameRoot().path);
  });

  test('adds installed registry gamemodes to world catalog', () async {
    final install = await repository().selectGameDirectory(gameRoot().path);
    final package = _createPackage(
      root(),
      id: 'registry.sample',
      version: '1.0.0',
    );

    await repository().installPackage(package.path, install);
    final snapshot = await repository().loadSnapshot();

    expect(
      snapshot.worldCatalog.gamemodes.map((mode) => mode.id),
      contains('registry.sample.survival'),
    );
  });
}
