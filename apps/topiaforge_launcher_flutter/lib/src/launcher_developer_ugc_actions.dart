part of 'launcher_bloc.dart';

extension LauncherDeveloperUgcActions on LauncherBloc {
  Future<void> _onDeveloperUgcPublishToggled(
    DeveloperUgcPublishToggled event,
    Emitter<LauncherState> emit,
  ) async {
    await _withUgcMutation(() async {
      if (_repository.isUgcPublisherRunning) {
        try {
          await _stopUgcPublisher();
        } on Object catch (error) {
          emit(
            state.copyWith(
              errorMessage: error.toString(),
              statusMessage: 'Could not stop the Automerge publisher.',
            ),
          );
          return;
        }
        emit(
          state.copyWith(
            ugcPublisherRunning: false,
            statusMessage: 'Stopped the Automerge publisher.',
          ),
        );
        return;
      }

      final settings = state.ugcLiveSync;
      if (settings.watchFolder.isEmpty) {
        emit(
          state.copyWith(
            statusMessage: 'Set a watch folder before publishing.',
          ),
        );
        return;
      }

      final developer = _developerRepository;
      if (developer != null) {
        try {
          await developer.runSetup();
        } on Object catch (error) {
          emit(
            state.copyWith(
              errorMessage: error.toString(),
              statusMessage:
                  'Could not prepare the Automerge publisher dependencies.',
            ),
          );
          return;
        }
      }

      final bool started;
      try {
        started = await _startUgcPublisher(settings, emit);
      } on Object catch (error) {
        emit(
          state.copyWith(
            ugcPublisherRunning: false,
            errorMessage: error.toString(),
            statusMessage: 'Could not start the Automerge publisher.',
          ),
        );
        return;
      }
      if (started) {
        emit(
          state.copyWith(
            ugcPublisherRunning: true,
            ugcSidecarLog: const [],
            statusMessage:
                'Automerge publisher watching ${settings.watchFolder} — capturing the live document URL…',
          ),
        );
      }
    });
  }

  Future<void> _onDeveloperUgcCleanupRequested(
    DeveloperUgcCleanupRequested event,
    Emitter<LauncherState> emit,
  ) async {
    await _guard(emit, 'UGC live-sync cleaned up.', () async {
      await _withUgcMutation(() async {
        final install = state.gameInstall;
        final workspace = state.developerWorkspace;
        final canCleanProject =
            _developerRepository != null && workspace?.hasProject == true;
        await _stopUgcPublisher(waitForExit: true);
        await _repository.revokeUgcPublisherSession();
        final cleanedSettings = _withoutLiveConnection(state.ugcLiveSync);
        emit(
          state.copyWith(
            ugcPublisherRunning: false,
            ugcSidecarLog: const [],
            ugcCapturedDocumentUrl: '',
            clearUgcStatus: true,
            ugcScenes: const [],
          ),
        );

        UgcLiveSyncCleanupReport? report;
        DeveloperWorkspace? updatedWorkspace = workspace;
        final failures = <String>[];
        if (install != null) {
          try {
            report = await _repository.cleanupUgcLiveSync(
              install,
              cleanedSettings,
            );
          } on Object catch (error) {
            failures.add('game cleanup failed: $error');
          }
        }

        final developer = _developerRepository;
        if (developer != null && workspace?.hasProject == true) {
          try {
            final project = await developer.updateUgcLiveSync(
              workspace!.projectRoot,
              cleanedSettings,
            );
            updatedWorkspace = DeveloperWorkspace(
              projectRoot: workspace.projectRoot,
              project: project,
              lock: workspace.lock,
              issues: workspace.issues,
              generatedPropsPath: workspace.generatedPropsPath,
            );
          } on Object catch (error) {
            failures.add('project cleanup failed: $error');
          }
        }

        emit(
          state.copyWith(
            isBusy: failures.isNotEmpty,
            developerWorkspace: updatedWorkspace,
            statusMessage: report == null
                ? canCleanProject
                      ? 'Cleared launcher and project UGC live state.'
                      : 'Cleared launcher UGC live state.'
                : 'Requested UGC live-sync stop and cleaned up ${report.configPath}.',
          ),
        );
        if (failures.isNotEmpty) {
          throw StateError(failures.join(' '));
        }
      });
    });
  }

  Future<bool> _startUgcPublisher(
    UgcLiveSyncSettings settings,
    Emitter<LauncherState> emit,
  ) async {
    final result = await _repository.startUgcPublisher(settings);
    if (!result.started) {
      emit(
        state.copyWith(
          statusMessage: result.message,
          errorMessage: result.message,
        ),
      );
      return false;
    }
    _ugcPublisherSessionId = result.sessionId;
    return true;
  }

  Future<void> _onDeveloperUgcSidecarOutput(
    DeveloperUgcSidecarOutput event,
    Emitter<LauncherState> emit,
  ) async {
    await _withUgcMutation(() async {
      if (event.publisherSessionId != _ugcPublisherSessionId ||
          !_repository.isUgcPublisherRunning) {
        return;
      }
      final log = [...state.ugcSidecarLog, event.line];
      _trimUgcLog(log);

      const marker = 'TOPIAFORGE_UGC_SESSION ';
      if (!event.line.startsWith(marker)) {
        emit(state.copyWith(ugcSidecarLog: log));
        return;
      }

      final Object? decoded;
      try {
        decoded = jsonDecode(event.line.substring(marker.length));
      } on FormatException {
        _emitInvalidPublisherSession(
          emit,
          log,
          'Publisher returned a malformed session payload.',
        );
        return;
      }
      if (decoded is! Map<String, Object?>) {
        _emitInvalidPublisherSession(
          emit,
          log,
          'Publisher returned a non-object session payload.',
        );
        return;
      }

      var capturedDoc = state.ugcCapturedDocumentUrl;
      var updatedWorkspace = state.developerWorkspace;
      final session = decoded;
      final documentValue = session['documentUrl'];
      final sceneValue = session['sceneId'];
      final documentUrl = documentValue is String ? documentValue : '';
      final sceneId = sceneValue is String ? sceneValue : '';
      if (documentUrl.trim().isEmpty) {
        _emitInvalidPublisherSession(
          emit,
          log,
          'Publisher session did not include a documentUrl.',
        );
        return;
      }
      capturedDoc = documentUrl;
      final current = state.ugcLiveSync;
      final next = UgcLiveSyncSettings(
        transport: 'automerge',
        watchFolder: current.watchFolder,
        editorUrl: '',
        documentUrl: documentUrl,
        syncServerUrl: current.syncServerUrl,
        sceneId: sceneId.isNotEmpty ? sceneId : current.sceneId,
        autoConnectOnStart: _ugcGoLivePending || current.autoConnectOnStart,
        maxSnapshotBytes: current.maxSnapshotBytes,
        debounceMilliseconds: current.debounceMilliseconds,
      );

      final repository = _developerRepository;
      final workspace = state.developerWorkspace;
      try {
        if (repository != null && workspace?.hasProject == true) {
          final project = await repository.updateUgcLiveSync(
            workspace!.projectRoot,
            next,
          );
          updatedWorkspace = _workspaceWithProject(workspace, project);
        }
        final install = state.gameInstall;
        if (install != null) {
          await _repository.deployUgcLiveSyncConfig(install, next);
        }
      } on Object catch (error) {
        _ugcGoLivePending = false;
        log.add('! Could not persist the publisher session: $error');
        _trimUgcLog(log);
        emit(
          state.copyWith(
            ugcSidecarLog: log,
            ugcCapturedDocumentUrl: capturedDoc,
            developerWorkspace: updatedWorkspace,
            errorMessage: error.toString(),
            statusMessage: 'Could not deploy the live UGC session.',
          ),
        );
        return;
      }

      emit(
        state.copyWith(
          ugcSidecarLog: log,
          ugcCapturedDocumentUrl: capturedDoc,
          developerWorkspace: updatedWorkspace,
          clearError: true,
        ),
      );

      if (_ugcGoLivePending && capturedDoc.isNotEmpty) {
        _ugcGoLivePending = false;
        if (!isClosed) add(const GameLaunchRequested());
      }
    });
  }

  Future<void> _onDeveloperUgcPublisherExited(
    DeveloperUgcPublisherExited event,
    Emitter<LauncherState> emit,
  ) async {
    await _withUgcMutation(() async {
      if (event.publisherSessionId != _ugcPublisherSessionId) {
        return;
      }
      final launchWasPending = _ugcGoLivePending;
      _ugcPublisherSessionId = null;
      _ugcGoLivePending = false;
      emit(
        state.copyWith(
          ugcPublisherRunning: false,
          statusMessage: launchWasPending
              ? 'The Automerge publisher exited before creating a live session.'
              : 'The Automerge publisher exited with code ${event.exitCode}.',
        ),
      );
    });
  }

  Future<void> _onDeveloperUgcStatusRefreshed(
    DeveloperUgcStatusRefreshed event,
    Emitter<LauncherState> emit,
  ) async {
    await _withUgcMutation(() async {
      final install = state.gameInstall;
      final status = install == null
          ? null
          : await _repository.readUgcLiveSyncStatus(install);
      final folder = state.ugcLiveSync.watchFolder.isNotEmpty
          ? state.ugcLiveSync.watchFolder
          : (status?.defaultWatchFolder ?? '');
      final inspection = folder.isEmpty
          ? UgcSceneInspectionResult()
          : await _repository.inspectWatchFolderScenes(folder);
      if (inspection.hasBlockingIssues) {
        throw FormatException(
          inspection.issues.firstWhere((issue) => issue.isBlocking).message,
        );
      }
      emit(
        state.copyWith(
          ugcStatus: status,
          clearUgcStatus: status == null,
          ugcScenes: inspection.scenes,
        ),
      );
    });
  }

  Future<void> _onDeveloperUgcGoLive(
    DeveloperUgcGoLive event,
    Emitter<LauncherState> emit,
  ) async {
    await _guard(
      emit,
      'Live session starting.',
      () => _withUgcMutation(() async {
        final install = state.gameInstall;
        final profile = state.selectedProfile;
        if (install == null || profile == null) {
          emit(
            state.copyWith(
              isBusy: false,
              statusMessage: 'Detect a Robotopia install first.',
            ),
          );
          return;
        }
        final base = state.ugcLiveSync;
        final settings = UgcLiveSyncSettings(
          transport: base.transport,
          watchFolder: base.watchFolder,
          editorUrl: base.editorUrl,
          documentUrl: base.documentUrl.isNotEmpty
              ? base.documentUrl
              : state.ugcCapturedDocumentUrl,
          syncServerUrl: base.syncServerUrl,
          sceneId: base.sceneId,
          autoConnectOnStart: true,
          maxSnapshotBytes: base.maxSnapshotBytes,
          debounceMilliseconds: base.debounceMilliseconds,
        );
        final developer = _developerRepository;
        if (developer != null) {
          await developer.runSetup();
        }
        final automerge = settings.transport == 'automerge';
        if (automerge && settings.documentUrl.isEmpty) {
          if (settings.watchFolder.isEmpty) {
            emit(
              state.copyWith(
                isBusy: false,
                statusMessage:
                    'Set a watch folder before going live via Automerge.',
              ),
            );
            return;
          }
          _ugcGoLivePending = true;
          final alreadyRunning = _repository.isUgcPublisherRunning;
          final started = alreadyRunning
              ? true
              : await _startUgcPublisher(settings, emit);
          emit(
            state.copyWith(
              isBusy: false,
              ugcPublisherRunning: started,
              ugcSidecarLog: alreadyRunning ? state.ugcSidecarLog : const [],
              statusMessage: started
                  ? 'Waiting for the publisher — the game will launch once the live document is captured…'
                  : 'Could not start the publisher.',
            ),
          );
          if (!started) {
            _ugcGoLivePending = false;
          }
          return;
        }

        final launchInstall = await _repairRuntimeBeforeLaunchIfNeeded(
          emit,
          install,
        );
        if (launchInstall == null) {
          return;
        }
        await _repository.deployUgcLiveSyncConfig(launchInstall, settings);
        final result = await _repository.launch(launchInstall, profile);
        emit(
          state.copyWith(
            isBusy: false,
            statusMessage: result.started
                ? 'Going live. ${result.message}'
                : 'Could not go live. ${result.message}',
            errorMessage: result.started ? null : result.message,
            clearError: result.started,
          ),
        );
      }),
    );
  }

  Future<void> _stopUgcPublisher({bool waitForExit = false}) async {
    await _repository.stopUgcPublisher(waitForExit: waitForExit);
    _ugcPublisherSessionId = null;
    _ugcGoLivePending = false;
  }

  void _trimUgcLog(List<String> log) {
    while (log.length > 60) {
      log.removeAt(0);
    }
  }

  void _emitInvalidPublisherSession(
    Emitter<LauncherState> emit,
    List<String> log,
    String message,
  ) {
    log.add('! $message');
    _trimUgcLog(log);
    final launchWasPending = _ugcGoLivePending;
    _ugcGoLivePending = false;
    emit(
      state.copyWith(
        ugcSidecarLog: log,
        statusMessage: launchWasPending
            ? '$message Check the publisher output, then try Go Live again.'
            : null,
        errorMessage: launchWasPending ? message : null,
      ),
    );
  }

  UgcLiveSyncSettings _withoutLiveConnection(UgcLiveSyncSettings settings) {
    return UgcLiveSyncSettings(
      transport: settings.transport,
      watchFolder: settings.watchFolder,
      syncServerUrl: settings.syncServerUrl,
      sceneId: settings.sceneId,
      maxSnapshotBytes: settings.maxSnapshotBytes,
      debounceMilliseconds: settings.debounceMilliseconds,
    );
  }

  DeveloperWorkspace _workspaceWithProject(
    DeveloperWorkspace workspace,
    DeveloperProject project,
  ) => DeveloperWorkspace(
    projectRoot: workspace.projectRoot,
    project: project,
    lock: workspace.lock,
    issues: workspace.issues,
    generatedPropsPath: workspace.generatedPropsPath,
  );
}
