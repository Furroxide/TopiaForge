part of 'launcher_bloc.dart';

extension LauncherDeveloperUgcActions on LauncherBloc {
  Future<void> _onDeveloperUgcPublishToggled(
    DeveloperUgcPublishToggled event,
    Emitter<LauncherState> emit,
  ) async {
    if (_ugcPublisher != null) {
      _cancelUgcPublisherStreams();
      _ugcPublisher!.kill();
      _ugcPublisher = null;
      _ugcGoLivePending = false;
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
        state.copyWith(statusMessage: 'Set a watch folder before publishing.'),
      );
      return;
    }

    final started = await _startUgcPublisher(settings, emit);
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
  }

  Future<bool> _startUgcPublisher(
    UgcLiveSyncSettings settings,
    Emitter<LauncherState> emit,
  ) async {
    final sidecar = _findSidecar();
    if (sidecar == null) {
      emit(
        state.copyWith(
          statusMessage:
              'Automerge sidecar not found. Run `robotopia ugc watch` from the repo instead.',
        ),
      );
      return false;
    }

    try {
      Directory(settings.watchFolder).createSync(recursive: true);
      final args = <String>[
        sidecar,
        '--watch',
        settings.watchFolder,
        '--sync',
        settings.syncServerUrl,
        '--session-file',
        '${_repository.dataRoot}/ugc-session.json',
      ];
      if (settings.documentUrl.isNotEmpty) {
        args.addAll(['--doc', settings.documentUrl]);
      }
      if (settings.sceneId.isNotEmpty) {
        args.addAll(['--scene', settings.sceneId]);
      }
      final process = await Process.start('node', args, runInShell: true);
      _ugcPublisher = process;
      _ugcStdoutSub = process.stdout
          .transform(utf8.decoder)
          .transform(const LineSplitter())
          .listen((line) {
            if (!isClosed) add(DeveloperUgcSidecarOutput(line));
          });
      _ugcStderrSub = process.stderr
          .transform(utf8.decoder)
          .transform(const LineSplitter())
          .listen((line) {
            if (!isClosed) add(DeveloperUgcSidecarOutput('! $line'));
          });
      unawaited(
        process.exitCode.then((_) {
          _cancelUgcPublisherStreams();
          _ugcPublisher = null;
          _ugcGoLivePending = false;
        }),
      );
      return true;
    } on ProcessException catch (error) {
      emit(
        state.copyWith(
          statusMessage:
              'Could not start Node (${error.message}). Install Node 20+ to publish via Automerge.',
        ),
      );
      return false;
    }
  }

  Future<void> _onDeveloperUgcSidecarOutput(
    DeveloperUgcSidecarOutput event,
    Emitter<LauncherState> emit,
  ) async {
    final log = [...state.ugcSidecarLog, event.line];
    while (log.length > 60) {
      log.removeAt(0);
    }

    const marker = 'ROBOTOPIA_UGC_SESSION ';
    if (!event.line.startsWith(marker)) {
      emit(state.copyWith(ugcSidecarLog: log));
      return;
    }

    String capturedDoc = state.ugcCapturedDocumentUrl;
    try {
      final session =
          jsonDecode(event.line.substring(marker.length))
              as Map<String, Object?>;
      final documentUrl = (session['documentUrl'] as String?) ?? '';
      final sceneId = (session['sceneId'] as String?) ?? '';
      if (documentUrl.isNotEmpty) {
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
        if (repository != null && workspace?.hasProject == true) {
          await repository.updateUgcLiveSync(workspace!.projectRoot, next);
        }
        final install = state.gameInstall;
        if (install != null) {
          await _repository.deployUgcLiveSyncConfig(install, next);
        }
      }
    } on Object {
      // A malformed session line is non-fatal; keep streaming.
    }

    emit(
      state.copyWith(ugcSidecarLog: log, ugcCapturedDocumentUrl: capturedDoc),
    );

    if (_ugcGoLivePending && capturedDoc.isNotEmpty) {
      _ugcGoLivePending = false;
      if (!isClosed) add(const GameLaunchRequested());
    }
  }

  Future<void> _onDeveloperUgcStatusRefreshed(
    DeveloperUgcStatusRefreshed event,
    Emitter<LauncherState> emit,
  ) async {
    final install = state.gameInstall;
    final status = install == null
        ? null
        : await _repository.readUgcLiveSyncStatus(install);
    final folder = state.ugcLiveSync.watchFolder.isNotEmpty
        ? state.ugcLiveSync.watchFolder
        : (status?.defaultWatchFolder ?? '');
    final scenes = folder.isEmpty
        ? const <UgcSceneRef>[]
        : await _repository.listWatchFolderScenes(folder);
    emit(state.copyWith(ugcStatus: status, ugcScenes: scenes));
  }

  Future<void> _onDeveloperUgcGoLive(
    DeveloperUgcGoLive event,
    Emitter<LauncherState> emit,
  ) async {
    final install = state.gameInstall;
    final profile = state.selectedProfile;
    if (install == null || profile == null) {
      emit(state.copyWith(statusMessage: 'Detect a Robotopia install first.'));
      return;
    }

    final base = state.ugcLiveSync;
    final settings = UgcLiveSyncSettings(
      transport: base.transport,
      watchFolder: base.watchFolder,
      editorUrl: base.editorUrl,
      documentUrl: base.documentUrl,
      syncServerUrl: base.syncServerUrl,
      sceneId: base.sceneId,
      autoConnectOnStart: true,
      maxSnapshotBytes: base.maxSnapshotBytes,
      debounceMilliseconds: base.debounceMilliseconds,
    );

    await _guard(emit, 'Live session starting.', () async {
      final developer = _developerRepository;
      if (developer != null) {
        await developer.runSetup();
      }
      if (settings.watchFolder.isNotEmpty) {
        Directory(settings.watchFolder).createSync(recursive: true);
      }

      final automerge = settings.transport == 'automerge';
      if (automerge && _ugcPublisher == null) {
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
        final started = await _startUgcPublisher(settings, emit);
        emit(
          state.copyWith(
            isBusy: false,
            ugcPublisherRunning: started,
            ugcSidecarLog: const [],
            statusMessage: started
                ? 'Publisher starting — the game will launch once the live document is captured…'
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
          statusMessage: 'Going live. ${result.message}',
        ),
      );
    });
  }
}

String? _findSidecar() {
  var dir = Directory.current.absolute;
  while (true) {
    final candidate = File('${dir.path}/tools/ugc-automerge-sidecar/index.mjs');
    if (candidate.existsSync()) {
      return candidate.path;
    }
    final parent = dir.parent;
    if (parent.path == dir.path) {
      return null;
    }
    dir = parent;
  }
}
