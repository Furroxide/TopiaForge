part of 'launcher_bloc.dart';

extension LauncherLaunchActions on LauncherBloc {
  Future<bool> _persistProfiles(
    List<LauncherProfile> profiles,
    String selectedId,
    Emitter<LauncherState> emit,
    String message,
  ) async {
    List<LauncherProfile> saved;
    try {
      saved = await _repository.saveProfiles(profiles, selectedId);
    } on Object catch (error) {
      emit(
        state.copyWith(
          isBusy: false,
          statusMessage: 'Profile could not be saved.',
          statusSeverity: IssueSeverity.error,
          errorMessage: error.toString(),
        ),
      );
      return false;
    }
    _activeLaunchRequest = null;
    emit(
      state.copyWith(
        profiles: saved,
        selectedProfileId: selectedId,
        previewsByProfile: const {},
        clearLaunchActivity: true,
        statusMessage: message,
      ),
    );
    _queueLaunchPreviews();
    return true;
  }

  void _queueLaunchPreviews() {
    final generation = ++_launchPreviewGeneration;
    final install = state.gameInstall;
    if (install == null) return;
    for (final profile in state.profiles) {
      unawaited(
        _repository
            .previewLaunch(install, profile)
            .then(
              (preview) {
                if (!isClosed) {
                  add(
                    LaunchPreviewUpdated(
                      profileId: profile.id,
                      revision: profile.revision,
                      installPath: install.path,
                      generation: generation,
                      preview: preview,
                    ),
                  );
                }
              },
              onError: (Object error, StackTrace stack) {
                if (!isClosed) {
                  add(
                    LaunchPreviewUpdated(
                      profileId: profile.id,
                      revision: profile.revision,
                      installPath: install.path,
                      generation: generation,
                      error: 'Launch options could not be loaded: $error',
                    ),
                  );
                }
              },
            ),
      );
    }
  }

  Future<void> _onLaunchPreviewUpdated(
    LaunchPreviewUpdated event,
    Emitter<LauncherState> emit,
  ) async {
    if (event.generation != _launchPreviewGeneration ||
        event.installPath != state.gameInstall?.path ||
        !state.profiles.any(
          (profile) =>
              profile.id == event.profileId &&
              profile.revision == event.revision,
        )) {
      return;
    }
    final preview = event.preview;
    if (preview != null &&
        preview.profileId == event.profileId &&
        preview.profileRevision == event.revision) {
      emit(
        state.copyWith(
          previewsByProfile: {
            ...state.previewsByProfile,
            event.profileId: preview,
          },
        ),
      );
    } else if (event.profileId == state.selectedProfileId) {
      emit(
        state.copyWith(
          errorMessage:
              event.error ?? 'Launch options did not match this profile.',
          statusMessage: 'Launch options unavailable.',
        ),
      );
    }
  }

  Future<void> _onLaunchSelectionChanged(
    LaunchSelectionChanged event,
    Emitter<LauncherState> emit,
  ) => _guard(
    emit,
    'Updated launch selection.',
    () => _applyLaunchSelection(event, emit),
  );

  Future<void> _applyLaunchSelection(
    LaunchSelectionChanged event,
    Emitter<LauncherState> emit,
  ) async {
    final profile = state.selectedProfile;
    final install = state.gameInstall;
    if (profile == null || install == null) return;
    if (event.selection.kind == LaunchSelectionKind.unresolvedLegacy) return;
    final preview = await _repository.previewLaunch(
      install,
      profile,
      selectionOverride: event.selection,
    );
    final request = event.selection.request;
    if (request != null) {
      final validTarget = preview.targets.any(
        (target) => target.id == request.targetId && target.selectable,
      );
      final validWorld =
          request.worldOverride == null ||
          (preview.allowWorldOverride &&
              preview.worlds.any(
                (world) => world.id == request.worldOverride && world.available,
              ));
      final validTransition =
          request.transitionOverride == null ||
          (preview.allowTransitionOverride &&
              preview.transitions.contains(request.transitionOverride));
      if (!validTarget || !validWorld || !validTransition) {
        emit(
          state.copyWith(
            isBusy: false,
            statusSeverity: IssueSeverity.error,
            errorMessage:
                'That launch choice is unavailable or is not permitted by this target.',
            statusMessage:
                'Choose an available launch target and its permitted options.',
          ),
        );
        return;
      }
    }
    await _saveUpdatedProfile(
      profile.copyWith(launchSelection: event.selection),
      emit,
      event.selection.kind == LaunchSelectionKind.mainMenu
          ? 'Launch will open the main menu.'
          : 'Updated launch target.',
    );
  }

  Future<void> _onGameLaunchRequested(
    GameLaunchRequested event,
    Emitter<LauncherState> emit,
  ) => _launchSelectedProfile(emit, restart: false);

  Future<void> _onGameRestartRequested(
    GameRestartRequested event,
    Emitter<LauncherState> emit,
  ) => _launchSelectedProfile(emit, restart: true);

  Future<void> _launchSelectedProfile(
    Emitter<LauncherState> emit, {
    required bool restart,
    LauncherProfile? selected,
  }) async {
    final install = state.gameInstall;
    final profile = selected ?? state.selectedProfile;
    if (install == null || profile == null) return;
    await _guard(
      emit,
      'Game process started; session start is unconfirmed.',
      () async {
        final launchInstall = await _repairRuntimeBeforeLaunchIfNeeded(
          emit,
          install,
        );
        if (launchInstall == null) return;
        final result = restart
            ? await _repository.restart(launchInstall, profile)
            : await _repository.launch(launchInstall, profile);
        final receipt = result.latestActivity;
        _launchReceipt =
            result.processStarted &&
                receipt != null &&
                receipt.requestId == result.requestId &&
                receipt.profileId == profile.id &&
                receipt.profileRevision == profile.revision
            ? receipt
            : null;
        _activeLaunchRequest = _launchReceipt?.requestId;
        emit(_launchResultState(result));
        final latest = result.latestActivity;
        if (latest != null) {
          await _onLaunchActivityUpdated(LaunchActivityUpdated(latest), emit);
        }
      },
    );
  }

  LauncherState _launchResultState(LaunchResult result) {
    final failures = [
      ...result.issues
          .where((issue) => issue.isBlocking)
          .map((issue) => issue.message),
      ...result.blocks.map((block) => block.message),
    ];
    final message = result.processStarted
        ? 'Game process started; session start is unconfirmed.'
        : [
            result.message,
            ...failures,
          ].where((text) => text.isNotEmpty).join(' ');
    return state.copyWith(
      isBusy: false,
      clearLaunchActivity: true,
      statusMessage: message,
      statusSeverity: result.processStarted
          ? IssueSeverity.warning
          : IssueSeverity.error,
      errorMessage: result.processStarted ? null : message,
      clearError: result.processStarted,
    );
  }

  Future<void> _onLaunchActivityUpdated(
    LaunchActivityUpdated event,
    Emitter<LauncherState> emit,
  ) async {
    final activity = event.activity;
    final profile = state.selectedProfile;
    if (activity.requestId != _activeLaunchRequest ||
        profile == null ||
        activity.profileId != profile.id ||
        activity.profileRevision != profile.revision) {
      return;
    }
    final receipt = _launchReceipt;
    if (receipt == null ||
        activity.installIdentity != receipt.installIdentity ||
        activity.packageDigest != receipt.packageDigest ||
        activity.command != receipt.command) {
      return;
    }
    final expectedProcess = receipt.process;
    final process = activity.process;
    if (expectedProcess != null &&
        (process == null ||
            process.pid != expectedProcess.pid ||
            process.startTimeUtc != expectedProcess.startTimeUtc ||
            process.executablePath != expectedProcess.executablePath ||
            process.nativeStartToken != expectedProcess.nativeStartToken)) {
      return;
    }
    final current = state.launchActivity;
    if (current != null &&
        (activity.installIdentity != current.installIdentity ||
            activity.packageDigest != current.packageDigest ||
            activity.command != current.command)) {
      return;
    }
    var accepted = current ?? activity;
    if (activity.progress != null) {
      accepted = accepted.applyProgress(activity.progress!);
    }
    if (activity.launchOutcome != null) {
      accepted = accepted.applyOutcome(activity.launchOutcome!);
    }
    if (activity.sessionOutcome != null) {
      accepted = accepted.applyOutcome(activity.sessionOutcome!);
    }
    if (!accepted.acknowledged && activity.sequence >= accepted.sequence) {
      accepted = accepted.copyWith(unconfirmed: activity.unconfirmed);
    }
    accepted = accepted.copyWith(processExited: activity.processExited);
    final outcome = accepted.sessionOutcome ?? accepted.launchOutcome;
    final failed =
        outcome?.status == 'failed' || outcome?.status == 'cancelled';
    final message = _launchActivityMessage(accepted);
    emit(
      state.copyWith(
        launchActivity: accepted,
        statusMessage: message,
        statusSeverity: failed
            ? IssueSeverity.error
            : accepted.unconfirmed ||
                  (accepted.processExited &&
                      accepted.command == 'launch-target' &&
                      accepted.sessionOutcome == null)
            ? IssueSeverity.warning
            : IssueSeverity.info,
        errorMessage: failed ? message : null,
        clearError: !failed,
      ),
    );
  }

  Future<void> _onLaunchActivityMonitorFailed(
    LaunchActivityMonitorFailed event,
    Emitter<LauncherState> emit,
  ) async {
    if (_activeLaunchRequest == null) return;
    emit(
      state.copyWith(
        statusMessage: '${event.message} Session status is unconfirmed.',
        statusSeverity: IssueSeverity.warning,
      ),
    );
  }
}

String _launchActivityMessage(LaunchActivity activity) {
  final outcome = activity.sessionOutcome ?? activity.launchOutcome;
  if (outcome?.status == 'failed') {
    return [
      outcome!.kind == 'session'
          ? 'Session ended with errors.'
          : 'Session startup failed.',
      ...outcome.blocks.map((block) => block.message),
      if (outcome.error != null) outcome.error!.message,
    ].join(' ');
  }
  if (outcome?.status == 'cancelled') return 'Launch request was cancelled.';
  if (outcome?.kind == 'session') return 'Session ended.';
  if (activity.command == 'main-menu' && activity.acknowledged) {
    return activity.processExited
        ? 'Main menu confirmed. Game process ended.'
        : 'Main menu confirmed.';
  }
  if (activity.processExited) {
    return 'Game process ended; session outcome is unconfirmed.';
  }
  if (activity.progress?.phase == 'stopping') return 'Stopping session…';
  if (activity.sessionStarted) return 'Session is running.';
  return switch (activity.progress?.phase) {
    'preparing' => 'Preparing session…',
    'loading-world' => 'Loading world…',
    'starting-mode' => 'Starting gamemode…',
    'running' => 'Runtime reports Running; awaiting confirmation…',
    'idle' => 'Runtime is idle; awaiting confirmation…',
    _ => 'Game process started; session start is unconfirmed.',
  };
}
