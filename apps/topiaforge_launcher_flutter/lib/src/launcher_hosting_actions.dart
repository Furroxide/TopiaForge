part of 'launcher_bloc.dart';

final class _HostingWork {
  int generation = 0;
  CancellationToken? token;
  HostingReceipt? receipt;
  Timer? timer;
  void stop() {
    token?.cancel();
    timer?.cancel();
    token = null;
    timer = null;
    receipt = null;
    generation++;
  }

  int begin() {
    stop();
    token = CancellationToken();
    return generation;
  }
}

extension LauncherHostingActions on LauncherBloc {
  HostingView get _hosting => state.multiplayer.hosting;
  void _showHosting(Emitter<LauncherState> emit, HostingView view) => emit(
    state.copyWith(multiplayer: state.multiplayer.copyWith(hosting: view)),
  );

  Future<void> _dispatchHosting(
    HostingEvent event,
    Emitter<LauncherState> emit,
  ) async {
    final work = _multiplayerWork.host;
    final gateway = _multiplayer;
    final install = state.gameInstall;
    final profile = state.selectedProfile;
    switch (event) {
      case HostingClosed():
        work.stop();
        _showHosting(emit, const HostingView());
      case HostingOpened():
        if (gateway is! MultiplayerHostingGateway ||
            install == null ||
            profile == null ||
            _hosting.busy ||
            state.isBusy) {
          return;
        }
        final generation = work.begin();
        _showHosting(
          emit,
          HostingView(generation: generation, phase: HostingPhase.loading),
        );
        _beside<MultiplayerPreferences?>(
          _hostingPreferences(gateway, install, work.token!),
          (value) => HostingPreferencesLoaded(generation, value),
          () => HostingPreferencesLoaded(generation, null),
        );
      case HostingPreferencesLoaded(:final generation, :final preferences):
        if (generation != work.generation) {
          return;
        }
        _showHosting(
          emit,
          HostingView(
            generation: generation,
            phase: preferences == null
                ? HostingPhase.failed
                : HostingPhase.editing,
            preferences: preferences,
            message: preferences == null
                ? 'Close Robotopia, then try again. Multiplayer preferences must synchronize before hosting.'
                : '',
          ),
        );
      case HostingEditRequested():
        if (_hosting.busy || _hosting.preferences == null) {
          return;
        }
        _showHosting(
          emit,
          HostingView(
            generation: _hosting.generation,
            phase: HostingPhase.editing,
            preferences: _hosting.preferences,
            choices: _hosting.choices,
          ),
        );
      case HostingReviewRequested(:final choices):
        if (gateway is! MultiplayerHostingGateway ||
            install == null ||
            profile == null ||
            _hosting.busy ||
            state.isBusy ||
            _hosting.preferences == null) {
          return;
        }
        final generation = work.begin();
        _showHosting(
          emit,
          HostingView(
            generation: generation,
            phase: HostingPhase.preparing,
            preferences: _hosting.preferences,
            choices: choices,
          ),
        );
        _beside<HostingPreparation>(
          gateway.prepareHosting(
            install,
            profile,
            choices,
            developerMode: state.developerMode,
            cancellation: work.token!,
          ),
          (result) => HostingPrepared(generation, result),
          () => HostingPrepared(
            generation,
            const HostingRefused('Hosting could not be checked. Try again.'),
          ),
        );
      case HostingPrepared(:final generation, :final preparation):
        if (generation != work.generation) {
          return;
        }
        _showHosting(
          emit,
          HostingView(
            generation: generation,
            phase: preparation is HostingPlan
                ? HostingPhase.review
                : HostingPhase.failed,
            preferences: preparation is HostingPlan
                ? preparation.preferences
                : _hosting.preferences,
            choices: _hosting.choices,
            plan: preparation is HostingPlan ? preparation : null,
            message: preparation is HostingRefused ? preparation.message : '',
            canAllowDirect:
                preparation is HostingRefused && preparation.canAllowDirect,
          ),
        );
      case HostingConfirmed(:final acknowledgeDisclosure):
        final plan = _hosting.plan;
        if (gateway is! MultiplayerHostingGateway ||
            install == null ||
            plan == null ||
            _hosting.phase != HostingPhase.review ||
            profile?.id != plan.profile.id ||
            profile?.revision != plan.profile.revision) {
          return;
        }
        final generation = work.generation;
        final intent = 'host-${_randomHex(12)}';
        work.receipt = HostingReceipt(
          plan.profile,
          intent,
          needsInvite: plan.choices.needsService,
        );
        _showHosting(
          emit,
          HostingView(
            generation: generation,
            phase: HostingPhase.starting,
            preferences: plan.preferences,
            choices: plan.choices,
            plan: plan,
          ),
        );
        _beside<LaunchResult>(
          gateway.startHosting(
            install,
            plan,
            intentId: intent,
            acknowledgeDisclosure: acknowledgeDisclosure,
            developerMode: state.developerMode,
            cancellation: work.token!,
          ),
          (result) => HostingStarted(generation, result),
          () => HostingStarted(
            generation,
            const LaunchResult(
              started: false,
              message: 'Hosting could not start. Check Setup and try again.',
            ),
          ),
        );
      case HostingStarted(:final generation, :final result):
        if (generation != work.generation) {
          if (result.processStarted) {
            emit(
              state.copyWith(
                statusMessage:
                    'Robotopia started before hosting was cancelled. Check the game for its status.',
              ),
            );
          }
          return;
        }
        if (!result.processStarted) {
          _showHosting(
            emit,
            HostingView(
              generation: generation,
              phase: HostingPhase.failed,
              preferences: _hosting.preferences,
              choices: _hosting.choices,
              message:
                  'Robotopia could not start hosting. Close any running copy and review Setup, then try again.',
            ),
          );
          return;
        }
        work.receipt?.bind(result);
        _reportHosting(emit);
        work.timer = Timer(const Duration(seconds: 90), () {
          if (!isClosed) add(HostingWaitExpired(generation));
        });
      case HostingWaitExpired(:final generation):
        if (generation == work.generation &&
            _hosting.phase == HostingPhase.waiting) {
          _showHosting(
            emit,
            HostingView(
              generation: generation,
              phase: HostingPhase.waiting,
              message:
                  'Hosting or its invite is still unconfirmed. Check Robotopia; it was left running.',
            ),
          );
        }
    }
  }

  Future<MultiplayerPreferences?> _hostingPreferences(
    MultiplayerGateway gateway,
    GameInstall install,
    CancellationToken token,
  ) async {
    if (await gateway.gameRunState(install) != GameRunState.notRunning) {
      return null;
    }
    final result = await gateway.syncWithGame(install, cancellation: token);
    return !token.isCancelled &&
            result is MultiplayerPreferencesSynced &&
            result.preferences.readable
        ? result.preferences
        : null;
  }

  void _followHosting(Emitter<LauncherState> emit, LaunchActivity activity) {
    final receipt = _multiplayerWork.host.receipt;
    if (receipt == null) {
      return;
    }
    receipt.accept(activity);
    if (receipt.activity != null) _reportHosting(emit);
  }

  void _reportHosting(Emitter<LauncherState> emit) {
    final receipt = _multiplayerWork.host.receipt;
    if (receipt == null) {
      return;
    }
    final phase = receipt.failed
        ? HostingPhase.failed
        : receipt.confirmed
        ? HostingPhase.hosting
        : HostingPhase.waiting;
    if (phase != HostingPhase.waiting) _multiplayerWork.host.timer?.cancel();
    final message = receipt.ended
        ? 'Hosting ended. Start a new hosted session when ready.'
        : receipt.failed
        ? 'The game could not confirm hosting. Check its multiplayer status before retrying.'
        : receipt.confirmed
        ? 'Hosting confirmed.'
        : 'Robotopia started; waiting for hosting confirmation. ${_hosting.choices?.passwordRequired == true ? 'Enter the password in Robotopia to open hosting.' : ''}';
    _showHosting(
      emit,
      HostingView(
        generation: _multiplayerWork.host.generation,
        phase: phase,
        message: message,
        inviteCode: receipt.inviteCode,
        preferences: _hosting.preferences,
        choices: _hosting.choices,
        plan: _hosting.plan,
      ),
    );
    if (receipt.activity != null) {
      emit(state.copyWith(launchActivity: receipt.activity));
    }
  }
}
