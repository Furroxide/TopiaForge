part of 'launcher_bloc.dart';

extension LauncherGameInstallActions on LauncherBloc {
  Future<void> _onKnownInstallDetected(
    KnownInstallDetected event,
    Emitter<LauncherState> emit,
  ) async {
    await _guard(emit, 'Searched for Robotopia installs.', () async {
      final candidates = await _discoverGameInstallCandidates();
      if (candidates.isEmpty) {
        emit(
          state.copyWith(
            clearGameInstall: true,
            gameInstallCandidates: const [],
          ),
        );
        final refreshed = _snapshotState(
          await _repository.loadSnapshot(),
          'No validated Robotopia install was found. Choose the folder manually.',
        );
        emit(
          refreshed.copyWith(
            clearGameInstall: true,
            gameInstallCandidates: const [],
          ),
        );
        return;
      }
      final currentPath = state.gameInstall?.path;
      final preferred = candidates.firstWhere(
        (candidate) => candidate.install.path == currentPath,
        orElse: () => candidates.first,
      );
      await _repository.selectGameDirectory(preferred.install.path);
      final count = candidates.length;
      emit(
        _snapshotState(
          await _repository.loadSnapshot(),
          count == 1
              ? 'Detected one Robotopia install.'
              : 'Detected $count Robotopia installs. Select one in Setup or Settings.',
        ),
      );
    });
  }

  Future<List<GameInstallCandidate>> _discoverGameInstallCandidates() async {
    final repository = _repository;
    if (repository is GameInstallDiscoveryRepository) {
      return repository.discoverGameInstalls();
    }
    final install = await repository.detectKnownInstall();
    return install == null
        ? const []
        : [
            GameInstallCandidate(
              install: install,
              sources: const [
                GameInstallDiscoverySource(
                  id: 'known-install',
                  label: 'Known install',
                  precedence: 0,
                ),
              ],
            ),
          ];
  }

  Future<void> _onGameDirectorySelected(
    GameDirectorySelected event,
    Emitter<LauncherState> emit,
  ) async {
    await _guard(emit, 'Selected Robotopia folder.', () async {
      await _repository.selectGameDirectory(event.path);
      emit(_snapshotState(await _repository.loadSnapshot(), 'Ready.'));
    });
  }

  Future<void> _onRuntimeRepaired(
    RuntimeRepaired event,
    Emitter<LauncherState> emit,
  ) async {
    final install = state.gameInstall;
    if (install == null) {
      return;
    }
    await _guard(emit, 'Repair complete.', () async {
      final report = await _repository.installOrRepairRuntime(install);
      emit(
        _snapshotState(
          await _repository.loadSnapshot(),
          report.ok
              ? report.actions.join(' ')
              : report.issues.map((issue) => issue.message).join(' '),
        ),
      );
    });
  }

  Future<void> _onRecheckGameCompat(
    RecheckGameCompatRequested event,
    Emitter<LauncherState> emit,
  ) async {
    final install = state.gameInstall;
    if (install == null) {
      return;
    }
    await _guard(emit, 'Rechecked game compatibility.', () async {
      final compat = await _repository.checkGameCompat(install);
      emit(
        state.copyWith(
          isBusy: false,
          gameInstall: install.copyWith(compatStatus: compat),
          statusMessage: _compatSummary(compat),
        ),
      );
    });
  }

  String _compatSummary(GameCompatStatus compat) {
    switch (compat.status) {
      case 'ok':
        return 'All mod bindings are compatible with the installed game.';
      case 'broken':
        return '${compat.errorCount} mod feature(s) may not work with this game version.';
      case 'skipped':
        return 'No game version detected for the compatibility check.';
      default:
        return 'Game compatibility could not be verified.';
    }
  }
}
