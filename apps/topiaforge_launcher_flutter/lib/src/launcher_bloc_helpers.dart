part of 'launcher_bloc.dart';

/// Snapshot projection and the shared busy/error wrapper.
extension LauncherBlocHelpers on LauncherBloc {
  LauncherState _snapshotState(
    LauncherSnapshot snapshot,
    String statusMessage, {
    String? selectedModId,
    IssueSeverity statusSeverity = IssueSeverity.info,
  }) {
    final changedLaunchContext =
        state.gameInstall?.path != snapshot.gameInstall?.path ||
        state.selectedProfileId != snapshot.selectedProfileId ||
        !snapshot.profiles.any(
          (profile) =>
              profile.id == state.selectedProfile?.id &&
              profile.revision == state.selectedProfile?.revision,
        );
    if (changedLaunchContext) {
      _activeLaunchRequest = null;
      _replacedGame = null;
    }
    final selected =
        selectedModId ??
        (snapshot.installedMods.any((mod) => mod.id == state.selectedModId)
            ? state.selectedModId
            : null) ??
        (snapshot.installedMods.isEmpty
            ? null
            : snapshot.installedMods.first.id);
    return state.copyWith(
      isBusy: false,
      statusMessage: snapshot.gameInstall == null
          ? 'Select or detect a Robotopia install to begin.'
          : statusMessage,
      statusSeverity: snapshot.gameInstall == null
          ? IssueSeverity.info
          : statusSeverity,
      gameInstall: snapshot.gameInstall,
      clearGameInstall: snapshot.gameInstall == null,
      gameInstallCandidates: snapshot.gameInstallCandidates,
      profiles: snapshot.profiles,
      selectedProfileId: snapshot.selectedProfileId,
      installedMods: snapshot.installedMods,
      registryMods: snapshot.registryMods,
      packageSources: snapshot.packageSources,
      sourceStatuses: snapshot.sourceStatuses,
      previewsByProfile: snapshot.previewsByProfile,
      clearLaunchActivity: changedLaunchContext,
      recentLog: snapshot.recentLog,
      launcherLog: snapshot.launcherLog,
      resolution: _dependencyPlanner.resolveInstalled(
        snapshot.installedMods,
        gameVersion: snapshot.gameInstall?.gameVersion,
        requireKnownGameVersion: true,
        platform: _launcherGamePlatform(snapshot.gameInstall),
        architecture: _launcherGameArchitecture(snapshot.gameInstall),
        contentTargets: _launcherGameContentTargets(snapshot.gameInstall),
      ),
      launcherUpdates: snapshot.launcherUpdates,
      selectedModId: selected,
      clearSelectedMod: selected == null,
      developerMode: snapshot.developerMode,
      clearError: true,
      snapshotLoaded: true,
    );
  }

  /// Whether a save may be built from the state's profiles, package sources
  /// or update settings. Each such save replaces what is on disk, so until a
  /// snapshot has loaded it is refused: one built from the stand-ins would
  /// replace the player's own.
  bool _canSaveFromState(Emitter<LauncherState> emit, String statusMessage) {
    if (state.snapshotLoaded) return true;
    emit(
      state.copyWith(
        isBusy: false,
        statusMessage: statusMessage,
        statusSeverity: IssueSeverity.error,
        errorMessage:
            'The launcher state has not loaded, so nothing was changed. '
            'Refresh it, then try again.',
      ),
    );
    return false;
  }

  /// Runs [save] and says whether it succeeded. A failure shows its message,
  /// as when the settings file cannot be read just now, and changes nothing.
  Future<bool> _savedSetting(
    Emitter<LauncherState> emit,
    Future<void> Function() save,
  ) async {
    try {
      await save();
      return true;
    } on Object catch (error) {
      emit(
        state.copyWith(
          statusMessage: 'Action failed.',
          statusSeverity: IssueSeverity.error,
          errorMessage: error.toString(),
        ),
      );
      return false;
    }
  }

  Future<void> _guard(
    Emitter<LauncherState> emit,
    String successMessage,
    Future<void> Function() run,
  ) async {
    emit(state.copyWith(isBusy: true, clearError: true));
    try {
      await run();
      if (state.isBusy) {
        emit(
          state.copyWith(
            isBusy: false,
            statusMessage: successMessage,
            clearError: true,
          ),
        );
      }
    } on Object catch (error) {
      emit(
        state.copyWith(
          isBusy: false,
          errorMessage: error.toString(),
          statusMessage: 'Action failed.',
        ),
      );
    }
  }
}
