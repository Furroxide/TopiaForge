import 'package:launcher_domain/launcher_domain.dart';

import 'launcher_section.dart';

class LauncherState {
  const LauncherState({
    required this.section,
    required this.isBusy,
    required this.statusMessage,
    this.statusSeverity = IssueSeverity.info,
    required this.profiles,
    required this.selectedProfileId,
    required this.installedMods,
    required this.registryMods,
    required this.packageSources,
    required this.sourceStatuses,
    required this.worldCatalog,
    required this.recentLog,
    required this.launcherLog,
    required this.resolution,
    required this.profileResolution,
    required this.launcherUpdates,
    this.launcherUpdateStatus = const LauncherUpdateStatus(),
    this.gameInstall,
    this.gameInstallCandidates = const [],
    this.selectedModId,
    this.modSearch = '',
    this.errorMessage,
    this.previewedPackagePath,
    this.previewedPackageSha256 = '',
    this.installPlan,
    this.diagnosticBundle,
    this.developerWorkspace,
    this.developerDoctor,
    this.developerMode = false,
    this.developerEnvironment,
    this.developerSetup,
    this.developerProjects = const [],
    this.unityEditors = const [],
    this.managedProject,
    this.unityResolved = const [],
    this.unityAvailable = const [],
    this.unityRepos = const [],
  });

  factory LauncherState.initial() => LauncherState(
    section: LauncherSection.home,
    isBusy: true,
    statusMessage: 'Loading launcher state.',
    profiles: const [],
    selectedProfileId: 'default',
    installedMods: const [],
    registryMods: const [],
    packageSources: const [],
    sourceStatuses: const [],
    worldCatalog: WorldCatalog.fallback(),
    recentLog: '',
    launcherLog: '',
    resolution: const DependencyResolutionResult(
      orderedMods: [],
      issues: [],
      graph: {},
    ),
    profileResolution: const DependencyResolutionResult(
      orderedMods: [],
      issues: [],
      graph: {},
    ),
    launcherUpdates: const LauncherUpdateSettings(),
  );

  final LauncherSection section;
  final bool isBusy;
  final String statusMessage;
  final IssueSeverity statusSeverity;
  final GameInstall? gameInstall;
  final List<GameInstallCandidate> gameInstallCandidates;
  final List<LauncherProfile> profiles;
  final String selectedProfileId;
  final List<InstalledMod> installedMods;
  final List<RegistryMod> registryMods;
  final List<PackageSource> packageSources;
  final List<PackageSourceStatus> sourceStatuses;
  final WorldCatalog worldCatalog;
  final String recentLog;
  final String launcherLog;
  final DependencyResolutionResult resolution;

  /// Resolution over only what the selected profile will actually load.
  ///
  /// [resolution] covers every installed mod, which is what the Mods screen shows. Launch is gated on
  /// this narrower result instead, so a profile that already excludes a conflicting mod is not blocked
  /// by a conflict it will never load. It mirrors the repository's own pre-launch check, so the button
  /// and the launch attempt agree rather than disagreeing one screen apart.
  final DependencyResolutionResult profileResolution;
  final LauncherUpdateSettings launcherUpdates;
  final LauncherUpdateStatus launcherUpdateStatus;
  final String? selectedModId;
  final String modSearch;
  final String? errorMessage;
  final String? previewedPackagePath;
  final String previewedPackageSha256;
  final PackageInstallPlan? installPlan;
  final DiagnosticBundle? diagnosticBundle;
  final DeveloperWorkspace? developerWorkspace;
  final DeveloperDoctorReport? developerDoctor;

  /// Opt-in developer mode (off by default). Controls whether the Developer tab is shown.
  final bool developerMode;

  /// Last developer-toolchain audit (.NET/Node/Unity/Git), shown in the Dev tab's Environment pane.
  final EnvironmentReport? developerEnvironment;

  /// Last setup/auto-fix result (action log), shown after running Setup in the Dev tab.
  final DeveloperSetupResult? developerSetup;

  /// The VCC-style tracked developer projects (mod + Unity), shown in the Dev tab's Projects list.
  final List<RegisteredProject> developerProjects;

  /// Installed Unity editors detected via Unity Hub (for "Open in Unity").
  final List<UnityEditor> unityEditors;

  /// The project currently being "managed" via the Projects list (drives which per-project panes render).
  final RegisteredProject? managedProject;

  /// The managed Unity project's resolved VPM packages (installed/locked), shown in the Packages pane.
  final List<VpmResolvedPackage> unityResolved;

  /// Packages available across the subscribed VPM listings (to add to the managed Unity project).
  final List<VpmPackageInfo> unityAvailable;

  /// The subscribed VPM repositories (package listings).
  final List<PackageSource> unityRepos;

  LauncherProfile? get selectedProfile {
    for (final profile in profiles) {
      if (profile.id == selectedProfileId) {
        return profile;
      }
    }
    return profiles.isEmpty ? null : profiles.first;
  }

  InstalledMod? get selectedMod {
    if (installedMods.isEmpty) {
      return null;
    }
    for (final mod in installedMods) {
      if (mod.id == selectedModId) {
        return mod;
      }
    }
    return installedMods.first;
  }

  /// Blocking issues among the mods the selected profile will load.
  ///
  /// Safe mode bypasses every mod, so nothing can block it.
  List<LauncherIssue> get blockingLaunchIssues {
    if (selectedProfile?.launchSettings.safeMode ?? false) {
      return const [];
    }
    return profileResolution.issues
        .where((issue) => issue.isBlocking)
        .toList(growable: false);
  }

  bool get canLaunch {
    return gameInstall != null &&
        gameInstall!.canLaunch &&
        !gameInstall!.needsRepair &&
        selectedProfile != null &&
        blockingLaunchIssues.isEmpty;
  }

  /// Whether a launch attempt is worth starting.
  ///
  /// Deliberately ignores [GameInstall.needsRepair], because launching repairs a stale runtime on the
  /// way. A blocking mod issue is different: the repository refuses the launch outright, so leaving the
  /// button live only turns a visible problem into a one-line failure after the click.
  bool get canStartLaunchFlow {
    return gameInstall != null &&
        gameInstall!.canLaunch &&
        selectedProfile != null &&
        blockingLaunchIssues.isEmpty;
  }

  int get availableModUpdateCount {
    return registryMods.where((mod) => mod.updateAvailable).length;
  }

  List<InstalledMod> get filteredMods {
    final query = modSearch.trim().toLowerCase();
    if (query.isEmpty) {
      return installedMods;
    }
    return installedMods
        .where(
          (mod) =>
              mod.name.toLowerCase().contains(query) ||
              mod.id.toLowerCase().contains(query) ||
              mod.version.toLowerCase().contains(query),
        )
        .toList();
  }

  LauncherState copyWith({
    LauncherSection? section,
    bool? isBusy,
    String? statusMessage,
    IssueSeverity? statusSeverity,
    GameInstall? gameInstall,
    bool clearGameInstall = false,
    List<GameInstallCandidate>? gameInstallCandidates,
    List<LauncherProfile>? profiles,
    String? selectedProfileId,
    List<InstalledMod>? installedMods,
    List<RegistryMod>? registryMods,
    List<PackageSource>? packageSources,
    List<PackageSourceStatus>? sourceStatuses,
    WorldCatalog? worldCatalog,
    String? recentLog,
    String? launcherLog,
    DependencyResolutionResult? resolution,
    DependencyResolutionResult? profileResolution,
    LauncherUpdateSettings? launcherUpdates,
    LauncherUpdateStatus? launcherUpdateStatus,
    String? selectedModId,
    bool clearSelectedMod = false,
    String? modSearch,
    String? errorMessage,
    bool clearError = false,
    String? previewedPackagePath,
    String? previewedPackageSha256,
    bool clearPreview = false,
    PackageInstallPlan? installPlan,
    bool clearInstallPlan = false,
    DiagnosticBundle? diagnosticBundle,
    DeveloperWorkspace? developerWorkspace,
    DeveloperDoctorReport? developerDoctor,
    bool? developerMode,
    EnvironmentReport? developerEnvironment,
    DeveloperSetupResult? developerSetup,
    List<RegisteredProject>? developerProjects,
    List<UnityEditor>? unityEditors,
    RegisteredProject? managedProject,
    bool clearManagedProject = false,
    List<VpmResolvedPackage>? unityResolved,
    List<VpmPackageInfo>? unityAvailable,
    List<PackageSource>? unityRepos,
  }) {
    return LauncherState(
      section: section ?? this.section,
      isBusy: isBusy ?? this.isBusy,
      statusMessage: statusMessage ?? this.statusMessage,
      statusSeverity:
          statusSeverity ??
          (statusMessage != null ? IssueSeverity.info : this.statusSeverity),
      gameInstall: clearGameInstall ? null : gameInstall ?? this.gameInstall,
      gameInstallCandidates:
          gameInstallCandidates ?? this.gameInstallCandidates,
      profiles: profiles ?? this.profiles,
      selectedProfileId: selectedProfileId ?? this.selectedProfileId,
      installedMods: installedMods ?? this.installedMods,
      registryMods: registryMods ?? this.registryMods,
      packageSources: packageSources ?? this.packageSources,
      sourceStatuses: sourceStatuses ?? this.sourceStatuses,
      worldCatalog: worldCatalog ?? this.worldCatalog,
      recentLog: recentLog ?? this.recentLog,
      launcherLog: launcherLog ?? this.launcherLog,
      resolution: resolution ?? this.resolution,
      profileResolution: profileResolution ?? this.profileResolution,
      launcherUpdates: launcherUpdates ?? this.launcherUpdates,
      launcherUpdateStatus: launcherUpdateStatus ?? this.launcherUpdateStatus,
      selectedModId: clearSelectedMod
          ? null
          : selectedModId ?? this.selectedModId,
      modSearch: modSearch ?? this.modSearch,
      errorMessage: clearError ? null : errorMessage ?? this.errorMessage,
      previewedPackagePath: clearPreview
          ? null
          : previewedPackagePath ?? this.previewedPackagePath,
      previewedPackageSha256: clearPreview
          ? ''
          : previewedPackageSha256 ?? this.previewedPackageSha256,
      installPlan: clearInstallPlan ? null : installPlan ?? this.installPlan,
      diagnosticBundle: diagnosticBundle ?? this.diagnosticBundle,
      developerWorkspace: developerWorkspace ?? this.developerWorkspace,
      developerDoctor: developerDoctor ?? this.developerDoctor,
      developerMode: developerMode ?? this.developerMode,
      developerEnvironment: developerEnvironment ?? this.developerEnvironment,
      developerSetup: developerSetup ?? this.developerSetup,
      developerProjects: developerProjects ?? this.developerProjects,
      unityEditors: unityEditors ?? this.unityEditors,
      managedProject: clearManagedProject
          ? null
          : managedProject ?? this.managedProject,
      unityResolved: unityResolved ?? this.unityResolved,
      unityAvailable: unityAvailable ?? this.unityAvailable,
      unityRepos: unityRepos ?? this.unityRepos,
    );
  }

  /// Sections shown in the nav. The Developer tab is hidden unless developer mode is enabled.
  List<LauncherSection> get visibleSections => [
    LauncherSection.home,
    LauncherSection.setup,
    LauncherSection.mods,
    LauncherSection.browse,
    LauncherSection.profiles,
    if (developerMode) LauncherSection.developer,
    LauncherSection.diagnostics,
    LauncherSection.settings,
  ];
}
