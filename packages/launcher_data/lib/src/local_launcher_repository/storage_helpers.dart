part of '../local_launcher_repository.dart';

extension _StorageHelpers on LocalLauncherRepository {
  Future<List<LauncherProfile>> _loadProfiles() async {
    if (!_profilesFile.existsSync()) {
      final defaults = [LauncherProfile.defaultProfile()];
      await saveProfiles(defaults, defaults.first.id);
      return defaults;
    }

    final decoded = jsonDecode(await _profilesFile.readAsString());
    final profiles = (decoded is Map ? decoded['profiles'] : null) as List?;
    final result = profiles == null
        ? <LauncherProfile>[]
        : profiles
              .whereType<Map>()
              .map(
                (item) => LauncherProfile.fromJson(
                  item.map((key, value) => MapEntry(key.toString(), value)),
                ),
              )
              .toList();
    return result.isEmpty ? [LauncherProfile.defaultProfile()] : result;
  }

  Future<List<PackageSource>> _loadPackageSources() async {
    if (!_sourcesFile.existsSync()) {
      return _defaultPackageSources();
    }

    final decoded = jsonDecode(await _sourcesFile.readAsString());
    final sources = (decoded is Map ? decoded['sources'] : null) as List?;
    final builtIns = _defaultPackageSources();
    final parsed = sources == null
        ? <PackageSource>[]
        : sources
              .whereType<Map>()
              .map(
                (item) => PackageSource.fromJson(
                  item.map((key, value) => MapEntry(key.toString(), value)),
                ),
              )
              .where(
                (source) =>
                    source.id.trim().isNotEmpty && source.url.trim().isNotEmpty,
              )
              // Built-in sources are app-managed: always reconcile their URL to the current
              // default so an older persisted entry (e.g. one that still points at a removed
              // mod_registry.json file) cannot pin a stale catalog location. Only the player's
              // enabled flag survives.
              .map((source) {
                final builtIn = builtIns
                    .where((item) => item.id == source.id)
                    .firstOrNull;
                return builtIn == null
                    ? source
                    : builtIn.copyWith(enabled: source.enabled);
              })
              .toList();
    // Append built-ins the persisted file predates (e.g. the official registry
    // added in an update). They can be disabled but never removed.
    for (final builtIn in builtIns) {
      if (!parsed.any((source) => source.id == builtIn.id)) {
        parsed.add(builtIn);
      }
    }
    return parsed;
  }

  List<PackageSource> _defaultPackageSources() {
    return [
      PackageSource(
        id: 'robotopia.local',
        name: 'Bundled Local Packages',
        // Point at the directory of built .robotopiamod packages. The catalog is derived
        // directly from those packages (manifest + sha read from each file), so there is no
        // separate metadata to drift out of sync with the packages themselves.
        url: Uri.file(p.join(_repositoryRoot.path, 'dist')).toString(),
        builtIn: true,
      ),
      const PackageSource(
        id: ModRegistryFormat.officialSourceId,
        name: ModRegistryFormat.officialSourceName,
        url: ModRegistryFormat.officialRegistryUrl,
        builtIn: true,
      ),
    ];
  }

  Future<Map<String, Object?>> _loadSettings() async {
    if (!_settingsFile.existsSync()) {
      return <String, Object?>{};
    }

    final decoded = jsonDecode(await _settingsFile.readAsString());
    return decoded is Map<String, Object?> ? decoded : <String, Object?>{};
  }

  Future<void> _saveSettings(Map<String, Object?> settings) async {
    await _settingsFile.create(recursive: true);
    await _settingsFile.writeAsString(_prettyJson(settings));
  }

  Future<String> _readLauncherLog({int maxLines = 200}) async {
    if (!_launcherLogFile.existsSync()) {
      return '';
    }

    return _tail(await _launcherLogFile.readAsLines(), maxLines).join('\n');
  }

  Future<void> _appendLauncherLog(String message) async {
    await _launcherLogFile.create(recursive: true);
    await _launcherLogFile.writeAsString(
      '${DateTime.now().toUtc().toIso8601String()} $message\n',
      mode: FileMode.append,
    );
  }

  Future<WorldCatalog> _loadWorldCatalog(
    GameInstall install,
    List<InstalledMod> installedMods,
    List<RegistryMod> registryMods,
  ) async {
    final file = File(
      p.join(_managerData(install).path, 'robotopia.worlds', 'catalog.json'),
    );
    WorldCatalog catalog;
    if (!file.existsSync()) {
      catalog = WorldCatalog.fallback();
    } else {
      try {
        catalog = WorldCatalog.fromJson(
          jsonDecode(await file.readAsString()) as Map<String, Object?>,
        );
      } on Object catch (error) {
        await _appendLauncherLog('World catalog read failed: $error');
        catalog = WorldCatalog.fallback();
      }
    }

    return _mergeManifestGamemodes(catalog, installedMods, registryMods);
  }

  WorldCatalog _mergeManifestGamemodes(
    WorldCatalog catalog,
    List<InstalledMod> installedMods,
    List<RegistryMod> registryMods,
  ) {
    final gamemodes = [...catalog.gamemodes];
    final seen = {for (final gamemode in gamemodes) gamemode.id.toLowerCase()};
    final installedIds = {
      for (final mod in installedMods.where((mod) => mod.enabled))
        mod.id.toLowerCase(),
    };

    for (final mod in installedMods.where((mod) => mod.enabled)) {
      for (final gamemode in mod.manifest?.worldGamemodes ?? const []) {
        if (seen.add(gamemode.id.toLowerCase())) {
          gamemodes.add(gamemode);
        }
      }
    }

    for (final mod in registryMods.where(
      (mod) => installedIds.contains(mod.manifest.id.toLowerCase()),
    )) {
      for (final gamemode in mod.manifest.worldGamemodes) {
        if (seen.add(gamemode.id.toLowerCase())) {
          gamemodes.add(gamemode);
        }
      }
    }

    return WorldCatalog(worlds: catalog.worlds, gamemodes: gamemodes);
  }

  Future<void> _writeWorldSelection(
    GameInstall install,
    WorldSelection selection,
  ) async {
    final file = File(
      p.join(_managerConfig(install).path, 'robotopia.worlds.json'),
    );
    await file.create(recursive: true);
    await file.writeAsString(_prettyJson(selection.toRuntimeConfig()));
  }
}
