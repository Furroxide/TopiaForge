part of 'launcher_bloc.dart';

extension LauncherCleanupActions on LauncherBloc {
  void _showCleanup(Emitter<LauncherState> emit, ServerCleanupView view) =>
      emit(
        state.copyWith(multiplayer: state.multiplayer.copyWith(cleanup: view)),
      );

  Future<void> _dispatchCleanup(
    ServerCleanupEvent event,
    Emitter<LauncherState> emit,
  ) async {
    final repository = _repository;
    final install = state.gameInstall;
    final view = state.multiplayer.cleanup;
    switch (event) {
      case ServerCleanupClosed():
        _multiplayerWork.cleanup++;
        _showCleanup(emit, const ServerCleanupView());
      case ServerCleanupRequested(:final profiles):
        if (repository is! MultiplayerContentHost ||
            install == null ||
            view.busy) {
          return;
        }
        final generation = ++_multiplayerWork.cleanup;
        _showCleanup(
          emit,
          ServerCleanupView(
            generation: generation,
            busy: true,
            catalog: view.catalog,
          ),
        );
        _beside<ServerContentCleanupPlan>(
          (repository as MultiplayerContentHost).planServerContentCleanup(
            install,
            serverProfileIds: profiles,
          ),
          (plan) => ServerCleanupPlanned(
            generation,
            plan,
            reviewing: profiles != null,
          ),
          () => ServerCleanupPlanned(generation, null, reviewing: false),
        );
      case ServerCleanupPlanned(
        :final generation,
        :final plan,
        :final reviewing,
      ):
        if (generation != _multiplayerWork.cleanup) {
          return;
        }
        _showCleanup(
          emit,
          ServerCleanupView(
            generation: generation,
            catalog: reviewing ? view.catalog : plan,
            plan: reviewing ? plan : null,
            reviewing: reviewing && plan != null,
            message: plan == null
                ? 'Server content could not be read. Close programs using the game folder and try again.'
                : '',
          ),
        );
      case ServerCleanupConfirmed(:final fingerprint):
        final plan = view.plan;
        if (repository is! MultiplayerContentHost ||
            install == null ||
            view.busy ||
            !view.reviewing ||
            plan == null ||
            plan.fingerprint != fingerprint ||
            plan.isEmpty) {
          return;
        }
        final generation = ++_multiplayerWork.cleanup;
        _showCleanup(
          emit,
          ServerCleanupView(
            generation: generation,
            busy: true,
            catalog: view.catalog,
            plan: plan,
            reviewing: true,
          ),
        );
        _beside<ServerContentCleanupResult>(
          (repository as MultiplayerContentHost).applyServerContentCleanup(
            install,
            plan,
          ),
          (result) => ServerCleanupFinished(generation, result),
          () => ServerCleanupFinished(generation, null),
        );
      case ServerCleanupFinished(:final generation, :final result):
        if (generation != _multiplayerWork.cleanup) {
          return;
        }
        _showCleanup(
          emit,
          ServerCleanupView(
            generation: generation,
            message: result == null
                ? 'Cleanup could not finish. Refresh server content before trying again.'
                : result.applied
                ? 'Removed ${result.removedProfiles.length} server profiles and ${result.removedVersions.length} versions; freed ${result.freedBytes} bytes. ${result.message}'
                : switch (result.refusal) {
                    ServerContentCleanupRefusal.stale =>
                      'The content changed. Refresh and review the cleanup again; nothing was removed.',
                    ServerContentCleanupRefusal.gameRunning =>
                      'Close Robotopia, then review cleanup again; nothing was removed.',
                    ServerContentCleanupRefusal.busy =>
                      'A launch or install is using these files. Wait, then review cleanup again.',
                    _ =>
                      'Server content could not be read. Refresh it before trying again.',
                  },
          ),
        );
        if (result?.applied == true) add(const LauncherRefreshRequested());
    }
  }
}
