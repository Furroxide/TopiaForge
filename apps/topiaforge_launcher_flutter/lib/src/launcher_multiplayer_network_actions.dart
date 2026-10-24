part of 'launcher_bloc.dart';

/// Diagnostics → Network: the multiplayer service, LAN discovery's state and
/// counters, and port checks, which run only when the player asks.
///
/// Listening on the LAN and checking ports open ports on this computer, so
/// neither starts before the player has read what they do
/// ([NetworkCheckNotice]); the bloc refuses both until then, whatever the
/// screen does. A check runs beside the event lane with a cancellation
/// token, and leaving Diagnostics drops it and stops the listening this page
/// started. No message here carries an error's own text.
extension LauncherMultiplayerNetworkActions on LauncherBloc {
  FutureOr<void> _dispatchMultiplayerNetworkEvent(
    MultiplayerNetworkEvent event,
    Emitter<LauncherState> emit,
  ) => switch (event) {
    MultiplayerNetworkCheckRequested() => _onNetworkCheckRequested(event, emit),
    MultiplayerNetworkChecked() => _onNetworkChecked(event, emit),
    MultiplayerLanListeningRequested() => _onLanListeningRequested(event, emit),
    MultiplayerLanSnapshotChanged() => _onLanSnapshotChanged(event, emit),
    MultiplayerNetworkNoticeRead() => _onNetworkNoticeRead(emit),
    MultiplayerNetworkLeft() => _onNetworkLeft(emit),
  };

  MultiplayerNetworkView get _networkView => state.multiplayer.network;

  void _showNetwork(
    Emitter<LauncherState> emit,
    MultiplayerNetworkView view, {
    String? status,
    IssueSeverity? severity,
  }) => emit(
    state.copyWith(
      multiplayer: state.multiplayer.copyWith(network: view),
      statusMessage: status,
      statusSeverity: severity,
    ),
  );

  /// Whether the player has read what listening and port checks do: this
  /// session, or stored with the preferences.
  bool get _networkNoticeRead =>
      _networkView.noticeRead ||
      _settingsView.preferences?.needsNetworkNotice == false;

  bool _refusedBeforeNotice(Emitter<LauncherState> emit) {
    if (_networkNoticeRead) return false;
    _showNetwork(
      emit,
      _networkView,
      status: 'Read what this check does on your network first.',
      severity: IssueSeverity.warning,
    );
    return true;
  }

  Future<void> _onNetworkCheckRequested(
    MultiplayerNetworkCheckRequested event,
    Emitter<LauncherState> emit,
  ) async {
    final gateway = _multiplayer;
    if (gateway == null) return;
    if (event.checkPorts && _refusedBeforeNotice(emit)) return;
    _multiplayerWork.check?.cancel();
    final token = _multiplayerWork.check = CancellationToken();
    final generation = ++_multiplayerWork.network;
    _showNetwork(
      emit,
      _networkView.copyWith(
        checking: true,
        checkingPorts: event.checkPorts,
        clearProblem: true,
      ),
    );
    _beside(
      gateway.checkNetwork(
        checkPorts: event.checkPorts,
        // The port sessions the player hosts use, once the preferences read.
        gamePort: _settingsView.preferences?.hosting.port,
        developerMode: state.developerMode,
        cancellation: token,
      ),
      (report) => MultiplayerNetworkChecked(generation, report),
      () => MultiplayerNetworkChecked(generation, null),
    );
  }

  Future<void> _onNetworkChecked(
    MultiplayerNetworkChecked event,
    Emitter<LauncherState> emit,
  ) async {
    if (event.generation != _multiplayerWork.network) return;
    final ports = _networkView.checkingPorts;
    _multiplayerWork.check = null;
    final view = _networkView.copyWith(checking: false, checkingPorts: false);
    final report = event.report;
    if (report == null) {
      _showNetwork(
        emit,
        view.copyWith(
          problem: 'The network check could not finish. Try again.',
        ),
        status: 'The network check could not finish.',
        severity: IssueSeverity.warning,
      );
      return;
    }
    _showNetwork(
      emit,
      view.copyWith(report: report, lan: report.lan),
      status: ports ? 'Checked the multiplayer ports.' : null,
    );
  }

  Future<void> _onLanListeningRequested(
    MultiplayerLanListeningRequested event,
    Emitter<LauncherState> emit,
  ) async {
    final gateway = _multiplayer;
    if (gateway == null) return;
    if (event.listen && _refusedBeforeNotice(emit)) return;
    _showNetwork(emit, _networkView.copyWith(listening: event.listen));
    _beside(
      event.listen ? gateway.startLanBrowsing() : gateway.stopLanBrowsing(),
      MultiplayerLanSnapshotChanged.new,
      () => MultiplayerLanSnapshotChanged(gateway.lanSnapshot),
    );
  }

  Future<void> _onLanSnapshotChanged(
    MultiplayerLanSnapshotChanged event,
    Emitter<LauncherState> emit,
  ) async {
    _showNetwork(emit, _networkView.copyWith(lan: event.snapshot));
  }

  Future<void> _onNetworkNoticeRead(Emitter<LauncherState> emit) async {
    _showNetwork(emit, _networkView.copyWith(noticeRead: true));
    // Kept with the preferences, so it is not asked again; while they cannot
    // be read it counts for this session only.
    final preferences = _settingsView.preferences;
    if (preferences != null && preferences.needsNetworkNotice) {
      await _onPreferencesEdited(
        const MultiplayerPreferencesEdited(
          MultiplayerPreferencesChange(acknowledgeNetworkNotice: true),
        ),
        emit,
      );
    }
  }

  Future<void> _onNetworkLeft(Emitter<LauncherState> emit) async {
    final gateway = _multiplayer;
    if (gateway == null) return;
    _multiplayerWork.check?.cancel();
    _multiplayerWork.check = null;
    // An answer still on its way is from before leaving: it is dropped.
    _multiplayerWork.network++;
    final listening = _networkView.listening;
    _showNetwork(
      emit,
      _networkView.copyWith(
        checking: false,
        checkingPorts: false,
        listening: false,
      ),
    );
    if (listening) {
      _beside(
        gateway.stopLanBrowsing(),
        MultiplayerLanSnapshotChanged.new,
        () => MultiplayerLanSnapshotChanged(gateway.lanSnapshot),
      );
    }
  }
}
