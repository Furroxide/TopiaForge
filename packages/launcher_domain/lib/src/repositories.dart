import 'dependency_planner.dart';
import 'models.dart';

abstract interface class LauncherRepository {
  String get dataRoot;

  Future<LauncherSnapshot> loadSnapshot();

  Future<GameInstall?> detectKnownInstall();

  Future<GameInstall> selectGameDirectory(String path);

  Future<RepairReport> installOrRepairRuntime(GameInstall install);

  /// Re-runs the game-compatibility check (via the bundled GameCompat.Extractor) and returns an informational
  /// status with per-mod findings. WARN-ONLY: the result never blocks launching. Degrades to an `unknown` status
  /// if the extractor tool is unavailable.
  Future<GameCompatStatus> checkGameCompat(GameInstall install);

  Future<PackageInstallPlan> previewPackage(
    String packagePath,
    GameInstall install, {
    String expectedSha256 = '',
    String sourceId = '',
    String sourceName = '',
  });

  Future<List<InstalledMod>> installPackage(
    String packagePath,
    GameInstall install, {
    String expectedSha256 = '',
  });

  Future<List<PackageSource>> savePackageSources(List<PackageSource> sources);

  Future<List<InstalledMod>> installInboxPackages(GameInstall install);

  Future<List<InstalledMod>> setModEnabled(
    GameInstall install,
    String modId,
    bool enabled,
  );

  Future<List<InstalledMod>> disableAllMods(GameInstall install);

  Future<List<InstalledMod>> uninstallMod(GameInstall install, String modId);

  Future<List<LauncherProfile>> saveProfiles(
    List<LauncherProfile> profiles,
    String selectedProfileId,
  );

  Future<LaunchResult> launch(GameInstall install, LauncherProfile profile);

  Future<LaunchResult> restart(GameInstall install, LauncherProfile profile);

  Future<List<LegacyMod>> detectLegacyMods(GameInstall install);

  Future<DiagnosticBundle> createDiagnosticBundle(
    GameInstall install,
    DependencyResolutionResult resolution,
  );

  Future<String> readRecentLog(GameInstall install, {int maxLines = 200});

  Future<void> openPath(String path);

  /// Persists the opt-in developer mode flag (off by default; reveals the launcher's Developer tab).
  Future<void> setDeveloperMode(bool enabled);

  /// Writes the UGC live-sync runtime config (`config/robotopia.ugc.livesync.json`) into the install so the
  /// `Robotopia.UgcLiveSync` mod picks it up on next launch. Returns the written file path.
  Future<String> deployUgcLiveSyncConfig(
    GameInstall install,
    UgcLiveSyncSettings settings,
  );

  /// Reads the UGC live-sync status handshake the mod writes (`config/robotopia.ugc.livesync.status.json`) so the
  /// cockpit can auto-detect the game's default watch folder and show live state. Null when absent/unreadable.
  Future<UgcLiveSyncStatusSnapshot?> readUgcLiveSyncStatus(GameInstall install);

  /// Parses the newest exported project file (`*.json`/`*.json.gz`) in [watchFolder] and returns its scenes, so
  /// the cockpit can offer a scene dropdown instead of a free-text field. Empty when the folder has no snapshot.
  Future<List<UgcSceneRef>> listWatchFolderScenes(String watchFolder);
}

abstract interface class DeveloperRepository {
  String get developerDataRoot;

  Future<DeveloperWorkspace> loadDeveloperWorkspace({String? projectPath});

  Future<DeveloperWorkspace> createModProject({
    required String parentDirectory,
    required String id,
    required String name,
    bool includeUnityCompanion = false,
  });

  Future<DeveloperWorkspace> resolveDeveloperProject(
    String projectPath, {
    bool restore = true,
    bool includePrerelease = false,
  });

  Future<DeveloperDoctorReport> runDoctor({String? projectPath});

  /// Audits the developer toolchain (.NET, Node, Unity, Git) with versions, severities, and remediation.
  /// Independent of any project, so it works for a fresh machine. Consuming mods requires none of these.
  Future<EnvironmentReport> checkEnvironment();

  /// Performs safe auto-fixes (installs the UGC Automerge sidecar's npm deps, ensures data folders) and returns
  /// the post-fix environment plus an action log. Never installs SDKs. Shared by the CLI `setup` command and the
  /// launcher's Developer tab so both behave identically.
  Future<DeveloperSetupResult> runSetup();

  Future<LegacyMigrationResult> migrateLegacyMods(
    String gamePath,
    String outputRoot,
  );

  Future<ModManifest> checkPackage(String packagePath);

  Future<DeveloperProject> addProjectPackageSource(
    String projectPath,
    PackageSource source,
  );

  Future<DeveloperProject> addProjectDependency(
    String projectPath,
    ModDependency dependency,
  );

  Future<DeveloperProject> removeProjectDependency(
    String projectPath,
    String dependencyId,
  );

  Future<String> packProject(
    String projectPath, {
    String outputDir = '',
    String configuration = 'Release',
  });

  /// Persists UGC live-sync settings into the project's `robotopia.project.json` (under `unityCompanion`).
  Future<DeveloperProject> updateUgcLiveSync(
    String projectPath,
    UgcLiveSyncSettings settings,
  );

  /// Lists the tracked developer projects (VCC-style registry, persisted to `developer_projects.json`).
  Future<List<RegisteredProject>> listProjects();

  /// Adds an existing project directory to the registry, sniffing its kind (mod / unity-world / unity-package).
  /// Throws if the directory is not a recognized project. Returns the updated project list.
  Future<List<RegisteredProject>> addExistingProject(String path);

  /// Removes a project from the registry (untrack only — never deletes files). Returns the updated list.
  Future<List<RegisteredProject>> removeProject(String path);

  /// Creates a new Unity authoring project from the bundled template (copies it, installs the UGC companion
  /// package, registers it). The VCC-style "new project from template" flow. Returns the updated project list.
  Future<List<RegisteredProject>> createUnityProject({
    required String parentDirectory,
    required String name,
    String template = 'world',
  });

  /// Records that a project was just opened (updates its lastOpened timestamp). Returns the updated list.
  Future<List<RegisteredProject>> touchProjectOpened(String path);

  /// Detects installed Unity editors via Unity Hub (detect-only; never installs Unity).
  Future<List<UnityEditor>> listUnityEditors();

  /// Opens [projectPath] in the Unity editor matching its `ProjectSettings/ProjectVersion.txt` (or the newest
  /// installed editor when no exact match). Returns the launched editor path. Throws when no editor is found.
  Future<String> openProjectInUnity(String projectPath);

  /// Resolves a Unity project's `Packages/vpm-manifest.json` against the subscribed VPM listings; when [restore]
  /// is true, downloads + installs the resolved packages into `Packages/` and writes the locked versions back.
  /// Throws on blocking resolution issues (missing/unsatisfiable packages). Returns the resolved packages.
  Future<List<VpmResolvedPackage>> resolveUnityProject(
    String projectPath, {
    bool restore = true,
  });

  /// Adds (or updates) a VPM dependency in the project's manifest, then resolves + restores. Returns the result.
  Future<List<VpmResolvedPackage>> addUnityPackage(
    String projectPath,
    String id,
    String versionRange,
  );

  /// Removes a VPM dependency (manifest + lock + installed folder). Returns the re-resolved packages.
  Future<List<VpmResolvedPackage>> removeUnityPackage(
    String projectPath,
    String id,
  );

  /// The latest version of every package available across the subscribed VPM listings.
  Future<List<VpmPackageInfo>> listAvailableUnityPackages();

  /// The subscribed VPM repositories (the launcher's package listings).
  Future<List<PackageSource>> listUnityRepos();

  /// Subscribes to a VPM repository (a listing `index.json` location). Returns the updated repo list.
  Future<List<PackageSource>> addUnityRepo(String url, {String name});

  /// Unsubscribes from a VPM repository by id (built-in repos cannot be removed). Returns the updated list.
  Future<List<PackageSource>> removeUnityRepo(String id);

  /// Scaffolds a new VPM Unity package from the bundled package template (the package-maker). Returns its path.
  Future<String> createUnityPackage({
    required String parentDirectory,
    required String id,
    String name,
  });
}
