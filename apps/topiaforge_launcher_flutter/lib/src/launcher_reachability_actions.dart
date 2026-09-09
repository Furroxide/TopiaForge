part of 'launcher_bloc.dart';

/// Developer-only NAT reachability probe.
///
/// The probe answers one question the multiplayer relay cost model depends on: what fraction of real hosts are
/// directly reachable? It is off by default, hidden outside developer mode, and its result never leaves the machine
/// — see `docs/internal/LauncherReachabilityProbe.md`.
extension LauncherReachabilityActions on LauncherBloc {
  Future<void> _onReachabilityProbeSettingsChanged(
    ReachabilityProbeSettingsChanged event,
    Emitter<LauncherState> emit,
  ) async {
    final gateway = _reachabilityProbe;
    if (gateway == null) {
      emit(
        state.copyWith(statusMessage: 'The reachability probe is unavailable.'),
      );
      return;
    }
    await _guard(emit, 'Reachability probe settings saved.', () async {
      await gateway.saveSettings(event.settings);
      // Leave isBusy set: _guard emits the success message above only while the state
      // is still busy, so clearing it here would silently drop that feedback.
      emit(state.copyWith(reachabilityProbe: event.settings));
    });
  }

  Future<void> _onReachabilityProbeRequested(
    ReachabilityProbeRequested event,
    Emitter<LauncherState> emit,
  ) async {
    final gateway = _reachabilityProbe;
    if (gateway == null) {
      emit(
        state.copyWith(statusMessage: 'The reachability probe is unavailable.'),
      );
      return;
    }
    await _guard(emit, 'Reachability probe finished.', () async {
      final outcome = await gateway.run(developerMode: state.developerMode);
      emit(
        state.copyWith(
          isBusy: false,
          reachabilityResult: outcome.classification,
          statusMessage: _reachabilityStatus(outcome),
          statusSeverity: outcome.ran
              ? IssueSeverity.info
              : IssueSeverity.warning,
        ),
      );
    });
  }

  String _reachabilityStatus(ReachabilityProbeOutcome outcome) {
    final refusal = outcome.refusal;
    if (refusal != null) {
      return switch (refusal) {
        ReachabilityProbeRefusal.developerModeRequired =>
          'The reachability probe runs only in developer mode.',
        ReachabilityProbeRefusal.notEnabled =>
          'Turn the reachability probe on before running it.',
        ReachabilityProbeRefusal.sharingNotConsented =>
          'Sharing an aggregate result has not been agreed to.',
        ReachabilityProbeRefusal.privacyNoticeNotApproved =>
          'No approved privacy notice covers sharing this result.',
      };
    }
    final message = outcome.message;
    if (message != null) return message;
    return 'Reachability: ${outcome.classification.reachability.name}.';
  }
}
