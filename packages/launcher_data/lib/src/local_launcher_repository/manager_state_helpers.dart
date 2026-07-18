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

    packages.sort((left, right) {
      final name = left.name.toLowerCase().compareTo(right.name.toLowerCase());
      if (name != 0) return name;
      final id = left.id.toLowerCase().compareTo(right.id.toLowerCase());
      if (id != 0) return id;
      final version = _compareVersionText(left.version, right.version);
      return version != 0
          ? version
          : left.packagePath.compareTo(right.packagePath);
    });
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
          await _readInstalledVersion(
            install,
            idDir,
            versionDir,
            effectiveState,
          ),
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
    GameInstall install,
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
        repairable: true,
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
      final structuralErrors = <String>[
        ...manifest
            .validate()
            .where((issue) => issue.isBlocking)
            .map((issue) => issue.message),
        if (p.basename(idDir.path) != manifest.id)
          'Package directory id does not match manifest id ${manifest.id}.',
        if (p.basename(versionDir.path) != manifest.version)
          'Package directory version does not match manifest version '
              '${manifest.version}.',
      ];
      final compatibilityErrors = _dependencyPlanner
          .runtimeCompatibilityIssues(
            manifest,
            gameVersion: install.gameVersion,
            requireKnownGameVersion: true,
            loaderVersion: _loaderVersion,
            sdkVersion: _sdkVersion,
            platform: _gamePlatform(install),
            architecture: _gameArchitecture(install),
            contentTargets: _gameContentTargets(install),
          )
          .map((issue) => issue.message)
          .toList(growable: false);
      final errors = <String>[...structuralErrors, ...compatibilityErrors];
      final packageValidation = errors.isEmpty
          ? await _validateInstalledPackage(versionDir, manifest)
          : const _InstalledPackageValidation(
              errors: [],
              sourceSha256: '',
              trust: '',
            );
      errors.addAll(packageValidation.errors);
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
        versionPinned: (stateItem?['versionPinned'] as bool?) ?? false,
        requestedVersion: (stateItem?['version'] as String?) ?? '',
        sourceSha256: packageValidation.sourceSha256,
        trust: packageValidation.trust,
        repairable:
            structuralErrors.isNotEmpty || packageValidation.errors.isNotEmpty,
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
        repairable: true,
      );
    }
  }

  InstalledMod _pickCurrentVersion(
    List<InstalledMod> versions,
    Map<dynamic, dynamic>? stateItem,
  ) {
    final requestedVersion = (stateItem?['version'] as String?) ?? '';
    final pinned = (stateItem?['versionPinned'] as bool?) ?? false;
    InstalledMod selected;
    String selectionReason;
    if (pinned) {
      final pinnedSelection = versions
          .where((candidate) => candidate.version == requestedVersion)
          .firstOrNull;
      if (pinnedSelection == null) {
        final first = versions.first;
        selected = InstalledMod(
          id: (stateItem?['id'] as String?) ?? first.id,
          name: (stateItem?['name'] as String?) ?? first.name,
          version: requestedVersion,
          enabled: (stateItem?['enabled'] as bool?) ?? true,
          restartRequired: (stateItem?['restartRequired'] as bool?) ?? false,
          uninstallPending: (stateItem?['uninstallPending'] as bool?) ?? false,
          packagePath: p.join(p.dirname(first.packagePath), requestedVersion),
          installedAtUtc: (stateItem?['installedAtUtc'] as String?) ?? '',
          updatedAtUtc: (stateItem?['updatedAtUtc'] as String?) ?? '',
          errors: [
            "Pinned version '$requestedVersion' is not installed; refusing to fall back. Repair or change the profile pin.",
          ],
          versionPinned: true,
          requestedVersion: requestedVersion,
          repairable: true,
        );
      } else {
        selected = pinnedSelection;
      }
      selectionReason = "exact profile pin '$requestedVersion'";
    } else {
      final valid = versions.where((candidate) => candidate.isValid).toList()
        ..sort(_compareInstalledVersionsDescending);
      if (valid.isNotEmpty) {
        selected = valid.first;
        selectionReason = requestedVersion.isEmpty
            ? "highest compatible version '${selected.version}' selected for an unpinned profile"
            : requestedVersion == selected.version
            ? "highest compatible unpinned version '${selected.version}' retained"
            : "recovered unpinned selection from '$requestedVersion' to highest compatible version '${selected.version}'";
      } else {
        final deterministic = [...versions]
          ..sort(_compareInstalledVersionsDescending);
        selected = deterministic.first;
        selectionReason =
            "no compatible installed version is available; '${selected.version}' is shown for repair and launch remains blocked";
      }
    }

    final statuses = <InstalledModVersionStatus>[
      for (final version in versions)
        InstalledModVersionStatus(
          version: version.version,
          packagePath: version.packagePath,
          errors: version.errors,
          selected: version.packagePath == selected.packagePath,
          sourceSha256: version.sourceSha256,
          trust: version.trust,
          repairable: version.repairable,
        ),
    ];
    if (!statuses.any((status) => status.selected)) {
      statuses.add(
        InstalledModVersionStatus(
          version: selected.version,
          packagePath: selected.packagePath,
          errors: selected.errors,
          selected: true,
          repairable: selected.repairable,
        ),
      );
    }
    statuses.sort((left, right) {
      final comparison = _compareVersionText(left.version, right.version);
      return comparison != 0
          ? comparison
          : left.packagePath.compareTo(right.packagePath);
    });
    return InstalledMod(
      id: selected.id,
      name: selected.name,
      version: selected.version,
      enabled: selected.enabled,
      restartRequired: selected.restartRequired,
      uninstallPending: selected.uninstallPending,
      packagePath: selected.packagePath,
      manifest: selected.manifest,
      installedAtUtc: selected.installedAtUtc,
      updatedAtUtc: selected.updatedAtUtc,
      errors: selected.errors,
      versionPinned: pinned,
      requestedVersion: requestedVersion,
      selectionReason: selectionReason,
      installedVersions: statuses,
      sourceSha256: selected.sourceSha256,
      trust: selected.trust,
      repairable: selected.repairable,
    );
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

int _compareInstalledVersionsDescending(InstalledMod left, InstalledMod right) {
  final version = _compareVersionText(right.version, left.version);
  return version != 0 ? version : left.packagePath.compareTo(right.packagePath);
}

int _compareVersionText(String left, String right) {
  final leftVersion = SemanticVersion.tryParse(left);
  final rightVersion = SemanticVersion.tryParse(right);
  if (leftVersion == null || rightVersion == null) {
    return left.compareTo(right);
  }
  return leftVersion.compareTo(rightVersion);
}
