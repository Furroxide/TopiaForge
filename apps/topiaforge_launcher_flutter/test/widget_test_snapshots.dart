part of 'widget_test.dart';

LauncherSnapshot _replaceGameInstall(
  LauncherSnapshot snapshot,
  GameInstall? install,
) {
  return LauncherSnapshot(
    gameInstall: install,
    profiles: snapshot.profiles,
    selectedProfileId: snapshot.selectedProfileId,
    installedMods: snapshot.installedMods,
    registryMods: snapshot.registryMods,
    packageSources: snapshot.packageSources,
    worldCatalog: snapshot.worldCatalog,
    recentLog: snapshot.recentLog,
    launcherUpdates: snapshot.launcherUpdates,
    developerMode: snapshot.developerMode,
    sourceStatuses: snapshot.sourceStatuses,
    launcherLog: snapshot.launcherLog,
  );
}

/// A detected, launchable install. [needsRepair] flips the loader to missing
/// so Home renders its "Almost ready" state.
LauncherSnapshot _readySnapshot({
  bool needsRepair = false,
  List<RegistryMod> registryMods = const [],
  List<LauncherProfile>? profiles,
  String selectedProfileId = 'default',
}) {
  return LauncherSnapshot(
    gameInstall: GameInstall(
      path: 'C:\\Games\\Robotopia',
      executablePath: 'C:\\Games\\Robotopia\\Robotopia.exe',
      bepInExStatus: ComponentState.ready,
      loaderStatus: needsRepair ? ComponentState.missing : ComponentState.ready,
    ),
    profiles: profiles ?? [LauncherProfile.defaultProfile()],
    selectedProfileId: selectedProfileId,
    installedMods: const [],
    registryMods: registryMods,
    packageSources: const [],
    worldCatalog: WorldCatalog.fallback(),
    recentLog: '',
    launcherUpdates: const LauncherUpdateSettings(enabled: false),
  );
}

LauncherSnapshot _updateSnapshot({
  bool needsRepair = false,
  List<String> modErrors = const [],
  List<InstalledModVersionStatus> installedVersions = const [],
}) {
  final installedManifest = _manifest('timer.mod', version: '1.0.0');
  final registryManifest = _manifest('timer.mod', version: '1.1.0');
  return LauncherSnapshot(
    gameInstall: GameInstall(
      path: 'C:\\Games\\Robotopia',
      executablePath: 'C:\\Games\\Robotopia\\Robotopia.exe',
      bepInExStatus: ComponentState.ready,
      loaderStatus: needsRepair ? ComponentState.missing : ComponentState.ready,
    ),
    profiles: [LauncherProfile.defaultProfile()],
    selectedProfileId: 'default',
    installedMods: [
      InstalledMod(
        id: installedManifest.id,
        name: installedManifest.name,
        version: installedManifest.version,
        enabled: true,
        restartRequired: true,
        uninstallPending: false,
        packagePath: 'C:\\Games\\Robotopia\\BepInEx\\TopiaForge',
        manifest: installedManifest,
        errors: modErrors,
        installedVersions: installedVersions,
      ),
    ],
    registryMods: [
      RegistryMod(
        manifest: registryManifest,
        downloadUrl: Uri.file(
          'C:\\packages\\timer-1.1.0.topiaforgemod',
          windows: true,
        ).toString(),
        installedVersion: installedManifest.version,
      ),
    ],
    packageSources: const [],
    worldCatalog: WorldCatalog.fallback(),
    recentLog: '',
    launcherUpdates: const LauncherUpdateSettings(enabled: false),
  );
}

ModManifest _manifest(String id, {required String version}) {
  return ModManifest(
    schemaVersion: 4,
    id: id,
    name: 'Timer Mod',
    version: version,
    author: const ModAuthor(name: 'TopiaForge'),
    entryAssembly: 'Timer.dll',
    entryType: 'Timer.Entry',
  );
}

LauncherSnapshot _discoverySnapshot({bool developerMode = false}) {
  return LauncherSnapshot(
    gameInstall: const GameInstall(
      path: 'C:\\Games\\Robotopia',
      executablePath: 'C:\\Games\\Robotopia\\Robotopia.exe',
      bepInExStatus: ComponentState.ready,
      loaderStatus: ComponentState.ready,
    ),
    profiles: [LauncherProfile.defaultProfile()],
    selectedProfileId: 'default',
    installedMods: const [],
    registryMods: [
      _registryMod('framework.mod', 'Framework Mod', 'Framework'),
      _registryMod('gameplay.mod', 'Gameplay Mod', 'Gameplay'),
    ],
    packageSources: const [],
    worldCatalog: WorldCatalog.fallback(),
    recentLog: '',
    launcherUpdates: const LauncherUpdateSettings(enabled: false),
    developerMode: developerMode,
  );
}

RegistryMod _registryMod(String id, String name, String category) {
  return RegistryMod(
    manifest: ModManifest(
      schemaVersion: 4,
      id: id,
      name: name,
      version: '1.0.0',
      author: const ModAuthor(name: 'TopiaForge'),
      entryAssembly: '$id.dll',
      entryType: '$id.Entry',
      category: category,
    ),
  );
}
