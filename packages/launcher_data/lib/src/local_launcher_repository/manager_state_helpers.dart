part of '../local_launcher_repository.dart';

extension _ManagerStateHelpers on LocalLauncherRepository {
  Future<List<InstalledMod>> _loadInstalledMods(GameInstall install) async {
    final packages = <InstalledMod>[];
    final state = await _readManagerState(install);
    final stateById = <String, Map<dynamic, dynamic>>{};
    for (final item in (state['mods'] as List).whereType<Map>()) {
      final id = item['id'] as String?;
      if (id != null) {
        stateById[id.toLowerCase()] = item;
      }
    }

    final root = _packagesRoot(install);
    if (!root.existsSync()) {
      return packages;
    }

    for (final idDir in root.listSync().whereType<Directory>()) {
      final versions = <InstalledMod>[];
      for (final versionDir in idDir.listSync().whereType<Directory>()) {
        versions.add(_readInstalledVersion(idDir, versionDir, stateById));
      }
      packages.add(
        _pickCurrentVersion(
          versions,
          stateById[p.basename(idDir.path).toLowerCase()],
        ),
      );
    }

    packages.sort(
      (a, b) => a.name.toLowerCase().compareTo(b.name.toLowerCase()),
    );
    return packages;
  }

  InstalledMod _readInstalledVersion(
    Directory idDir,
    Directory versionDir,
    Map<String, Map<dynamic, dynamic>> stateById,
  ) {
    final manifestFile = File(p.join(versionDir.path, 'robotopia.mod.json'));
    if (!manifestFile.existsSync()) {
      return InstalledMod(
        id: p.basename(idDir.path),
        name: p.basename(idDir.path),
        version: p.basename(versionDir.path),
        enabled: false,
        restartRequired: false,
        uninstallPending: false,
        packagePath: versionDir.path,
        errors: const ['Missing robotopia.mod.json.'],
      );
    }

    try {
      final manifest = ModManifest.fromJson(
        jsonDecode(manifestFile.readAsStringSync()) as Map<String, Object?>,
      );
      final stateItem = stateById[manifest.id.toLowerCase()];
      return InstalledMod(
        id: manifest.id,
        name: manifest.name,
        version: manifest.version,
        enabled: (stateItem?['enabled'] as bool?) ?? true,
        restartRequired: (stateItem?['restartRequired'] as bool?) ?? false,
        uninstallPending: (stateItem?['uninstallPending'] as bool?) ?? false,
        installedAtUtc: (stateItem?['installedAtUtc'] as String?) ?? '',
        updatedAtUtc: (stateItem?['updatedAtUtc'] as String?) ?? '',
        packagePath: versionDir.path,
        manifest: manifest,
        errors: manifest.validate().map((issue) => issue.message).toList(),
      );
    } on Object catch (error) {
      return InstalledMod(
        id: p.basename(idDir.path),
        name: p.basename(idDir.path),
        version: p.basename(versionDir.path),
        enabled: false,
        restartRequired: false,
        uninstallPending: false,
        packagePath: versionDir.path,
        errors: ['Failed to read manifest: $error'],
      );
    }
  }

  InstalledMod _pickCurrentVersion(
    List<InstalledMod> versions,
    Map<dynamic, dynamic>? stateItem,
  ) {
    if (versions.length == 1) {
      return versions.single;
    }

    final selectedVersion = stateItem?['version'] as String?;
    if (selectedVersion != null) {
      for (final version in versions) {
        if (version.version.toLowerCase() == selectedVersion.toLowerCase()) {
          return version;
        }
      }
    }

    versions.sort((a, b) {
      final aVersion = SemanticVersion.tryParse(a.version);
      final bVersion = SemanticVersion.tryParse(b.version);
      if (aVersion == null || bVersion == null) {
        return b.version.compareTo(a.version);
      }
      return bVersion.compareTo(aVersion);
    });
    return versions.first;
  }

  Future<Map<String, Object?>> _readManagerState(GameInstall install) async {
    final file = _managerStateFile(install);
    if (!file.existsSync()) {
      return {'mods': <Object?>[]};
    }

    final decoded = jsonDecode(await file.readAsString());
    if (decoded is Map<String, Object?> && decoded['mods'] is List) {
      return decoded;
    }
    return {'mods': <Object?>[]};
  }

  Future<void> _saveManagerState(
    GameInstall install,
    Map<String, Object?> state,
  ) async {
    final file = _managerStateFile(install);
    await file.create(recursive: true);
    await file.writeAsString(_prettyJson(state));
  }

  void _upsertState(
    Map<String, Object?> state,
    ModManifest manifest, {
    required bool enabled,
    required bool restartRequired,
    bool preserveExistingEnabled = false,
  }) {
    final mods = (state['mods'] as List).whereType<Map>().toList();
    Map<dynamic, dynamic>? item;
    for (final candidate in mods) {
      if ((candidate['id'] as String?)?.toLowerCase() ==
          manifest.id.toLowerCase()) {
        item = candidate;
        break;
      }
    }

    final now = DateTime.now().toUtc().toIso8601String();
    if (item == null) {
      item = {'id': manifest.id, 'installedAtUtc': now};
      mods.add(item);
    }

    final existingEnabled = item['enabled'] as bool?;
    item['name'] = manifest.name;
    item['version'] = manifest.version;
    item['enabled'] = preserveExistingEnabled
        ? existingEnabled ?? enabled
        : enabled;
    item['restartRequired'] = restartRequired;
    item['uninstallPending'] = false;
    item['updatedAtUtc'] = now;
    state['mods'] = mods;
  }
}
