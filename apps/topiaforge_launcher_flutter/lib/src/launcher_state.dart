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
    this.gameInstall,
    this.selectedModId,
    this.modSearch = '',
    this.errorMessage,
    this.previewedPackagePath,
    this.previewedPackageSha256 = '',
    this.installPlan,
    this.diagnosticBundle,
    this.developerWorkspace,
    this.developerDoctor,
    this.ugcPublisherRunning = false,
    this.developerMode = false,
    this.developerEnvironment,
    this.developerSetup,
    this.ugcStatus,
    this.ugcScenes = const [],
    this.ugcSidecarLog = const [],
    this.ugcCapturedDocumentUrl = '',
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
    launcherUpdates: const LauncherUpdateSettings(),
  );

  final LauncherSection section;
  final bool isBusy;
  final String statusMessage;
  final IssueSeverity statusSeverity;
  final GameInstall? gameInstall;
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
  final String? selectedModId;
  final String modSearch;
  final String? errorMessage;
  final String? previewedPackagePath;
  final String previewedPackageSha256;
  final PackageInstallPlan? installPlan;
  final DiagnosticBundle? diagnosticBundle;
  final DeveloperWorkspace? developerWorkspace;
  final DeveloperDoctorReport? developerDoctor;

  /// True while the UGC Automerge publisher (Node sidecar) is running in watch mode from the launcher.
  final bool ugcPublisherRunning;

  /// Opt-in developer mode (off by default). Controls whether the Developer tab is shown.
  final bool developerMode;

  /// Last developer-toolchain audit (.NET/Node/Unity/Git), shown in the Dev tab's Environment pane.
  final EnvironmentReport? developerEnvironment;

  /// Last setup/auto-fix result (action log), shown after running Setup in the Dev tab.
  final DeveloperSetupResult? developerSetup;

  /// Last UGC live-sync status read from the game's handshake file (default watch folder, connected doc, scenes).
  final UgcLiveSyncStatusSnapshot? ugcStatus;

  /// Scenes parsed from the newest exported project in the watch folder (drives the cockpit's scene dropdown).
  final List<UgcSceneRef> ugcScenes;

  /// Recent lines from the running Automerge publisher (Node sidecar), shown in the cockpit's console view.
  final List<String> ugcSidecarLog;

  /// The live Automerge document url auto-captured from the publisher's output (pre-populated into the game).
  final String ugcCapturedDocumentUrl;

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

  /// Convenience accessor for the current project's UGC live-sync settings (defaults when none).
  UgcLiveSyncSettings get ugcLiveSync =>
      developerWorkspace?.project?.unityCompanion.liveSync ??
      const UgcLiveSyncSettings();

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
    bool? ugcPublisherRunning,
    bool? developerMode,
    EnvironmentReport? developerEnvironment,
    DeveloperSetupResult? developerSetup,
    UgcLiveSyncStatusSnapshot? ugcStatus,
    bool clearUgcStatus = false,
    List<UgcSceneRef>? ugcScenes,
    List<String>? ugcSidecarLog,
    String? ugcCapturedDocumentUrl,
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
      ugcPublisherRunning: ugcPublisherRunning ?? this.ugcPublisherRunning,
      developerMode: developerMode ?? this.developerMode,
      developerEnvironment: developerEnvironment ?? this.developerEnvironment,
      developerSetup: developerSetup ?? this.developerSetup,
      ugcStatus: clearUgcStatus ? null : ugcStatus ?? this.ugcStatus,
      ugcScenes: ugcScenes ?? this.ugcScenes,
      ugcSidecarLog: ugcSidecarLog ?? this.ugcSidecarLog,
      ugcCapturedDocumentUrl:
          ugcCapturedDocumentUrl ?? this.ugcCapturedDocumentUrl,
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
