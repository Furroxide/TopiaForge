part of '../local_launcher_repository.dart';

extension _SnapshotLoading on LocalLauncherRepository {
  Future<LauncherSnapshot> _loadSnapshot() async {
    _ensureDataRoot();
    final profiles = await _loadProfiles();
    final settings = await _loadSettings();
    final selectedProfileId =
        (settings['selectedProfileId'] as String?) ?? profiles.first.id;
    final discovery = await _resolveGameInstallDiscovery(settings);
    final gameInstallCandidates = discovery.candidates;
    final gameInstall = discovery.install;
    var installedMods = <InstalledMod>[];
    if (gameInstall != null) {
      // A damaged record of server content is repaired first, failing
      // closed, then a join's install the launcher did not live to finish is
      // undone.
      await _repairServerContentWhenFree(gameInstall);
      await _sweepJoinInstallsWhenFree(gameInstall);
      try {
        installedMods = await _loadInstalledMods(gameInstall);
      } on _ManagerStateContentException {
        // Keep profiles and their explicit recovery previews available. Never
        // reconcile or replace manager state whose content cannot be trusted.
      }
    }
    final packageSources = await _loadPackageSources();
    final registryOutcome = await _loadRegistryOutcome(
      installedMods,
      packageSources,
    );
    final registryMods = registryOutcome.mods;
    final previews = <String, LaunchPreview>{};
    if (gameInstall != null) {
      for (final profile in profiles) {
        previews[profile.id] = await previewLaunch(gameInstall, profile);
      }
    }
    return LauncherSnapshot(
      gameInstall: gameInstall,
      gameInstallCandidates: gameInstallCandidates,
      profiles: profiles,
      selectedProfileId: selectedProfileId,
      installedMods: installedMods,
      registryMods: registryMods,
      packageSources: packageSources,
      previewsByProfile: previews,
      recentLog: gameInstall == null
          ? await _readLauncherLog()
          : await readRecentLog(gameInstall),
      launcherUpdates: LauncherUpdateSettings.fromJson(
        _objectMap(settings['launcherUpdates']),
      ),
      developerMode: (settings['developerMode'] as bool?) ?? false,
      sourceStatuses: registryOutcome.statuses,
      launcherLog: await _readLauncherLog(),
    );
  }
}
