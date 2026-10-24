part of 'launcher_bloc.dart';

/// Settings → Multiplayer: the player's preferences, the rule that the ones
/// the game can change are read-only while it runs, and the rounds that take
/// in what changed in game and bring the game's copy up to date.
///
/// Reading and saving the preferences, checking the game and each round run
/// beside the event lane (`unawaited(...).then(add(...))`), so a slow disk or
/// process list never holds the launcher up. Nothing here shows or logs an
/// error's own text: every message is a plain sentence of this file's, so no
/// address can reach the status bar or the launcher's log.
extension LauncherMultiplayerSettingsActions on LauncherBloc {
  FutureOr<void> _dispatchMultiplayerSettingsEvent(
    MultiplayerSettingsEvent event,
    Emitter<LauncherState> emit,
  ) => switch (event) {
    MultiplayerPreferencesRefreshRequested() => _onPreferencesRefresh(
      event,
      emit,
    ),
    MultiplayerPreferencesEdited() => _onPreferencesEdited(event, emit),
    MultiplayerGameCheckRequested() => _onGameCheckRequested(emit),
    MultiplayerPreferencesLoaded() => _onPreferencesLoaded(event, emit),
    MultiplayerPreferencesChanged() => _onPreferencesChanged(event, emit),
    MultiplayerGameChecked() => _onGameChecked(event, emit),
    MultiplayerPreferencesRoundFinished() => _onRoundFinished(event, emit),
  };

  MultiplayerSettingsView get _settingsView => state.multiplayer.settings;

  void _showSettings(
    Emitter<LauncherState> emit,
    MultiplayerSettingsView view, {
    String? status,
    IssueSeverity? severity,
  }) => emit(
    state.copyWith(
      multiplayer: state.multiplayer.copyWith(settings: view),
      statusMessage: status,
      statusSeverity: severity,
    ),
  );

  /// Runs [work] beside the event lane and adds its answer, unless the bloc
  /// has closed by then. [failed] answers when [work] throws, whatever it
  /// threw, so no error's own text travels further.
  void _beside<T>(
    Future<T> work,
    LauncherEvent Function(T value) answer,
    LauncherEvent Function() failed,
  ) {
    unawaited(
      work.then(
        (value) {
          if (!isClosed) add(answer(value));
        },
        onError: (Object _) {
          if (!isClosed) add(failed());
        },
      ),
    );
  }

  Future<void> _onPreferencesRefresh(
    MultiplayerPreferencesRefreshRequested event,
    Emitter<LauncherState> emit,
  ) async {
    final gateway = _multiplayer;
    if (gateway == null) return;
    final generation = _multiplayerWork.read = ++_multiplayerWork.preferences;
    final view = _settingsView;
    _showSettings(
      emit,
      view.copyWith(
        phase: view.phase == MultiplayerPreferencesPhase.unreadable
            ? MultiplayerPreferencesPhase.loading
            : null,
        clearProblem: true,
      ),
    );
    _beside(
      gateway.loadPreferences(),
      (preferences) => MultiplayerPreferencesLoaded(generation, preferences),
      () => MultiplayerPreferencesLoaded(generation, _unreadablePreferences()),
    );
    if (event.checkGame) add(const MultiplayerGameCheckRequested());
  }

  Future<void> _onPreferencesLoaded(
    MultiplayerPreferencesLoaded event,
    Emitter<LauncherState> emit,
  ) async {
    // A newer change or round answers for the preferences, except before
    // anything shows: a round that started before the newest read answered
    // may fail or be cancelled, and that read is then all there is to show.
    final waiting =
        event.generation == _multiplayerWork.read &&
        _settingsView.phase == MultiplayerPreferencesPhase.loading;
    if (event.generation != _multiplayerWork.preferences && !waiting) return;
    _showPreferences(emit, event.preferences, generation: event.generation);
  }

  /// Shows [preferences], read by the work of [generation], or says they
  /// could not be read. Work that may have read the store before the last
  /// link choice was written shows that choice instead of the one it read.
  void _showPreferences(
    Emitter<LauncherState> emit,
    MultiplayerPreferences preferences, {
    required int generation,
    String? status,
    IssueSeverity? severity,
  }) {
    if (!preferences.readable) {
      _showSettings(
        emit,
        _settingsView.copyWith(
          phase: MultiplayerPreferencesPhase.unreadable,
          clearPreferences: true,
        ),
        status: _unreadableStatus,
        severity: IssueSeverity.warning,
      );
      return;
    }
    final choice = _multiplayerWork.linkChoice;
    _showSettings(
      emit,
      _settingsView.copyWith(
        phase: MultiplayerPreferencesPhase.ready,
        preferences:
            choice != null && generation <= _multiplayerWork.linkChosenAt
            ? _withLinkHandling(preferences, choice)
            : preferences,
      ),
      status: status,
      severity: severity,
    );
  }

  Future<void> _onPreferencesEdited(
    MultiplayerPreferencesEdited event,
    Emitter<LauncherState> emit,
  ) async {
    final gateway = _multiplayer;
    final view = _settingsView;
    final change = event.change;
    if (gateway == null || view.phase != MultiplayerPreferencesPhase.ready) {
      return;
    }
    if (change.sharedWithGame && view.lock != MultiplayerPreferencesLock.none) {
      _showSettings(
        emit,
        view,
        status: _lockedStatus(view.lock),
        severity: IssueSeverity.warning,
      );
      return;
    }
    if (view.saving || (change.sharedWithGame && view.syncing)) {
      _showSettings(
        emit,
        view,
        status: 'A change is still being saved. Try again in a moment.',
        severity: IssueSeverity.warning,
      );
      return;
    }
    final generation = ++_multiplayerWork.preferences;
    // The game may have started since the last check, so a change it could
    // also make asks again first. That check is then the newest, and the
    // answer to any check asked before it is stale.
    final install = change.sharedWithGame ? state.gameInstall : null;
    final gameCheck = install == null
        ? _multiplayerWork.game
        : ++_multiplayerWork.game;
    _showSettings(emit, view.copyWith(saving: true, clearProblem: true));
    Future<MultiplayerChangeOutcome> save() async {
      if (install != null) {
        final running = await gateway.gameRunState(install);
        if (running != GameRunState.notRunning) {
          return MultiplayerChangeLocked(running);
        }
      }
      try {
        return MultiplayerChangeSaved(await gateway.changePreferences(change));
      } on MultiplayerPreferencesException catch (error) {
        return MultiplayerChangeFailed(
          error.unreadable ? _unreadableStatus : _notSaved,
          unreadable: error.unreadable,
        );
      }
    }

    _beside(
      save(),
      (outcome) => MultiplayerPreferencesChanged(
        generation,
        change,
        outcome,
        gameCheck: gameCheck,
      ),
      () => MultiplayerPreferencesChanged(
        generation,
        change,
        const MultiplayerChangeFailed(_notSaved),
        gameCheck: gameCheck,
      ),
    );
  }

  Future<void> _onPreferencesChanged(
    MultiplayerPreferencesChanged event,
    Emitter<LauncherState> emit,
  ) async {
    final current = event.generation == _multiplayerWork.preferences;
    final view = _settingsView.copyWith(saving: false);
    switch (event.outcome) {
      case MultiplayerChangeSaved(:final preferences):
        final shared = event.change.sharedWithGame;
        // The game reads its copy when it starts, so the change is due to
        // reach it with a round.
        if (shared) _multiplayerWork.roundDue = true;
        if (!current) {
          // A newer read or round brings the preferences after this change.
          _showSettings(emit, view, status: 'Multiplayer settings saved.');
          if (shared) add(const MultiplayerGameCheckRequested());
          return;
        }
        _showSettings(emit, view);
        _showPreferences(
          emit,
          preferences,
          generation: event.generation,
          status: _savedStatus(event),
        );
        if (!shared) return;
        // The change's own check is still the newest word on the game, so the
        // round runs now. A check asked since may say the game runs: a fresh
        // one decides, and runs the round once the game is closed.
        if (event.gameCheck == _multiplayerWork.game) {
          _startRound(emit);
        } else {
          add(const MultiplayerGameCheckRequested());
        }
      case MultiplayerChangeLocked(:final state):
        _multiplayerWork.roundDue = true;
        final lock = _lockFor(state);
        // The change is refused either way, but only the newest check says
        // whether the game runs: the answer to a check asked since the
        // change's own stands, as the change's own outranks an older one.
        _showSettings(
          emit,
          event.gameCheck == _multiplayerWork.game
              ? view.copyWith(lock: lock)
              : view,
          status: _lockedStatus(lock),
          severity: IssueSeverity.warning,
        );
      case MultiplayerChangeFailed(:final message, :final unreadable):
        _showSettings(
          emit,
          unreadable
              ? view.copyWith(
                  phase: MultiplayerPreferencesPhase.unreadable,
                  clearPreferences: true,
                )
              : view.copyWith(problem: message),
          status: message,
          severity: IssueSeverity.error,
        );
    }
  }

  String _savedStatus(MultiplayerPreferencesChanged event) {
    final change = event.change;
    if (change.voiceConsent == false) {
      return 'Voice chat consent withdrawn. Voice chat is off.';
    }
    if (change.lanDiscovery == false) {
      return 'LAN discovery is off. TopiaForge no longer listens for local '
          'sessions.';
    }
    return 'Multiplayer settings saved.';
  }

  /// Saved by link handling: the switch shows the stored choice at once, and
  /// the registration's own status follows it.
  void _linkHandlingSaved(bool enabled, Emitter<LauncherState> emit) {
    _multiplayerWork
      ..linkChoice = enabled
      ..linkChosenAt = _multiplayerWork.preferences;
    final preferences = _settingsView.preferences;
    if (preferences == null) return;
    _showSettings(
      emit,
      _settingsView.copyWith(
        preferences: _withLinkHandling(preferences, enabled),
      ),
    );
  }

  Future<void> _onGameCheckRequested(Emitter<LauncherState> emit) async {
    final gateway = _multiplayer;
    if (gateway == null) return;
    final generation = ++_multiplayerWork.game;
    final install = state.gameInstall;
    if (install == null) {
      // No install: no game runs from one, and no copy is kept in step.
      _showSettings(
        emit,
        _settingsView.copyWith(lock: MultiplayerPreferencesLock.none),
      );
      return;
    }
    // The process list decides, for a game this launcher started too: it
    // also sees a game started some other way, and an exit the launch
    // monitor may not have reported.
    _beside(
      gateway.gameRunState(install),
      (running) => MultiplayerGameChecked(generation, install.path, running),
      () => MultiplayerGameChecked(
        generation,
        install.path,
        GameRunState.unknown,
      ),
    );
  }

  Future<void> _onGameChecked(
    MultiplayerGameChecked event,
    Emitter<LauncherState> emit,
  ) async {
    if (event.generation != _multiplayerWork.game ||
        event.installPath != state.gameInstall?.path) {
      return;
    }
    final lock = _lockFor(event.state);
    if (lock != MultiplayerPreferencesLock.none) {
      // Nothing is taken in while the game runs; once it is closed, a round
      // takes in what the player changed in it.
      _multiplayerWork.roundDue = true;
      _multiplayerWork.round?.cancel();
    }
    _showSettings(emit, _settingsView.copyWith(lock: lock));
    if (lock == MultiplayerPreferencesLock.none &&
        _multiplayerWork.roundDue &&
        _settingsView.phase != MultiplayerPreferencesPhase.unreadable) {
      _startRound(emit);
    }
  }

  /// Starts a round with the game at the selected install.
  void _startRound(Emitter<LauncherState> emit) {
    final gateway = _multiplayer;
    final install = state.gameInstall;
    if (gateway == null || install == null) return;
    _multiplayerWork.round?.cancel();
    final token = _multiplayerWork.round = CancellationToken();
    final generation = ++_multiplayerWork.preferences;
    _showSettings(emit, _settingsView.copyWith(syncing: true));
    _beside(
      gateway.syncWithGame(install, cancellation: token),
      (round) => MultiplayerPreferencesRoundFinished(generation, token, round),
      () => MultiplayerPreferencesRoundFinished(
        generation,
        token,
        MultiplayerPreferencesRoundFailed(
          _unreadablePreferences(),
          MultiplayerRoundProblem.gameCopyNotWritten,
        ),
      ),
    );
  }

  Future<void> _onRoundFinished(
    MultiplayerPreferencesRoundFinished event,
    Emitter<LauncherState> emit,
  ) async {
    if (identical(event.token, _multiplayerWork.round)) {
      _multiplayerWork.round = null;
      _showSettings(emit, _settingsView.copyWith(syncing: false));
    }
    if (event.generation != _multiplayerWork.preferences ||
        event.token.isCancelled) {
      return;
    }
    switch (event.round) {
      case MultiplayerPreferencesSynced(
        :final preferences,
        :final changedInGame,
      ):
        _multiplayerWork.roundDue = false;
        _showSettings(emit, _settingsView.copyWith(clearProblem: true));
        _showPreferences(
          emit,
          preferences,
          generation: event.generation,
          status: changedInGame
              ? 'Took in the multiplayer settings you changed in Robotopia.'
              : null,
        );
      case MultiplayerPreferencesRoundSkipped(:final preferences):
        _showPreferences(emit, preferences, generation: event.generation);
      case MultiplayerPreferencesRoundFailed(:final problem):
        final message = switch (problem) {
          MultiplayerRoundProblem.preferencesNotSaved => _notSaved,
          MultiplayerRoundProblem.gameCopyNotWritten => _gameCopy,
        };
        _showSettings(
          emit,
          _settingsView.copyWith(problem: message),
          status: message,
          severity: IssueSeverity.warning,
        );
    }
  }

  /// What the bloc reacts to in every state change: the install, a game this
  /// launcher started and the section on screen. The launch actions and the
  /// shell stay as they are; this watches what they change.
  void _multiplayerStateChanged(LauncherState previous, LauncherState next) {
    if (_multiplayer == null || isClosed) return;
    // The first snapshot says which install there is, if any.
    var check = !previous.snapshotLoaded && next.snapshotLoaded;
    if (previous.gameInstall?.path != next.gameInstall?.path) {
      _multiplayerWork
        ..roundDue = true
        ..round?.cancel();
      check = true;
    }
    bool runs(LaunchActivity? activity) =>
        activity != null && !activity.processExited;
    if (runs(previous.launchActivity) != runs(next.launchActivity)) {
      check = true;
    }
    if (previous.section != next.section) {
      if (next.section == LauncherSection.settings) check = true;
      if (previous.section == LauncherSection.diagnostics) {
        add(const MultiplayerNetworkLeft());
      }
    }
    if (check) add(const MultiplayerGameCheckRequested());
  }
}
