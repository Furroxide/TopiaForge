part of 'launcher_bloc.dart';

extension LauncherGameInstallActions on LauncherBloc {
  Future<void> _onKnownInstallDetected(
    KnownInstallDetected event,
    Emitter<LauncherState> emit,
  ) async {
    await _guard(emit, 'Detected Robotopia install.', () async {
      final install = await _repository.detectKnownInstall();
      if (install == null) {
        emit(
          state.copyWith(
            isBusy: false,
            statusMessage: 'Known Robotopia launcher path was not found.',
          ),
        );
        return;
      }
      await _repository.selectGameDirectory(install.path);
      emit(_snapshotState(await _repository.loadSnapshot(), 'Ready.'));
    });
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
