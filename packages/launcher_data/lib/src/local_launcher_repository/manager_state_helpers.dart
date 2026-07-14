part of '../local_launcher_repository.dart';

extension _ManagerStateHelpers on LocalLauncherRepository {
  Future<List<InstalledMod>> _loadInstalledMods(GameInstall install) async {
    final packages = <InstalledMod>[];
    final state = await _readManagerState(install);
    final stateById = _stateByModId(state);
    final catalog = await _loadInstalledVersionCatalog(
      install,
      stateById: stateById,
    );
    for (final entry in catalog.entries) {
      final versions = entry.value;
      if (versions.isEmpty) {
        continue;
      }
      packages.add(_pickCurrentVersion(versions, stateById[entry.key]));
    }

    packages.sort(
      (a, b) => a.name.toLowerCase().compareTo(b.name.toLowerCase()),
    );
    return packages;
  }

  Future<Map<String, List<InstalledMod>>> _loadInstalledVersionCatalog(
    GameInstall install, {
    Map<String, Map<dynamic, dynamic>>? stateById,
  }) async {
    final effectiveState =
        stateById ?? _stateByModId(await _readManagerState(install));
    final catalog = <String, List<InstalledMod>>{};
    final root = _packagesRoot(install);
    if (!root.existsSync()) {
      return catalog;
    }

    for (final idDir
        in root.listSync(followLinks: false).whereType<Directory>()) {
      final key = p.basename(idDir.path).toLowerCase();
      final versions = catalog.putIfAbsent(key, () => <InstalledMod>[]);
      for (final versionDir
          in idDir.listSync(followLinks: false).whereType<Directory>()) {
        versions.add(
          await _readInstalledVersion(idDir, versionDir, effectiveState),
        );
      }
      versions.sort((left, right) {
        final leftVersion = SemanticVersion.tryParse(left.version);
        final rightVersion = SemanticVersion.tryParse(right.version);
        if (leftVersion == null || rightVersion == null) {
          return left.version.compareTo(right.version);
        }
        return leftVersion.compareTo(rightVersion);
      });
    }
    return catalog;
  }

  Map<String, Map<dynamic, dynamic>> _stateByModId(Map<String, Object?> state) {
    final result = <String, Map<dynamic, dynamic>>{};
    for (final item in (state['mods'] as List).whereType<Map>()) {
      final id = item['id'] as String?;
      if (id != null && ModManifest.isValidId(id)) {
        result[id.toLowerCase()] = item;
      }
    }
    return result;
  }

  Future<InstalledMod> _readInstalledVersion(
    Directory idDir,
    Directory versionDir,
    Map<String, Map<dynamic, dynamic>> stateById,
  ) async {
    final manifestFile = File(p.join(versionDir.path, 'topiaforge.mod.json'));
    if (!manifestFile.existsSync()) {
      return InstalledMod(
        id: p.basename(idDir.path),
        name: p.basename(idDir.path),
        version: p.basename(versionDir.path),
        enabled: false,
        restartRequired: false,
        uninstallPending: false,
        packagePath: versionDir.path,
        errors: const ['Missing topiaforge.mod.json.'],
      );
    }

    try {
      final manifest = ModManifest.fromJson(
        jsonDecode(
              utf8.decode(
                await _readLauncherFileBounded(
                  manifestFile,
                  _maxLauncherManifestBytes,
                ),
              ),
            )
            as Map<String, Object?>,
      );
      final stateItem = stateById[manifest.id.toLowerCase()];
      final errors = <String>[
        ...manifest
            .validate()
            .where((issue) => issue.isBlocking)
            .map((issue) => issue.message),
        if (p.basename(idDir.path).toLowerCase() != manifest.id.toLowerCase())
          'Package directory id does not match manifest id ${manifest.id}.',
        if (p.basename(versionDir.path) != manifest.version)
          'Package directory version does not match manifest version '
              '${manifest.version}.',
      ];
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
        errors: errors,
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

    final decoded = jsonDecode(
      utf8.decode(await _readLauncherFileBounded(file, _maxManagerStateBytes)),
    );
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
    await _writeJsonFileAtomic(file, state);
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

const _maxLauncherManifestBytes = 1024 * 1024;

const _maxManagerStateBytes = 16 * 1024 * 1024;
