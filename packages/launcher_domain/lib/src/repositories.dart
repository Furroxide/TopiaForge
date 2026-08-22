import 'dependency_planner.dart';
import 'models.dart';

abstract interface class LauncherRepository {
  String get dataRoot;

  /// Releases repository-owned processes, subscriptions, and stream
  /// controllers. Implementations must make repeated calls safe.
  Future<void> dispose();

  Future<LauncherSnapshot> loadSnapshot();

  /// Compatibility shim returning the highest-precedence discovered install.
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
    String sourceId = '',
  });

  Future<List<PackageSource>> savePackageSources(List<PackageSource> sources);

  /// Safely preflights, selects, installs, and consumes package-inbox files.
  /// Expected candidate failures are returned as structured issues and the
  /// rejected bytes remain available for inspection.
  Future<PackageInboxInstallOutcome> installInboxPackages(GameInstall install);

  /// Atomically reinstalls a damaged installed version from a matching
  /// integrity-pinned registry entry or verified package cache entry.
  Future<List<InstalledMod>> repairInstalledMod(
    GameInstall install,
    InstalledMod mod,
  );

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

  /// Writes one portable profile document to a user-selected path.
  Future<void> exportProfile(LauncherProfile profile, String path);

  /// Reads one portable profile document from a user-selected path.
  Future<LauncherProfile> importProfile(String path);

  /// Starts [install] with [profile] as a process-scoped snapshot. Profile mod
  /// enablement, version pins, safe mode, arguments, and environment must not
  /// be persisted into the manager's global state when launch fails or exits.
  Future<LaunchResult> launch(GameInstall install, LauncherProfile profile);

  /// Stops the matching game process, then follows the same process-scoped
  /// profile contract as [launch].
  Future<LaunchResult> restart(GameInstall install, LauncherProfile profile);

  Future<DiagnosticBundle> createDiagnosticBundle(
    GameInstall install,
    DependencyResolutionResult resolution,
  );

  Future<String> readRecentLog(GameInstall install, {int maxLines = 200});

  Future<void> openPath(String path);

  /// Opens the directory containing a filesystem item. Path interpretation
  /// belongs to the repository so Bloc and widgets remain IO-independent.
  Future<void> openContainingFolder(String path);

  Future<void> ensureDirectory(String path);

  /// Persists the opt-in developer mode flag (off by default; reveals the launcher's Developer tab).
  Future<void> setDeveloperMode(bool enabled);

  /// Persists launcher self-update settings such as automatic checks and release channel.
  Future<void> saveLauncherUpdateSettings(LauncherUpdateSettings settings);

}

abstract interface class LauncherUpdateRepository {
  Stream<LauncherUpdateStatus> get statuses;

  Future<LauncherUpdateStatus> checkForUpdate({
    required String currentVersion,
    required LauncherUpdateChannel channel,
    bool force = false,
  });

  Future<LauncherUpdateStatus> stageUpdate(LauncherUpdateCandidate candidate);

  Future<void> applyStagedUpdate(LauncherUpdateStatus staged);

  Future<void> recoverPendingUpdate();

  Future<void> dispose();
}

/// Optional launcher-repository capability for enumerating multiple installs.
///
/// Consumers must continue to support a plain [LauncherRepository] by falling
/// back to [LauncherRepository.detectKnownInstall].
abstract interface class GameInstallDiscoveryRepository
    implements LauncherRepository {
  Future<List<GameInstallCandidate>> discoverGameInstalls();
}

abstract interface class DeveloperRepository {
  String get developerDataRoot;

  Future<DeveloperWorkspace> loadDeveloperWorkspace({String? projectPath});

  Future<DeveloperWorkspace> createModProject({
    required String parentDirectory,
    required String id,
    required String name,
    bool includeUnityCompanion = false,
    ModScaffoldOptions options = const ModScaffoldOptions(),
  });

  /// Lists the scaffoldable mod templates (`templates/mod/<id>/template.json` in the repo). Always includes at
  /// least the built-in `minimal` template, so scaffolding works in synthetic environments without a repo.
  Future<List<ModTemplateInfo>> listModTemplates();

  /// Reads the project's `topiaforge.mod.json`. Throws when the project or manifest is missing.
  Future<ModManifest> readModManifest(String projectPath);

  /// Overwrites the project's `topiaforge.mod.json` with [manifest] and returns its validation issues.
  Future<List<LauncherIssue>> updateModManifest(
    String projectPath,
    ModManifest manifest,
  );



  Future<DeveloperWorkspace> resolveDeveloperProject(
    String projectPath, {
    bool restore = true,
    bool includePrerelease = false,
  });

  Future<DeveloperDoctorReport> runDoctor({String? projectPath});

  /// Audits the developer toolchain (.NET, Node, Unity, Git) with versions, severities, and remediation.
  /// Independent of any project, so it works for a fresh machine. Consuming mods requires none of these.
  Future<EnvironmentReport> checkEnvironment();

  /// Performs safe auto-fixes (ensures data folders) and returns
  /// the post-fix environment plus an action log. Never installs SDKs. Shared by the CLI `setup` command and the
  /// launcher's Developer tab so both behave identically.
  Future<DeveloperSetupResult> runSetup();

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


  /// Lists the tracked developer projects (VCC-style registry, persisted to `developer_projects.json`).
  Future<List<RegisteredProject>> listProjects();

  /// Adds an existing project directory to the registry, sniffing its kind (mod / unity-world / unity-package).
  /// Throws if the directory is not a recognized project. Returns the updated project list.
  Future<List<RegisteredProject>> addExistingProject(String path);

  /// Removes a project from the registry (untrack only — never deletes files). Returns the updated list.
  Future<List<RegisteredProject>> removeProject(String path);

  /// Creates a new Unity authoring project from the bundled template (copies it and registers it).
  /// The VCC-style "new project from template" flow. Returns the updated project list.
  Future<List<RegisteredProject>> createUnityProject({
    required String parentDirectory,
    required String name,
    String template = 'world',
  });

  /// Records that a project was just opened (updates its lastOpened timestamp). Returns the updated list.
  Future<List<RegisteredProject>> touchProjectOpened(String path);

  /// Detects installed Unity editors via Unity Hub (detect-only; never installs Unity).
  Future<List<UnityEditor>> listUnityEditors();

  /// Opens [projectPath] in the exact TopiaForge Unity authoring editor. Throws
  /// when the project pin or installed editor does not match.
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

  /// Reads the world-authoring pairing config (`topiaforge.world.json`) from a Unity project root, or null
  /// when the project has none.
  Future<WorldAuthoringConfig?> readWorldAuthoringConfig(
    String unityProjectPath,
  );

  /// Writes the world-authoring pairing config into a Unity project root. Returns what was written.
  Future<WorldAuthoringConfig> writeWorldAuthoringConfig(
    String unityProjectPath,
    WorldAuthoringConfig config,
  );

  /// Builds the paired world prefab into an AssetBundle by running the Unity editor headlessly
  /// (`-batchmode -executeMethod` against the world-companion package's builder) and verifies the bundle
  /// landed in the paired mod's `AssetBundles/` folder. [modPath]/[bundleName]/[unityExePath] override the
  /// config/auto-detected values. Never throws for build failures — inspect the result.
  Future<WorldBundleBuildResult> buildWorldBundle({
    required String unityProjectPath,
    String modPath = '',
    String bundleName = '',
    String unityExePath = '',
  });
}
