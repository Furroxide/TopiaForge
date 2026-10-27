import 'package:launcher_domain/launcher_domain.dart';

import 'launcher_multiplayer_state.dart' show JoinFlowOrigin;
import 'launcher_section.dart';
import 'session_link_inbox.dart';

part 'launcher_developer_event.dart';
part 'launcher_map_source_event.dart';
part 'launcher_multiplayer_event.dart';
part 'launcher_multiplayer_join_event.dart';
part 'launcher_multiplayer_settings_event.dart';

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

/// Selects [profileId] and launches the game in one sequential handler.
/// Firing ProfileSelected + GameLaunchRequested back-to-back would race: the
/// launch handler can read the old selectedProfile before the selection emits.
class ProfileLaunchRequested extends LauncherEvent {
  const ProfileLaunchRequested(this.profileId);

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

class ProfileManagerStateInheritanceChanged extends LauncherEvent {
  const ProfileManagerStateInheritanceChanged(this.enabled);

  final bool enabled;
}

class ProfileModSelectionChanged extends LauncherEvent {
  const ProfileModSelectionChanged(this.modId, this.enabled);

  final String modId;
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

class SelectedModRepairRequested extends LauncherEvent {
  const SelectedModRepairRequested();
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

class LaunchSelectionChanged extends LauncherEvent {
  const LaunchSelectionChanged(this.selection);
  final LaunchSelection selection;
}

class LaunchPreviewUpdated extends LauncherEvent {
  const LaunchPreviewUpdated({
    required this.profileId,
    required this.revision,
    required this.installPath,
    required this.generation,
    this.preview,
    this.error,
  });
  final String profileId;
  final int revision;
  final String installPath;
  final int generation;
  final LaunchPreview? preview;
  final String? error;
}

class LaunchActivityUpdated extends LauncherEvent {
  const LaunchActivityUpdated(this.activity);
  final LaunchActivity activity;
}

class LaunchActivityMonitorFailed extends LauncherEvent {
  const LaunchActivityMonitorFailed(this.message);
  final String message;
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

class LauncherUpdateSettingsChanged extends LauncherEvent {
  const LauncherUpdateSettingsChanged({
    this.enabled,
    this.checkAutomatically,
    this.channel,
  });

  final bool? enabled;
  final bool? checkAutomatically;
  final LauncherUpdateChannel? channel;
}

class LauncherUpdateCheckRequested extends LauncherEvent {
  const LauncherUpdateCheckRequested({this.force = true});

  final bool force;
}

class LauncherUpdateDownloadRequested extends LauncherEvent {
  const LauncherUpdateDownloadRequested();
}

class LauncherUpdateInstallConfirmed extends LauncherEvent {
  const LauncherUpdateInstallConfirmed();
}

class LauncherUpdateStatusChanged extends LauncherEvent {
  const LauncherUpdateStatusChanged(this.status);

  final LauncherUpdateStatus status;
}

class GameFolderOpened extends LauncherEvent {
  const GameFolderOpened();
}

class DataFolderOpened extends LauncherEvent {
  const DataFolderOpened();
}

class DownloadedMapsClearRequested extends LauncherEvent {
  const DownloadedMapsClearRequested();
}

/// Toggles opt-in developer mode (shows/hides the Developer tab). Off by default for consumers.
class DeveloperModeToggled extends LauncherEvent {
  const DeveloperModeToggled(this.enabled);

  final bool enabled;
}
