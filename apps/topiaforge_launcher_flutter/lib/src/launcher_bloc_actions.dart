part of 'launcher_bloc.dart';

extension LauncherBlocActions on LauncherBloc {
  Future<void> _onPackageSourceAdded(
    PackageSourceAdded event,
    Emitter<LauncherState> emit,
  ) async {
    final id = 'source-${DateTime.now().millisecondsSinceEpoch}';
    final source = PackageSource(id: id, name: event.name, url: event.url);
    await _guard(emit, 'Added package source.', () async {
      await _repository.savePackageSources([...state.packageSources, source]);
      emit(_snapshotState(await _repository.loadSnapshot(), 'Ready.'));
    });
  }

  Future<void> _onPackageSourceEnabledChanged(
    PackageSourceEnabledChanged event,
    Emitter<LauncherState> emit,
  ) async {
    final sources = [
      for (final source in state.packageSources)
        if (source.id == event.sourceId)
          source.copyWith(enabled: event.enabled)
        else
          source,
    ];
    await _guard(emit, 'Updated package source.', () async {
      await _repository.savePackageSources(sources);
      emit(_snapshotState(await _repository.loadSnapshot(), 'Ready.'));
    });
  }

  Future<void> _onPackageSourceRemoved(
    PackageSourceRemoved event,
    Emitter<LauncherState> emit,
  ) async {
    final sources = state.packageSources
        .where((source) => source.id != event.sourceId || source.builtIn)
        .toList();
    await _guard(emit, 'Removed package source.', () async {
      await _repository.savePackageSources(sources);
      emit(_snapshotState(await _repository.loadSnapshot(), 'Ready.'));
    });
  }

  Future<void> _onGameFolderOpened(
    GameFolderOpened event,
    Emitter<LauncherState> emit,
  ) async {
    final install = state.gameInstall;
    if (install != null) {
      await _repository.openPath(install.path);
    }
  }

  Future<void> _onDataFolderOpened(
    DataFolderOpened event,
    Emitter<LauncherState> emit,
  ) {
    return _repository.openPath(_repository.dataRoot);
  }

  Future<GameInstall?> _repairRuntimeBeforeLaunchIfNeeded(
    Emitter<LauncherState> emit,
    GameInstall install,
  ) async {
    if (!install.needsRepair) {
      return install;
    }

    emit(state.copyWith(statusMessage: 'Repairing runtime before launch.'));
    final report = await _repository.installOrRepairRuntime(install);
    final snapshot = await _repository.loadSnapshot();
    final refreshed = snapshot.gameInstall;
    final repaired =
        report.ok &&
        refreshed != null &&
        !refreshed.needsRepair &&
        refreshed.canLaunch;

    if (!repaired) {
      final message = _runtimeRepairFailureMessage(report, refreshed);
      emit(
        _snapshotState(snapshot, message).copyWith(
          isBusy: false,
          statusMessage: message,
          errorMessage: message,
        ),
      );
      return null;
    }

    emit(
      _snapshotState(
        snapshot,
        'Runtime repaired. Launching TopiaForge.',
      ).copyWith(isBusy: true, clearError: true),
    );
    return refreshed;
  }

  String _runtimeRepairFailureMessage(
    RepairReport report,
    GameInstall? install,
  ) {
    final messages = [
      ...report.issues
          .where((issue) => issue.isBlocking)
          .map((issue) => issue.message),
      if (install != null)
        ...install.issues
            .where((issue) => issue.isBlocking)
            .map((issue) => issue.message),
    ];
    if (messages.isEmpty && install?.needsRepair == true) {
      messages.add('Runtime files are still missing or stale after repair.');
    }
    if (messages.isEmpty) {
      messages.add('Open Setup or Diagnostics for details.');
    }
    return [
      'Automatic runtime repair could not complete.',
      ...messages,
    ].join(' ');
  }
}

String _packageInboxMessage(PackageInboxInstallOutcome outcome) {
  final summary = switch (outcome.status) {
    PackageInboxInstallStatus.success when outcome.candidateCount == 0 =>
      'Package inbox is empty.',
    PackageInboxInstallStatus.success =>
      'Installed ${outcome.installedCount} package(s) and consumed '
          '${outcome.consumedCount} inbox file(s).',
    PackageInboxInstallStatus.partial =>
      'Package inbox partially processed: ${outcome.installedCount} '
          'installed, ${outcome.retainedCount} retained.',
    PackageInboxInstallStatus.failure =>
      'Package inbox failed: no packages installed; '
          '${outcome.retainedCount} retained.',
  };
  return outcome.issues.isEmpty
      ? summary
      : '$summary ${outcome.issues.first.message}';
}
