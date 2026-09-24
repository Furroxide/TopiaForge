part of 'widget_test.dart';

class _FakeLauncherRepository extends _InstallFakeLauncherRepository {
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
             recentLog: '',
             launcherUpdates: const LauncherUpdateSettings(),
             developerMode: developerMode,
           );
  LauncherSnapshot _snapshot;
  final activityController = StreamController<LaunchActivity>.broadcast(
    sync: true,
  );
  final Map<String, LaunchPreview> previewOverrides = {};
  final Map<String, Completer<LaunchPreview>> previewGates = {};
  final launchedSelections = <LaunchSelection?>[];
  void Function(LauncherProfile)? onLaunch;
  Object? previewFailure;
  Object? saveFailure;
  @override
  Stream<LaunchActivity> get launchActivities => activityController.stream;
  @override
  Future<void> dispose() async {
    await activityController.close();
    await super.dispose();
  }

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
  void onGameInstallSelected(GameInstall install) {
    _snapshot = _replaceGameInstall(_snapshot, install);
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
    if (saveFailure != null) throw saveFailure!;
    savedProfiles = profiles.map((profile) {
      final previous = _snapshot.profiles
          .where((item) => item.id == profile.id)
          .firstOrNull;
      return previous != null &&
              previous.toJson().toString() == profile.toJson().toString()
          ? profile
          : profile.copyWith(revision: (previous?.revision ?? 0) + 1);
    }).toList();
    savedSelectedProfileId = selectedProfileId;
    _snapshot = _replaceSnapshotProfiles(
      _snapshot,
      savedProfiles,
      selectedProfileId,
    );
    return savedProfiles;
  }

  @override
  Future<LaunchPreview> previewLaunch(
    GameInstall install,
    LauncherProfile profile, {
    LaunchSelection? selectionOverride,
  }) async {
    if (previewFailure != null) throw previewFailure!;
    final gate = previewGates[profile.id];
    if (gate != null) return gate.future;
    final supplied = previewOverrides[profile.id];
    if (supplied != null) return supplied;
    return _buildFakePreview(_snapshot, install, profile, selectionOverride);
  }

  @override
  Future<void> exportProfile(LauncherProfile profile, String path) async {}

  @override
  Future<LauncherProfile> importProfile(String path) async =>
      importedProfile ?? LauncherProfile.defaultProfile();

  @override
  Future<LaunchResult> launch(
    GameInstall install,
    LauncherProfile profile, {
    LaunchSelection? selectionOverride,
  }) async {
    launchedProfileIds.add(profile.id);
    launchedProfiles.add(profile);
    launchedSelections.add(selectionOverride);
    onLaunch?.call(profile);
    return launchResult;
  }

  @override
  Future<LaunchResult> restart(
    GameInstall install,
    LauncherProfile profile, {
    LaunchSelection? selectionOverride,
  }) async {
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
}
