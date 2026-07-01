import 'launcher_section.dart';

sealed class LauncherEvent {
  const LauncherEvent();
}

class LauncherStarted extends LauncherEvent {
  const LauncherStarted();
}

class LauncherRefreshRequested extends LauncherEvent {
  const LauncherRefreshRequested();
}

class LauncherSectionSelected extends LauncherEvent {
  const LauncherSectionSelected(this.section);

  final LauncherSection section;
}

class ModSelected extends LauncherEvent {
  const ModSelected(this.modId);

  final String modId;
}

class ModSearchChanged extends LauncherEvent {
  const ModSearchChanged(this.query);

  final String query;
}

class ProfileSelected extends LauncherEvent {
  const ProfileSelected(this.profileId);

  final String profileId;
}

class ProfileCreated extends LauncherEvent {
  const ProfileCreated();
}

class SelectedProfileDuplicated extends LauncherEvent {
  const SelectedProfileDuplicated();
}

class SelectedProfileDeleted extends LauncherEvent {
  const SelectedProfileDeleted();
}

class SafeModeToggled extends LauncherEvent {
  const SafeModeToggled(this.enabled);

  final bool enabled;
}

class KnownInstallDetected extends LauncherEvent {
  const KnownInstallDetected();
}

class GameDirectorySelected extends LauncherEvent {
  const GameDirectorySelected(this.path);

  final String path;
}

class RuntimeRepaired extends LauncherEvent {
  const RuntimeRepaired();
}

class PackagePreviewRequested extends LauncherEvent {
  const PackagePreviewRequested(
    this.packagePath, {
    this.expectedSha256 = '',
    this.sourceId = '',
    this.sourceName = '',
  });

  final String packagePath;
  final String expectedSha256;
  final String sourceId;
  final String sourceName;
}

class PreviewedPackageInstalled extends LauncherEvent {
  const PreviewedPackageInstalled();
}

class InboxPackagesInstalled extends LauncherEvent {
  const InboxPackagesInstalled();
}

class SelectedModEnabledChanged extends LauncherEvent {
  const SelectedModEnabledChanged(this.enabled);

  final bool enabled;
}

class AllModsDisabled extends LauncherEvent {
  const AllModsDisabled();
}

class SelectedModUninstalled extends LauncherEvent {
  const SelectedModUninstalled();
}

class GameLaunchRequested extends LauncherEvent {
  const GameLaunchRequested();
}

class GameRestartRequested extends LauncherEvent {
  const GameRestartRequested();
}

class DiagnosticBundleRequested extends LauncherEvent {
  const DiagnosticBundleRequested();
}

/// Force a fresh game-compatibility check (bypassing the game-version cache). WARN-ONLY: never affects launch.
class RecheckGameCompatRequested extends LauncherEvent {
  const RecheckGameCompatRequested();
}

class SelectedProfileExported extends LauncherEvent {
  const SelectedProfileExported(this.path);

  final String path;
}

class ProfileImported extends LauncherEvent {
  const ProfileImported(this.path);

  final String path;
}

class WorldSelectionChanged extends LauncherEvent {
  const WorldSelectionChanged({
    this.worldId,
    this.gamemodeId,
    this.loadMode,
    this.autoLoadOnStart,
  });

  final String? worldId;
  final String? gamemodeId;
  final String? loadMode;
  final bool? autoLoadOnStart;
}

class PackageSourceAdded extends LauncherEvent {
  const PackageSourceAdded({required this.name, required this.url});

  final String name;
  final String url;
}

class PackageSourceEnabledChanged extends LauncherEvent {
  const PackageSourceEnabledChanged(this.sourceId, this.enabled);

  final String sourceId;
  final bool enabled;
}

class PackageSourceRemoved extends LauncherEvent {
  const PackageSourceRemoved(this.sourceId);

  final String sourceId;
}

class PackageSourcesRefreshed extends LauncherEvent {
  const PackageSourcesRefreshed();
}

class GameFolderOpened extends LauncherEvent {
  const GameFolderOpened();
}

class DataFolderOpened extends LauncherEvent {
  const DataFolderOpened();
}

/// Toggles opt-in developer mode (shows/hides the Developer tab). Off by default for consumers.
class DeveloperModeToggled extends LauncherEvent {
  const DeveloperModeToggled(this.enabled);

  final bool enabled;
}

class DeveloperWorkspaceRefreshed extends LauncherEvent {
  const DeveloperWorkspaceRefreshed();
}

/// Re-runs the developer-toolchain audit (checkEnvironment).
class DeveloperEnvironmentChecked extends LauncherEvent {
  const DeveloperEnvironmentChecked();
}

/// Runs the safe auto-fixes (runSetup): installs the UGC sidecar deps, ensures folders.
class DeveloperSetupRequested extends LauncherEvent {
  const DeveloperSetupRequested();
}

/// Builds and packs the current project into a .robotopiamod.
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

/// Saves UGC live-sync settings into the project (and deploys them to the detected install). Only the provided
/// fields change; the rest are kept from the current project settings.
class DeveloperUgcSettingsSaved extends LauncherEvent {
  const DeveloperUgcSettingsSaved({
    this.transport,
    this.watchFolder,
    this.editorUrl,
    this.documentUrl,
    this.syncServerUrl,
    this.sceneId,
    this.autoConnectOnStart,
  });

  final String? transport;
  final String? watchFolder;
  final String? editorUrl;
  final String? documentUrl;
  final String? syncServerUrl;
  final String? sceneId;
  final bool? autoConnectOnStart;
}

/// Writes the current project's UGC live-sync config into the detected game install.
class DeveloperUgcConfigDeployed extends LauncherEvent {
  const DeveloperUgcConfigDeployed();
}

/// Opens the configured UGC watch folder in the OS file manager.
class DeveloperWatchFolderOpened extends LauncherEvent {
  const DeveloperWatchFolderOpened();
}

/// Starts or stops the Automerge publisher (Node sidecar) in watch mode for the current watch folder.
class DeveloperUgcPublishToggled extends LauncherEvent {
  const DeveloperUgcPublishToggled();
}

/// Refreshes the cockpit's live diagnostics: reads the game's status handshake and the watch-folder scenes.
class DeveloperUgcStatusRefreshed extends LauncherEvent {
  const DeveloperUgcStatusRefreshed();
}

/// Internal: one line of output from the running Automerge publisher. The launcher parses the machine-readable
/// `ROBOTOPIA_UGC_SESSION {json}` line to auto-detect the live document URL and pre-populate the game's config.
class DeveloperUgcSidecarOutput extends LauncherEvent {
  const DeveloperUgcSidecarOutput(this.line);

  final String line;
}

/// One-button "Go Live": runs setup, starts the publisher (Automerge), deploys the auto-detected config with
/// auto-connect, and launches the game — the zero-manual-scripts path.
class DeveloperUgcGoLive extends LauncherEvent {
  const DeveloperUgcGoLive();
}
