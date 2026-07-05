part of 'launcher_bloc.dart';

extension LauncherBlocActions on LauncherBloc {
  Future<void> _onWorldSelectionChanged(
    WorldSelectionChanged event,
    Emitter<LauncherState> emit,
  ) async {
    final selected = state.selectedProfile;
    if (selected == null) {
      return;
    }
    // Normalize before persisting so the stored selection is always consistent with the catalog and the
    // runtime's allowed set. This is the single authoritative place that guards persisted world state: an
    // unknown world/gamemode id is ignored (keeps the prior value) and loadMode is clamped to a known mode.
    final catalog = state.worldCatalog;
    final worldId = catalog.worlds.any((world) => world.id == event.worldId)
        ? event.worldId
        : null;
    final gamemodeId =
        catalog.gamemodes.any((mode) => mode.id == event.gamemodeId)
        ? event.gamemodeId
        : null;
    // Reconcile the load mode against the world this selection will actually point at. The load-mode
    // control in the UI only clamps for DISPLAY, so without this a world change (or an untouched default)
    // could leave a load mode the world cannot honour persisted and written to the runtime config — e.g.
    // additiveArena for a checkpoint level that is scene-replacement only. The runtime would then receive
    // an incoherent (world, loadMode) pair, so this is reconciled here, the single authoritative guard.
    final prior = selected.worldSelection;
    final resolvedWorldId = worldId ?? prior.worldId;
    final loadMode = catalog.reconcileLoadMode(
      resolvedWorldId,
      event.loadMode ?? prior.loadMode,
    );
    final updated = selected.copyWith(
      worldSelection: prior.copyWith(
        worldId: worldId,
        gamemodeId: gamemodeId,
        loadMode: loadMode,
        autoLoadOnStart: event.autoLoadOnStart,
      ),
    );
    final profiles = [
      for (final profile in state.profiles)
        if (profile.id == updated.id) updated else profile,
    ];
    await _repository.saveProfiles(profiles, updated.id);
    emit(
      state.copyWith(
        profiles: profiles,
        selectedProfileId: updated.id,
        statusMessage: 'Updated world launch settings.',
      ),
    );
  }

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

  Future<void> _onGameLaunchRequested(
    GameLaunchRequested event,
    Emitter<LauncherState> emit,
  ) async {
    final install = state.gameInstall;
    final profile = state.selectedProfile;
    if (install == null || profile == null) {
      return;
    }
    await _guard(emit, 'Launched Robotopia.', () async {
      final launchInstall = await _repairRuntimeBeforeLaunchIfNeeded(
        emit,
        install,
      );
      if (launchInstall == null) {
        return;
      }
      final result = await _repository.launch(launchInstall, profile);
      emit(_launchResultState(result));
    });
  }

  Future<void> _onGameRestartRequested(
    GameRestartRequested event,
    Emitter<LauncherState> emit,
  ) async {
    final install = state.gameInstall;
    final profile = state.selectedProfile;
    if (install == null || profile == null) {
      return;
    }
    await _guard(emit, 'Restarted Robotopia.', () async {
      final launchInstall = await _repairRuntimeBeforeLaunchIfNeeded(
        emit,
        install,
      );
      if (launchInstall == null) {
        return;
      }
      final result = await _repository.restart(launchInstall, profile);
      emit(_launchResultState(result));
    });
  }

  LauncherState _launchResultState(LaunchResult result) {
    return state.copyWith(
      isBusy: false,
      statusMessage: result.message,
      errorMessage: result.started ? null : result.message,
      clearError: result.started,
    );
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
        'Runtime repaired. Launching Robotopia.',
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
