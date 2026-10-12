part of '../local_launcher_repository.dart';

/// Mod-state mutations behind setModEnabled, disableAllMods, and uninstallMod:
/// each saves the updated manager state, logs the change, and returns the
/// reloaded installed mods. (Split from local_launcher_repository.dart for the
/// 500-line file cap.)
extension _ModStateMutations on LocalLauncherRepository {
  Future<List<InstalledMod>> _setModEnabled(
    GameInstall install,
    String modId,
    bool enabled,
  ) async {
    _requireSafeModId(modId);
    final state = await _readManagerState(install);
    for (final item in (state['mods'] as List).whereType<Map>()) {
      if ((item['id'] as String?)?.toLowerCase() == modId.toLowerCase()) {
        item['enabled'] = enabled;
        item['restartRequired'] = true;
        item['updatedAtUtc'] = DateTime.now().toUtc().toIso8601String();
      }
    }
    await _saveManagerState(install, state);
    await _appendLauncherLogBestEffort(
      '${enabled ? 'Enabled' : 'Disabled'} $modId.',
    );
    return _loadInstalledMods(install);
  }

  Future<List<InstalledMod>> _disableAllMods(GameInstall install) async {
    final state = await _readManagerState(install);
    for (final item in (state['mods'] as List).whereType<Map>()) {
      item['enabled'] = false;
      item['restartRequired'] = true;
      item['updatedAtUtc'] = DateTime.now().toUtc().toIso8601String();
    }
    await _saveManagerState(install, state);
    await _appendLauncherLogBestEffort('Disabled all mods.');
    return _loadInstalledMods(install);
  }

  Future<List<InstalledMod>> _uninstallMod(
    GameInstall install,
    String modId,
  ) async {
    _requireSafeModId(modId);
    final modRoot = Directory(p.join(_packagesRoot(install).path, modId));
    if (modRoot.existsSync()) {
      modRoot.deleteSync(recursive: true);
    }

    final state = await _readManagerState(install);
    final mods = (state['mods'] as List).whereType<Map>().toList();
    mods.removeWhere(
      (item) => (item['id'] as String?)?.toLowerCase() == modId.toLowerCase(),
    );
    state['mods'] = mods;
    await _saveManagerState(install, state);
    await _appendLauncherLogBestEffort('Uninstalled $modId.');
    return _loadInstalledMods(install);
  }
}
