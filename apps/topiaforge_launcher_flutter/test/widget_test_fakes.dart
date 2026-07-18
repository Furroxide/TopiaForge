part of 'widget_test.dart';

class _FakeLauncherRepository extends _PublisherFakeLauncherRepository {
  _FakeLauncherRepository({
    LauncherSnapshot? snapshot,
    bool developerMode = false,
    this.packageInstallPlan,
    this.repairedSnapshot,
    this.inboxOutcome,
  }) : _snapshot =
           snapshot ??
           LauncherSnapshot(
             profiles: [LauncherProfile.defaultProfile()],
             selectedProfileId: 'default',
             installedMods: const [],
             registryMods: const [],
             packageSources: const [],
             worldCatalog: WorldCatalog.fallback(),
             recentLog: '',
             launcherUpdates: const LauncherUpdateSettings(enabled: false),
             developerMode: developerMode,
           );
  LauncherSnapshot _snapshot;
  final PackageInstallPlan? packageInstallPlan;
  final LauncherSnapshot? repairedSnapshot;
  final PackageInboxInstallOutcome? inboxOutcome;
  int installPackageCount = 0;
  String lastInstallSourceId = '';
  int repairInstalledModCount = 0;
  InstalledMod? repairRequest;
  int restartCount = 0;
  int installOrRepairRuntimeCount = 0;
  final launchedProfileIds = <String>[];
  final launchedProfiles = <LauncherProfile>[];
  List<LauncherProfile> savedProfiles = const [];
  String savedSelectedProfileId = '';
  LauncherProfile? importedProfile;
  LaunchResult launchResult = const LaunchResult(
    started: true,
    message: 'Launched TopiaForge.',
  );
  Completer<void>? loadGate;
  Completer<void>? loadEntered;
  int loadCount = 0;
  int activeLoads = 0;
  int maxConcurrentLoads = 0;
  @override
  String get dataRoot => '/tmp/topiaforge-launcher';
  @override
  Future<LauncherSnapshot> loadSnapshot() async {
    loadCount += 1;
    activeLoads += 1;
    if (activeLoads > maxConcurrentLoads) {
      maxConcurrentLoads = activeLoads;
    }
    final entered = loadEntered;
    if (entered != null && !entered.isCompleted) {
      entered.complete();
    }
    try {
      await loadGate?.future;
      return _snapshot;
    } finally {
      activeLoads -= 1;
    }
  }

  @override
  Future<GameInstall?> detectKnownInstall() async => null;
  @override
  Future<GameInstall> selectGameDirectory(String path) async {
    throw UnimplementedError();
  }

  @override
  Future<RepairReport> installOrRepairRuntime(GameInstall install) async {
    installOrRepairRuntimeCount += 1;
    final current = _snapshot.gameInstall;
    if (current != null) {
      _snapshot = _replaceGameInstall(
        _snapshot,
        GameInstall(
          path: current.path,
          executablePath: current.executablePath,
          bepInExStatus: ComponentState.ready,
          loaderStatus: ComponentState.ready,
          layout: current.layout,
          issues: current.issues,
          compatStatus: current.compatStatus,
        ),
      );
    }
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
    return packageInstallPlan ?? (throw UnimplementedError());
  }

  @override
  Future<List<InstalledMod>> installPackage(
    String packagePath,
    GameInstall install, {
    String expectedSha256 = '',
    String sourceId = '',
  }) async {
    installPackageCount += 1;
    lastInstallSourceId = sourceId;
    return _snapshot.installedMods;
  }

  @override
  Future<List<PackageSource>> savePackageSources(
    List<PackageSource> sources,
  ) async {
    return sources;
  }

  @override
  Future<PackageInboxInstallOutcome> installInboxPackages(
    GameInstall install,
  ) async => inboxOutcome ?? (throw UnimplementedError());

  @override
  Future<List<InstalledMod>> repairInstalledMod(
    GameInstall install,
    InstalledMod mod,
  ) async {
    repairInstalledModCount += 1;
    repairRequest = mod;
    final repaired = repairedSnapshot;
    if (repaired != null) {
      _snapshot = repaired;
    }
    return _snapshot.installedMods;
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
    savedProfiles = profiles;
    savedSelectedProfileId = selectedProfileId;
    return profiles;
  }

  @override
  Future<void> exportProfile(LauncherProfile profile, String path) async {}

  @override
  Future<LauncherProfile> importProfile(String path) async =>
      importedProfile ?? LauncherProfile.defaultProfile();

  @override
  Future<LaunchResult> launch(
    GameInstall install,
    LauncherProfile profile,
  ) async {
    launchedProfileIds.add(profile.id);
    launchedProfiles.add(profile);
    return launchResult;
  }

  @override
  Future<LaunchResult> restart(
    GameInstall install,
    LauncherProfile profile,
  ) async {
    restartCount += 1;
    return const LaunchResult(started: true, message: 'Restarted TopiaForge.');
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
  Future<void> openContainingFolder(String path) async {}
  @override
  Future<void> ensureDirectory(String path) async {}
  @override
  Future<void> setDeveloperMode(bool enabled) async {}

  @override
  Future<void> saveLauncherUpdateSettings(
    LauncherUpdateSettings settings,
  ) async {}

  @override
  Future<UgcLiveSyncStatusSnapshot?> readUgcLiveSyncStatus(
    GameInstall install,
  ) async {
    return null;
  }

  @override
  Future<UgcSceneInspectionResult> inspectWatchFolderScenes(
    String watchFolder,
  ) async => UgcSceneInspectionResult();
}

class _FakeDeveloperRepository implements DeveloperRepository {
  _FakeDeveloperRepository({
    this.initialUgcSettings = const UgcLiveSyncSettings(
      editorUrl: 'https://editor/?project=automerge:stale',
      documentUrl: 'automerge:stale',
      autoConnectOnStart: true,
    ),
  }) : _currentUgcSettings = initialUgcSettings;

  bool hasProject = true;
  final UgcLiveSyncSettings initialUgcSettings;
  UgcLiveSyncSettings _currentUgcSettings;
  UgcLiveSyncSettings? updatedUgcSettings;
  Completer<void>? updateUgcGate;
  Completer<void>? updateUgcEntered;

  @override
  String get developerDataRoot => '/tmp/topiaforge-developer';
  @override
  Future<DeveloperWorkspace> loadDeveloperWorkspace({String? projectPath}) {
    return Future.value(_workspace(_currentUgcSettings));
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
        schemaVersion: 4,
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
      '$projectPath/ProjectSettings/TopiaForgeUgcCompanion.json',
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
  ) async {
    final entered = updateUgcEntered;
    if (entered != null && !entered.isCompleted) {
      entered.complete();
    }
    await updateUgcGate?.future;
    updatedUgcSettings = settings;
    _currentUgcSettings = settings;
    return _workspace(settings).project!;
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
}
