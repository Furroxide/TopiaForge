part of 'widget_test.dart';

class _FakeLauncherRepository implements LauncherRepository {
  _FakeLauncherRepository({
    LauncherSnapshot? snapshot,
    bool developerMode = false,
  }) : _snapshot =
           snapshot ??
           LauncherSnapshot(
             profiles: [LauncherProfile.defaultProfile()],
             selectedProfileId: 'default',
             installedMods: const [],
             registryMods: const [],
             packageSources: const [],
             worldCatalog: WorldCatalog.fallback(),
             legacyMods: const [],
             recentLog: '',
             launcherUpdates: const LauncherUpdateSettings(enabled: false),
             developerMode: developerMode,
           );
  final LauncherSnapshot _snapshot;
  int restartCount = 0;
  int installOrRepairRuntimeCount = 0;
  final launchedProfileIds = <String>[];
  @override
  String get dataRoot => '/tmp/robotopia-launcher';
  @override
  Future<LauncherSnapshot> loadSnapshot() async => _snapshot;
  @override
  Future<GameInstall?> detectKnownInstall() async => null;
  @override
  Future<GameInstall> selectGameDirectory(String path) async {
    throw UnimplementedError();
  }

  @override
  Future<RepairReport> installOrRepairRuntime(GameInstall install) async {
    installOrRepairRuntimeCount += 1;
    return const RepairReport(actions: ['Runtime repaired.'], issues: []);
  }

  @override
  Future<GameCompatStatus> checkGameCompat(GameInstall install) async =>
      GameCompatStatus.skipped();
  @override
  Future<PackageInstallPlan> previewPackage(
    String packagePath,
    GameInstall install, {
    String expectedSha256 = '',
    String sourceId = '',
    String sourceName = '',
  }) async {
    throw UnimplementedError();
  }

  @override
  Future<List<InstalledMod>> installPackage(
    String packagePath,
    GameInstall install, {
    String expectedSha256 = '',
  }) async {
    throw UnimplementedError();
  }

  @override
  Future<List<PackageSource>> savePackageSources(
    List<PackageSource> sources,
  ) async {
    return sources;
  }

  @override
  Future<List<InstalledMod>> installInboxPackages(GameInstall install) async {
    throw UnimplementedError();
  }

  @override
  Future<List<InstalledMod>> setModEnabled(
    GameInstall install,
    String modId,
    bool enabled,
  ) async {
    throw UnimplementedError();
  }

  @override
  Future<List<InstalledMod>> disableAllMods(GameInstall install) async {
    throw UnimplementedError();
  }

  @override
  Future<List<InstalledMod>> uninstallMod(
    GameInstall install,
    String modId,
  ) async {
    throw UnimplementedError();
  }

  @override
  Future<List<LauncherProfile>> saveProfiles(
    List<LauncherProfile> profiles,
    String selectedProfileId,
  ) async {
    return profiles;
  }

  @override
  Future<LaunchResult> launch(
    GameInstall install,
    LauncherProfile profile,
  ) async {
    launchedProfileIds.add(profile.id);
    return const LaunchResult(started: true, message: 'Launched Robotopia.');
  }

  @override
  Future<LaunchResult> restart(
    GameInstall install,
    LauncherProfile profile,
  ) async {
    restartCount += 1;
    return const LaunchResult(started: true, message: 'Restarted Robotopia.');
  }

  @override
  Future<List<LegacyMod>> detectLegacyMods(GameInstall install) async {
    return const [];
  }

  @override
  Future<DiagnosticBundle> createDiagnosticBundle(
    GameInstall install,
    DependencyResolutionResult resolution,
  ) async {
    throw UnimplementedError();
  }

  @override
  Future<String> readRecentLog(
    GameInstall install, {
    int maxLines = 200,
  }) async {
    return '';
  }

  @override
  Future<void> openPath(String path) async {}
  @override
  Future<void> setDeveloperMode(bool enabled) async {}

  @override
  Future<void> saveLauncherUpdateSettings(
    LauncherUpdateSettings settings,
  ) async {}

  @override
  Future<String> deployUgcLiveSyncConfig(
    GameInstall install,
    UgcLiveSyncSettings settings,
  ) async {
    return '/tmp/robotopia.ugc.livesync.json';
  }

  @override
  Future<UgcLiveSyncStatusSnapshot?> readUgcLiveSyncStatus(
    GameInstall install,
  ) async {
    return null;
  }

  @override
  Future<List<UgcSceneRef>> listWatchFolderScenes(String watchFolder) async {
    return const [];
  }
}

class _FakeDeveloperRepository implements DeveloperRepository {
  @override
  String get developerDataRoot => '/tmp/robotopia-developer';
  @override
  Future<DeveloperWorkspace> loadDeveloperWorkspace({String? projectPath}) {
    return Future.value(_workspace());
  }

  @override
  Future<DeveloperWorkspace> createModProject({
    required String parentDirectory,
    required String id,
    required String name,
    bool includeUnityCompanion = false,
    ModScaffoldOptions options = const ModScaffoldOptions(),
  }) {
    return Future.value(_workspace());
  }

  @override
  Future<List<ModTemplateInfo>> listModTemplates() {
    return Future.value(const [ModTemplateInfo(id: 'minimal')]);
  }

  @override
  Future<ModManifest> readModManifest(String projectPath) {
    return Future.value(
      const ModManifest(
        schemaVersion: 2,
        id: 'sample.mod',
        name: 'Sample Mod',
        version: '0.1.0',
      ),
    );
  }

  @override
  Future<List<LauncherIssue>> updateModManifest(
    String projectPath,
    ModManifest manifest,
  ) {
    return Future.value(const <LauncherIssue>[]);
  }

  @override
  Future<bool> ensureUgcCompanionPackage(
    String projectPath, {
    bool update = false,
  }) {
    return Future.value(true);
  }

  @override
  Future<String> writeUgcCompanionSeed(
    String projectPath, {
    required String watchFolder,
    String projectName = '',
    String sceneId = '',
    String sceneName = '',
    String environment = '',
    bool liveSync = true,
  }) {
    return Future.value(
      '$projectPath/ProjectSettings/RobotopiaUgcCompanion.json',
    );
  }

  @override
  Future<DeveloperWorkspace> resolveDeveloperProject(
    String projectPath, {
    bool restore = true,
    bool includePrerelease = false,
  }) {
    return Future.value(_workspace());
  }

  @override
  Future<DeveloperDoctorReport> runDoctor({String? projectPath}) {
    return Future.value(
      const DeveloperDoctorReport(
        projectRoot: '/tmp/creator',
        messages: ['Developer project found.'],
      ),
    );
  }

  int runSetupCount = 0;
  @override
  Future<EnvironmentReport> checkEnvironment() {
    return Future.value(
      const EnvironmentReport(
        checks: [
          ToolCheck(
            name: '.NET SDK',
            status: ToolStatus.ok,
            purpose: ToolPurpose.develop,
            detail: 'v8.0.100',
          ),
        ],
      ),
    );
  }

  @override
  Future<DeveloperSetupResult> runSetup() async {
    runSetupCount += 1;
    return DeveloperSetupResult(
      environment: await checkEnvironment(),
      actions: const ['UGC Automerge sidecar dependencies already present.'],
    );
  }

  @override
  Future<LegacyMigrationResult> migrateLegacyMods(
    String gamePath,
    String outputRoot,
  ) {
    throw UnimplementedError();
  }

  @override
  Future<ModManifest> checkPackage(String packagePath) {
    throw UnimplementedError();
  }

  @override
  Future<DeveloperProject> addProjectPackageSource(
    String projectPath,
    PackageSource source,
  ) {
    throw UnimplementedError();
  }

  @override
  Future<DeveloperProject> addProjectDependency(
    String projectPath,
    ModDependency dependency,
  ) {
    throw UnimplementedError();
  }

  @override
  Future<DeveloperProject> removeProjectDependency(
    String projectPath,
    String dependencyId,
  ) {
    throw UnimplementedError();
  }

  @override
  Future<String> packProject(
    String projectPath, {
    String outputDir = '',
    String configuration = 'Release',
  }) {
    throw UnimplementedError();
  }

  @override
  Future<DeveloperProject> updateUgcLiveSync(
    String projectPath,
    UgcLiveSyncSettings settings,
  ) {
    throw UnimplementedError();
  }

  @override
  Future<List<RegisteredProject>> listProjects() async => const [];
  @override
  Future<List<RegisteredProject>> addExistingProject(String path) async =>
      const [];
  @override
  Future<List<RegisteredProject>> removeProject(String path) async => const [];
  @override
  Future<List<RegisteredProject>> createUnityProject({
    required String parentDirectory,
    required String name,
    String template = 'world',
  }) async => const [];
  @override
  Future<List<RegisteredProject>> touchProjectOpened(String path) async =>
      const [];
  @override
  Future<List<UnityEditor>> listUnityEditors() async => const [];
  @override
  Future<String> openProjectInUnity(String projectPath) async => '';
  @override
  Future<List<VpmResolvedPackage>> resolveUnityProject(
    String projectPath, {
    bool restore = true,
  }) async => const [];
  @override
  Future<List<VpmResolvedPackage>> addUnityPackage(
    String projectPath,
    String id,
    String versionRange,
  ) async => const [];
  @override
  Future<List<VpmResolvedPackage>> removeUnityPackage(
    String projectPath,
    String id,
  ) async => const [];
  @override
  Future<List<VpmPackageInfo>> listAvailableUnityPackages() async => const [];
  @override
  Future<List<PackageSource>> listUnityRepos() async => const [];
  @override
  Future<List<PackageSource>> addUnityRepo(
    String url, {
    String name = '',
  }) async => const [];
  @override
  Future<List<PackageSource>> removeUnityRepo(String id) async => const [];
  @override
  Future<String> createUnityPackage({
    required String parentDirectory,
    required String id,
    String name = '',
  }) async => '';
  @override
  Future<WorldAuthoringConfig?> readWorldAuthoringConfig(
    String unityProjectPath,
  ) async => null;
  @override
  Future<WorldAuthoringConfig> writeWorldAuthoringConfig(
    String unityProjectPath,
    WorldAuthoringConfig config,
  ) async => config;
  @override
  Future<WorldBundleBuildResult> buildWorldBundle({
    required String unityProjectPath,
    String modPath = '',
    String bundleName = '',
    String unityExePath = '',
  }) async => const WorldBundleBuildResult(success: false);
  DeveloperWorkspace _workspace() {
    return const DeveloperWorkspace(
      projectRoot: '/tmp/creator',
      generatedPropsPath: '/tmp/creator/robotopia.dev.props',
      project: DeveloperProject(
        schemaVersion: 1,
        id: 'creator.mod',
        name: 'Creator Mod',
      ),
      lock: DeveloperLock(
        schemaVersion: 1,
        projectId: 'creator.mod',
        resolvedAtUtc: '2026-06-29T00:00:00Z',
        packages: [
          LockedPackage(
            id: 'api.mod',
            name: 'API Mod',
            version: '1.0.0',
            packageUrl: 'file:///api.robotopiamod',
            packageSha256: 'sha',
            apiAssemblies: ['ref/Api.dll'],
          ),
        ],
      ),
    );
  }
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
      loaderStatus: needsRepair
          ? ComponentState.missing
          : ComponentState.ready,
    ),
    profiles: profiles ?? [LauncherProfile.defaultProfile()],
    selectedProfileId: selectedProfileId,
    installedMods: const [],
    registryMods: registryMods,
    packageSources: const [],
    worldCatalog: WorldCatalog.fallback(),
    legacyMods: const [],
    recentLog: '',
    launcherUpdates: const LauncherUpdateSettings(enabled: false),
  );
}

LauncherSnapshot _updateSnapshot() {
  final installedManifest = _manifest('timer.mod', version: '1.0.0');
  final registryManifest = _manifest('timer.mod', version: '1.1.0');
  return LauncherSnapshot(
    gameInstall: const GameInstall(
      path: 'C:\\Games\\Robotopia',
      executablePath: 'C:\\Games\\Robotopia\\Robotopia.exe',
      bepInExStatus: ComponentState.ready,
      loaderStatus: ComponentState.ready,
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
        packagePath: 'C:\\Games\\Robotopia\\BepInEx\\RobotopiaModManager',
        manifest: installedManifest,
      ),
    ],
    registryMods: [
      RegistryMod(
        manifest: registryManifest,
        downloadUrl: Uri.file(
          'C:\\packages\\timer-1.1.0.robotopiamod',
          windows: true,
        ).toString(),
        installedVersion: installedManifest.version,
      ),
    ],
    packageSources: const [],
    worldCatalog: WorldCatalog.fallback(),
    legacyMods: const [],
    recentLog: '',
    launcherUpdates: const LauncherUpdateSettings(enabled: false),
  );
}

ModManifest _manifest(String id, {required String version}) {
  return ModManifest(
    schemaVersion: 2,
    id: id,
    name: 'Timer Mod',
    version: version,
    author: const ModAuthor(name: 'QuantumWorks'),
    entryAssembly: 'Timer.dll',
    entryType: 'Timer.Entry',
  );
}
