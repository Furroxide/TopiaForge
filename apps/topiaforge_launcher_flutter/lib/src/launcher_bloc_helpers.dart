part of 'launcher_bloc.dart';

/// Shared bloc plumbing: projecting a repository snapshot onto launcher state, and the busy/error wrapper every
/// action handler runs inside.
extension LauncherBlocHelpers on LauncherBloc {
  LauncherState _snapshotState(
    LauncherSnapshot snapshot,
    String statusMessage, {
    String? selectedModId,
    IssueSeverity statusSeverity = IssueSeverity.info,
  }) {
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
      worldCatalog: snapshot.worldCatalog,
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
    );
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
