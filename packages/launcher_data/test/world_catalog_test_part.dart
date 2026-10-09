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

    await expectLater(
      repository().installPackage(package.path, install),
      throwsA(
        predicate<Object>(
          (error) => error.toString().contains('worldGamemodes'),
        ),
      ),
    );
    final snapshot = await repository().loadSnapshot();

    expect(
      snapshot.installedMods.where((mod) => mod.id == 'mode.mod'),
      isEmpty,
    );
    expect(snapshot.previewsByProfile, isNotEmpty);
    expect(
      snapshot.previewsByProfile.values.expand((preview) => preview.targets),
      isEmpty,
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

    expect(snapshot.previewsByProfile, isNotEmpty);
    expect(
      snapshot.previewsByProfile.values.expand((preview) => preview.worlds),
      isEmpty,
    );
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
      snapshot.installedMods.where((mod) => mod.id == 'registry.sample'),
      isNotEmpty,
    );
    expect(snapshot.previewsByProfile, isNotEmpty);
    expect(
      snapshot.previewsByProfile.values.expand((preview) => preview.targets),
      isEmpty,
    );
  });
}
