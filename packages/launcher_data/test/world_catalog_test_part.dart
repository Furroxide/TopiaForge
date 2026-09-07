part of 'launcher_data_test.dart';

/// What the launcher offers as launchable, and where each source of it comes from.
void _registerWorldCatalogTests({
  required Directory Function() root,
  required Directory Function() gameRoot,
  required LocalLauncherRepository Function() repository,
}) {
  test('legacy manifest metadata cannot become a launch target', () async {
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
      isNot(contains('mode.mod.survival')),
    );
  });

  test('obsolete unprovenanced catalog cannot add launch content', () async {
    final install = await repository().selectGameDirectory(gameRoot().path);
    // Old catalogs had no profile/package provenance. Even valid-looking
    // declarations in this legacy file cannot become launch authority.
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

    expect(snapshot.worldCatalog.worlds, isEmpty);
    expect(snapshot.worldCatalog.gamemodes, isEmpty);
    expect(snapshot.worldCatalog.menuEntries, isEmpty);
    expect(
      snapshot.previewsByProfile.values.expand((preview) => preview.targets),
      isEmpty,
    );
    expect(install.path, gameRoot().path);
  });

  test('registry metadata cannot become launch content', () async {
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
      isNot(contains('registry.sample.survival')),
    );
  });
}
