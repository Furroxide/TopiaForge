part of 'launcher_data_test.dart';

/// What the launcher offers as launchable, and where each source of it comes from.
void _registerWorldCatalogTests({
  required Directory Function() root,
  required Directory Function() gameRoot,
  required LocalLauncherRepository Function() repository,
}) {
  test('a manifest cannot add a gamemode to the world catalog', () async {
    final install = await repository().selectGameDirectory(gameRoot().path);
    // The catalog is what the game reported, and that is all it is. It used to be
    // merged with every enabled manifest's gamemode list, so a mode appeared in
    // the launcher because a package mentioned it -- not because anything could
    // run it. A declaration now names its implementation, so an unrunnable
    // launch is a resolution failure the player can be told about rather than a
    // menu entry that quietly does nothing.
    final package = _createPackage(root(), id: 'mode.mod', version: '1.0.0');

    await repository().installPackage(package.path, install);
    final snapshot = await repository().loadSnapshot();

    expect(
      snapshot.worldCatalog.gamemodes.map((mode) => mode.id),
      isNot(contains('mode.mod.survival')),
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

  test('a registry entry cannot add a gamemode either', () async {
    final install = await repository().selectGameDirectory(gameRoot().path);
    final package = _createPackage(
      root(),
      id: 'registry.sample',
      version: '1.0.0',
    );

    await repository().installPackage(package.path, install);
    final snapshot = await repository().loadSnapshot();

    // Registry metadata describes a package that could be installed. Letting it
    // populate the catalog meant the launcher offered launches whose code was
    // never on this machine at all.
    expect(
      snapshot.worldCatalog.gamemodes.map((mode) => mode.id),
      isNot(contains('registry.sample.survival')),
    );
  });
}
