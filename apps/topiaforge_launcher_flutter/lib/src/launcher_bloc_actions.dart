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
    final knownWorld = catalog.worlds.any((world) => world.id == event.worldId);
    final knownGamemode = catalog.gamemodes.any(
      (mode) => mode.id == event.gamemodeId,
    );
    final worldId = knownWorld ? event.worldId : null;
    final gamemodeId = knownGamemode ? event.gamemodeId : null;
    // Say so when a choice is dropped. Silently keeping the prior value is how a stale catalog looks
    // exactly like a launcher that ignores you.
    final discarded = <String>[
      if (event.worldId != null && !knownWorld) 'world',
      if (event.gamemodeId != null && !knownGamemode) 'gamemode',
    ];
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
        launchIntoGamemode: event.launchIntoGamemode,
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
        statusMessage: discarded.isEmpty
            ? 'Updated world launch settings.'
            : 'Ignored an unknown ${discarded.join(' and ')} — '
                  'launch the game once so TopiaForge can publish its world list.',
      ),
    );
  }

  /// Home's gamemode picker. Selecting a gamemode also points the profile's world at the one that
  /// gamemode's own menu entry declares, so "Launch Zombies" starts in the world Zombies expects
  /// rather than in whatever world happened to be remembered.
  Future<void> _onLaunchGamemodeSelected(
    LaunchGamemodeSelected event,
    Emitter<LauncherState> emit,
  ) async {
    final selected = state.selectedProfile;
    if (selected == null) {
      return;
    }
    final prior = selected.worldSelection;
    final gamemodeId = event.gamemodeId;
    final catalog = state.worldCatalog;

    late final WorldSelection selection;
    if (gamemodeId == null) {
      selection = prior.copyWith(launchIntoGamemode: false);
    } else {
      final entry = catalog.menuEntryFor(gamemodeId);
      final worldId = entry != null && entry.worldId.isNotEmpty
          ? entry.worldId
          : prior.worldId;
      selection = prior.copyWith(
        worldId: worldId,
        gamemodeId: gamemodeId,
        loadMode: catalog.reconcileLoadMode(worldId, prior.loadMode),
        launchIntoGamemode: true,
      );
    }

    final updated = selected.copyWith(worldSelection: selection);
    final profiles = [
      for (final profile in state.profiles)
        if (profile.id == updated.id) updated else profile,
    ];
    await _repository.saveProfiles(profiles, updated.id);
    emit(
      state.copyWith(
        profiles: profiles,
        selectedProfileId: updated.id,
        statusMessage: gamemodeId == null
            ? 'Launch will start the game normally.'
            : 'Launch will start this game mode.',
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
    await _guard(emit, 'Launched TopiaForge.', () async {
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
    await _guard(emit, 'Restarted TopiaForge.', () async {
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
