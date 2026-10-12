part of 'launcher_event.dart';

// Developer-screen events: workspace and toolchain checks, the project
// registry, Unity/VPM packages, and the reachability probe. Split out of
// `launcher_event.dart` to stay inside the 500-line cap in AGENTS.md; they
// remain part of that library because `LauncherEvent` is sealed.

class DeveloperWorkspaceRefreshed extends LauncherEvent {
  const DeveloperWorkspaceRefreshed();
}

/// Re-runs the developer-toolchain audit (checkEnvironment).
class DeveloperEnvironmentChecked extends LauncherEvent {
  const DeveloperEnvironmentChecked();
}

/// Runs the safe auto-fixes (runSetup): ensures developer folders.
class DeveloperSetupRequested extends LauncherEvent {
  const DeveloperSetupRequested();
}

/// Builds and packs the current project into a .topiaforgemod.
class DeveloperProjectPacked extends LauncherEvent {
  const DeveloperProjectPacked();
}

/// Packs the current project and installs it into the detected game install.
class DeveloperProjectInstalledToGame extends LauncherEvent {
  const DeveloperProjectInstalledToGame();
}

/// Opens the current project's folder in the OS file manager.
class DeveloperProjectFolderOpened extends LauncherEvent {
  const DeveloperProjectFolderOpened();
}

/// Opens an external tool install URL (from an environment check's remediation link).
class DeveloperToolLinkOpened extends LauncherEvent {
  const DeveloperToolLinkOpened(this.url);

  final String url;
}

/// Scaffolds a new mod project with the given id/name (and optional Unity companion).
class DeveloperModProjectCreated extends LauncherEvent {
  const DeveloperModProjectCreated({
    required this.id,
    required this.name,
    this.includeUnityCompanion = false,
  });

  final String id;
  final String name;
  final bool includeUnityCompanion;
}

class DeveloperProjectResolved extends LauncherEvent {
  const DeveloperProjectResolved();
}

/// Refreshes the VCC-style projects registry list + the installed Unity editor list.
class DeveloperProjectsRefreshed extends LauncherEvent {
  const DeveloperProjectsRefreshed();
}

/// Adds an existing project directory to the registry (sniffs its kind).
class DeveloperProjectAdded extends LauncherEvent {
  const DeveloperProjectAdded(this.path);

  final String path;
}

/// Untracks a project from the registry (does not delete files).
class DeveloperProjectRemoved extends LauncherEvent {
  const DeveloperProjectRemoved(this.path);

  final String path;
}

/// Opens a tracked project in the matching Unity editor.
class DeveloperProjectOpenedInUnity extends LauncherEvent {
  const DeveloperProjectOpenedInUnity(this.path);

  final String path;
}

/// "Manage" a tracked project: loads its workspace into the per-project panes below the list.
class DeveloperProjectManaged extends LauncherEvent {
  const DeveloperProjectManaged(this.path);

  final String path;
}

/// Creates a new Unity authoring project from the bundled template (copies it, installs the companion, registers).
class DeveloperUnityProjectCreated extends LauncherEvent {
  const DeveloperUnityProjectCreated({
    required this.name,
    this.template = 'world',
  });

  final String name;
  final String template;
}

/// Resolves + restores the managed Unity project's VPM packages.
class DeveloperUnityResolved extends LauncherEvent {
  const DeveloperUnityResolved();
}

/// Adds a VPM package to the managed Unity project.
class DeveloperUnityPackageAdded extends LauncherEvent {
  const DeveloperUnityPackageAdded(this.id, {this.versionRange = '*'});

  final String id;
  final String versionRange;
}

/// Removes a VPM package from the managed Unity project.
class DeveloperUnityPackageRemoved extends LauncherEvent {
  const DeveloperUnityPackageRemoved(this.id);

  final String id;
}

/// Subscribes to a VPM repository (package listing).
class DeveloperUnityRepoAdded extends LauncherEvent {
  const DeveloperUnityRepoAdded(this.url);

  final String url;
}

/// Unsubscribes from a VPM repository.
class DeveloperUnityRepoRemoved extends LauncherEvent {
  const DeveloperUnityRepoRemoved(this.id);

  final String id;
}

class DeveloperDoctorRequested extends LauncherEvent {
  const DeveloperDoctorRequested();
}

class DeveloperSampleProjectCreated extends LauncherEvent {
  const DeveloperSampleProjectCreated();
}

/// Turns the developer-only NAT reachability probe on or off, and records whether the player agrees that an
/// aggregate, address-free result may be shared. Both default to off; see `docs/internal/LauncherReachabilityProbe.md`.
class ReachabilityProbeSettingsChanged extends LauncherEvent {
  const ReachabilityProbeSettingsChanged(this.settings);

  final ReachabilityProbeSettings settings;
}

/// Runs the reachability probe once, locally. The result is shown to the person who ran it and goes nowhere else.
class ReachabilityProbeRequested extends LauncherEvent {
  const ReachabilityProbeRequested();
}
