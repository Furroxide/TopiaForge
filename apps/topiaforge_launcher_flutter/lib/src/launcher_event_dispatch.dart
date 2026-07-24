part of 'launcher_bloc.dart';

/// One sequential event lane prevents async refreshes and mutations from
/// overwriting each other with stale snapshots. The explicit exhaustive
/// dispatch also makes a newly added event a compile-time integration task.
extension LauncherEventDispatch on LauncherBloc {
  FutureOr<void> _dispatchEvent(
    LauncherEvent event,
    Emitter<LauncherState> emit,
  ) {
    return switch (event) {
      LauncherStarted() ||
      LauncherRefreshRequested() ||
      PackageSourcesRefreshed() => _onLoad(event, emit),
      LauncherSectionSelected() => _onSectionSelected(event, emit),
      ModSelected() => _onModSelected(event, emit),
      ModSearchChanged() => _onModSearchChanged(event, emit),
      ProfileSelected() => _onProfileSelected(event, emit),
      ProfileLaunchRequested() => _onProfileLaunchRequested(event, emit),
      ProfileCreated() => _onProfileCreated(event, emit),
      SelectedProfileDuplicated() => _onSelectedProfileDuplicated(event, emit),
      SelectedProfileDeleted() => _onSelectedProfileDeleted(event, emit),
      SafeModeToggled() => _onSafeModeToggled(event, emit),
      ProfileManagerStateInheritanceChanged() =>
        _onProfileManagerStateInheritanceChanged(event, emit),
      ProfileModSelectionChanged() => _onProfileModSelectionChanged(
        event,
        emit,
      ),
      WorldSelectionChanged() => _onWorldSelectionChanged(event, emit),
      KnownInstallDetected() => _onKnownInstallDetected(event, emit),
      GameDirectorySelected() => _onGameDirectorySelected(event, emit),
      RuntimeRepaired() => _onRuntimeRepaired(event, emit),
      PackagePreviewRequested() => _onPackagePreviewRequested(event, emit),
      PreviewedPackageInstalled() => _onPreviewedPackageInstalled(event, emit),
      InboxPackagesInstalled() => _onInboxPackagesInstalled(event, emit),
      SelectedModEnabledChanged() => _onSelectedModEnabledChanged(event, emit),
      SelectedModRepairRequested() => _onSelectedModRepairRequested(
        event,
        emit,
      ),
      AllModsDisabled() => _onAllModsDisabled(event, emit),
      SelectedModUninstalled() => _onSelectedModUninstalled(event, emit),
      GameLaunchRequested() => _onGameLaunchRequested(event, emit),
      GameRestartRequested() => _onGameRestartRequested(event, emit),
      DiagnosticBundleRequested() => _onDiagnosticBundleRequested(event, emit),
      RecheckGameCompatRequested() => _onRecheckGameCompat(event, emit),
      SelectedProfileExported() => _onSelectedProfileExported(event, emit),
      ProfileImported() => _onProfileImported(event, emit),
      PackageSourceAdded() => _onPackageSourceAdded(event, emit),
      PackageSourceEnabledChanged() => _onPackageSourceEnabledChanged(
        event,
        emit,
      ),
      PackageSourceRemoved() => _onPackageSourceRemoved(event, emit),
      LauncherUpdateSettingsChanged() => _onLauncherUpdateSettingsChanged(
        event,
        emit,
      ),
      GameFolderOpened() => _onGameFolderOpened(event, emit),
      DataFolderOpened() => _onDataFolderOpened(event, emit),
      DeveloperModeToggled() => _onDeveloperModeToggled(event, emit),
      DeveloperWorkspaceRefreshed() => _onDeveloperWorkspaceRefreshed(
        event,
        emit,
      ),
      DeveloperEnvironmentChecked() => _onDeveloperEnvironmentChecked(
        event,
        emit,
      ),
      DeveloperSetupRequested() => _onDeveloperSetupRequested(event, emit),
      DeveloperProjectPacked() => _onDeveloperProjectPacked(event, emit),
      DeveloperProjectInstalledToGame() => _onDeveloperProjectInstalledToGame(
        event,
        emit,
      ),
      DeveloperProjectFolderOpened() => _onDeveloperProjectFolderOpened(
        event,
        emit,
      ),
      DeveloperToolLinkOpened() => _onDeveloperToolLinkOpened(event, emit),
      DeveloperModProjectCreated() => _onDeveloperModProjectCreated(
        event,
        emit,
      ),
      DeveloperProjectResolved() => _onDeveloperProjectResolved(event, emit),
      DeveloperProjectsRefreshed() => _onDeveloperProjectsRefreshed(
        event,
        emit,
      ),
      DeveloperProjectAdded() => _onDeveloperProjectAdded(event, emit),
      DeveloperProjectRemoved() => _onDeveloperProjectRemoved(event, emit),
      DeveloperProjectOpenedInUnity() => _onDeveloperProjectOpenedInUnity(
        event,
        emit,
      ),
      DeveloperProjectManaged() => _onDeveloperProjectManaged(event, emit),
      DeveloperUnityProjectCreated() => _onDeveloperUnityProjectCreated(
        event,
        emit,
      ),
      DeveloperUnityResolved() => _onDeveloperUnityResolved(event, emit),
      DeveloperUnityPackageAdded() => _onDeveloperUnityPackageAdded(
        event,
        emit,
      ),
      DeveloperUnityPackageRemoved() => _onDeveloperUnityPackageRemoved(
        event,
        emit,
      ),
      DeveloperUnityRepoAdded() => _onDeveloperUnityRepoAdded(event, emit),
      DeveloperUnityRepoRemoved() => _onDeveloperUnityRepoRemoved(event, emit),
      DeveloperDoctorRequested() => _onDeveloperDoctorRequested(event, emit),
      DeveloperSampleProjectCreated() => _onDeveloperSampleProjectCreated(
        event,
        emit,
      ),
      DeveloperUgcSettingsSaved() => _onDeveloperUgcSettingsSaved(event, emit),
      DeveloperUgcConfigDeployed() => _onDeveloperUgcConfigDeployed(
        event,
        emit,
      ),
      DeveloperWatchFolderOpened() => _onDeveloperWatchFolderOpened(
        event,
        emit,
      ),
      DeveloperUgcPublishToggled() => _onDeveloperUgcPublishToggled(
        event,
        emit,
      ),
      DeveloperUgcCleanupRequested() => _onDeveloperUgcCleanupRequested(
        event,
        emit,
      ),
      DeveloperUgcStatusRefreshed() => _onDeveloperUgcStatusRefreshed(
        event,
        emit,
      ),
      DeveloperUgcSidecarOutput() => _onDeveloperUgcSidecarOutput(event, emit),
      DeveloperUgcPublisherExited() => _onDeveloperUgcPublisherExited(
        event,
        emit,
      ),
      DeveloperUgcGoLive() => _onDeveloperUgcGoLive(event, emit),
    };
  }
}
