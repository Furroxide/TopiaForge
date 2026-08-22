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
    this.reachabilityProbe = const ReachabilityProbeSettings(),
    this.reachabilityResult,
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

  /// Opt-in reachability probe settings. Off by default; the probe is developer-mode only and never runs on its own.
  final ReachabilityProbeSettings reachabilityProbe;

  /// The last local probe classification, or null if the probe has not been run this session. Never persisted.
  final NatClassification? reachabilityResult;

  /// Last developer-toolchain audit (.NET/Node/Unity/Git), shown in the Dev tab's Environment pane.
  final EnvironmentReport? developerEnvironment;

  /// Last setup/auto-fix result (action log), shown after running Setup in the Dev tab.
  final DeveloperSetupResult? developerSetup;

  /// Scenes parsed from the newest exported project in the watch folder (drives the cockpit's scene dropdown).

  /// Recent lines from the running Automerge publisher (Node sidecar), shown in the cockpit's console view.

  /// The live Automerge document url auto-captured from the publisher's output (pre-populated into the game).

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

  bool get canLaunch {
    return gameInstall != null &&
        gameInstall!.canLaunch &&
        !gameInstall!.needsRepair &&
        selectedProfile != null;
  }

  bool get canStartLaunchFlow {
    return gameInstall != null &&
        gameInstall!.canLaunch &&
        selectedProfile != null;
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
    ReachabilityProbeSettings? reachabilityProbe,
    NatClassification? reachabilityResult,
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
      reachabilityProbe: reachabilityProbe ?? this.reachabilityProbe,
      reachabilityResult: reachabilityResult ?? this.reachabilityResult,
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
