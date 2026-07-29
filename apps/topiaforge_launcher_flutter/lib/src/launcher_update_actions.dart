part of 'launcher_bloc.dart';

extension LauncherUpdateActions on LauncherBloc {
  Future<void> _onLauncherUpdateCheckRequested(
    LauncherUpdateCheckRequested event,
    Emitter<LauncherState> emit,
  ) async {
    final repository = _updateRepository;
    if (repository == null || !state.launcherUpdates.enabled) {
      return;
    }
    final status = await repository.checkForUpdate(
      currentVersion: TopiaForgeLauncherBuild.version,
      channel: state.launcherUpdates.channel,
      force: event.force,
    );
    emit(
      state.copyWith(
        launcherUpdateStatus: status,
        statusMessage: status.message,
        errorMessage: status.phase == LauncherUpdatePhase.failed
            ? status.message
            : null,
        clearError: status.phase != LauncherUpdatePhase.failed,
      ),
    );
  }

  Future<void> _onLauncherUpdateDownloadRequested(
    LauncherUpdateDownloadRequested event,
    Emitter<LauncherState> emit,
  ) async {
    final repository = _updateRepository;
    final candidate = state.launcherUpdateStatus.candidate;
    if (repository == null || candidate == null) return;
    final status = await repository.stageUpdate(candidate);
    emit(
      state.copyWith(
        launcherUpdateStatus: status,
        statusMessage: status.message,
        errorMessage: status.phase == LauncherUpdatePhase.failed
            ? status.message
            : null,
        clearError: status.phase != LauncherUpdatePhase.failed,
      ),
    );
  }

  Future<void> _onLauncherUpdateInstallConfirmed(
    LauncherUpdateInstallConfirmed event,
    Emitter<LauncherState> emit,
  ) async {
    final repository = _updateRepository;
    final staged = state.launcherUpdateStatus;
    if (repository == null || staged.phase != LauncherUpdatePhase.staged) {
      return;
    }
    await repository.applyStagedUpdate(staged);
  }

  Future<void> _onLauncherUpdateStatusChanged(
    LauncherUpdateStatusChanged event,
    Emitter<LauncherState> emit,
  ) async {
    emit(
      state.copyWith(
        launcherUpdateStatus: event.status,
        statusMessage: event.status.message,
      ),
    );
  }
}
