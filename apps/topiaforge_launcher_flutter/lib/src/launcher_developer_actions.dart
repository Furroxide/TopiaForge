part of 'launcher_bloc.dart';

extension LauncherDeveloperActions on LauncherBloc {
  Future<void> _onDeveloperWorkspaceRefreshed(
    DeveloperWorkspaceRefreshed event,
    Emitter<LauncherState> emit,
  ) async {
    final repository = _developerRepository;
    if (repository == null) {
      emit(state.copyWith(statusMessage: 'Developer tools are unavailable.'));
      return;
    }
    await _guard(emit, 'Developer workspace refreshed.', () async {
      final workspace = await repository.loadDeveloperWorkspace();
      emit(
        state.copyWith(
          isBusy: false,
          developerWorkspace: workspace,
          statusMessage: workspace.hasProject
              ? 'Developer project ready.'
              : 'No developer project found.',
        ),
      );
    });
  }

  Future<void> _onDeveloperProjectResolved(
    DeveloperProjectResolved event,
    Emitter<LauncherState> emit,
  ) async {
    final repository = _developerRepository;
    final workspace = state.developerWorkspace;
    if (repository == null || workspace?.hasProject != true) {
      emit(state.copyWith(statusMessage: 'Open a developer project first.'));
      return;
    }
    await _guard(emit, 'Developer project restored.', () async {
      final updated = await repository.resolveDeveloperProject(
        workspace!.projectRoot,
      );
      emit(
        state.copyWith(
          isBusy: false,
          developerWorkspace: updated,
          statusMessage: updated.hasBlockingIssues
              ? 'Resolve blocking developer project issues.'
              : 'Developer project restored.',
        ),
      );
    });
  }

  Future<void> _onDeveloperDoctorRequested(
    DeveloperDoctorRequested event,
    Emitter<LauncherState> emit,
  ) async {
    final repository = _developerRepository;
    if (repository == null) {
      emit(state.copyWith(statusMessage: 'Developer tools are unavailable.'));
      return;
    }
    await _guard(emit, 'Developer doctor complete.', () async {
      final report = await repository.runDoctor(
        projectPath: state.developerWorkspace?.projectRoot,
      );
      emit(
        state.copyWith(
          isBusy: false,
          developerDoctor: report,
          statusMessage: report.ok
              ? 'Developer environment looks ready.'
              : 'Developer environment has issues.',
        ),
      );
    });
  }

  Future<void> _onDeveloperSampleProjectCreated(
    DeveloperSampleProjectCreated event,
    Emitter<LauncherState> emit,
  ) async {
    final repository = _developerRepository;
    if (repository == null) {
      emit(state.copyWith(statusMessage: 'Developer tools are unavailable.'));
      return;
    }
    await _guard(emit, 'Created developer project.', () async {
      final workspace = await repository.createModProject(
        parentDirectory: repository.developerDataRoot,
        id: 'sample.creator_mod',
        name: 'Sample Creator Mod',
        includeUnityCompanion: true,
      );
      emit(
        state.copyWith(
          isBusy: false,
          developerWorkspace: workspace,
          statusMessage: 'Created ${workspace.project!.name}.',
        ),
      );
      add(const DeveloperProjectsRefreshed());
    });
  }

  Future<void> _onDeveloperUgcSettingsSaved(
    DeveloperUgcSettingsSaved event,
    Emitter<LauncherState> emit,
  ) async {
    await _guard(
      emit,
      'Saved UGC live-sync settings.',
      () => _withUgcMutation(() async {
        final repository = _developerRepository;
        final workspace = state.developerWorkspace;
        if (repository == null || workspace?.hasProject != true) {
          emit(
            state.copyWith(
              isBusy: false,
              statusMessage: 'Open a developer project first.',
            ),
          );
          return;
        }
        final current = workspace!.project!.unityCompanion.liveSync;
        final next = UgcLiveSyncSettings(
          transport: event.transport ?? current.transport,
          watchFolder: event.watchFolder ?? current.watchFolder,
          editorUrl: event.editorUrl ?? current.editorUrl,
          documentUrl: event.documentUrl ?? current.documentUrl,
          syncServerUrl: event.syncServerUrl ?? current.syncServerUrl,
          sceneId: event.sceneId ?? current.sceneId,
          autoConnectOnStart:
              event.autoConnectOnStart ?? current.autoConnectOnStart,
          maxSnapshotBytes: current.maxSnapshotBytes,
          debounceMilliseconds: current.debounceMilliseconds,
        );
        final project = await repository.updateUgcLiveSync(
          workspace.projectRoot,
          next,
        );
        var updated = DeveloperWorkspace(
          projectRoot: workspace.projectRoot,
          project: project,
          lock: workspace.lock,
          issues: workspace.issues,
          generatedPropsPath: workspace.generatedPropsPath,
        );
        emit(
          state.copyWith(
            developerWorkspace: updated,
            statusMessage: 'Saved UGC live-sync settings.',
          ),
        );
        updated = await repository.loadDeveloperWorkspace(
          projectPath: workspace.projectRoot,
        );
        emit(state.copyWith(developerWorkspace: updated));
        var message = 'Saved UGC live-sync settings.';
        final install = state.gameInstall;
        if (install != null) {
          final path = await _repository.deployUgcLiveSyncConfig(install, next);
          message = 'Saved settings and deployed config to $path.';
        }
        emit(
          state.copyWith(
            isBusy: false,
            developerWorkspace: updated,
            statusMessage: message,
          ),
        );
      }),
    );
  }

  Future<void> _onDeveloperUgcConfigDeployed(
    DeveloperUgcConfigDeployed event,
    Emitter<LauncherState> emit,
  ) async {
    await _guard(
      emit,
      'Deployed UGC config to the game.',
      () => _withUgcMutation(() async {
        final install = state.gameInstall;
        if (install == null) {
          emit(
            state.copyWith(
              isBusy: false,
              statusMessage: 'Detect a Robotopia install first.',
            ),
          );
          return;
        }
        final path = await _repository.deployUgcLiveSyncConfig(
          install,
          state.ugcLiveSync,
        );
        emit(
          state.copyWith(
            isBusy: false,
            statusMessage: 'Deployed UGC config to $path.',
          ),
        );
      }),
    );
  }

  Future<void> _onDeveloperWatchFolderOpened(
    DeveloperWatchFolderOpened event,
    Emitter<LauncherState> emit,
  ) async {
    final folder = state.ugcLiveSync.watchFolder;
    if (folder.isEmpty) {
      emit(state.copyWith(statusMessage: 'Set a watch folder first.'));
      return;
    }
    await _repository.ensureDirectory(folder);
    await _repository.openPath(folder);
    emit(state.copyWith(statusMessage: 'Opened $folder.'));
  }
}
