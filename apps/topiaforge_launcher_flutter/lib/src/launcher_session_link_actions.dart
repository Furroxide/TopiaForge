part of 'launcher_bloc.dart';

/// Session links and link handling.
///
/// A link never joins on its own. The domain reads it; a valid link waits
/// in [SessionLinkConsentPlaceholder] until the multiplayer section's
/// consent flow takes it over, and a refused one says why without repeating
/// the link. A newer link replaces a pending one. Nothing here logs a link.
extension LauncherSessionLinkActions on LauncherBloc {
  FutureOr<void> _dispatchMultiplayerEvent(
    MultiplayerEvent event,
    Emitter<LauncherState> emit,
  ) {
    return switch (event) {
      SessionLinkReceived() => _onSessionLinkReceived(event, emit),
      SessionLinkDismissed() => _onSessionLinkDismissed(event, emit),
      SessionLinkHandlingStarted() => _onSessionLinkHandlingStarted(emit),
      SessionLinkHandlingChanged() => _onSessionLinkHandlingChanged(
        event,
        emit,
      ),
      SessionLinkHandlingUpdated() => _onSessionLinkHandlingUpdated(
        event,
        emit,
      ),
    };
  }

  Future<void> _onSessionLinkReceived(
    SessionLinkReceived event,
    Emitter<LauncherState> emit,
  ) async {
    final multiplayer = state.multiplayer;
    final arrival = multiplayer.linkArrivals + 1;
    final source = event.arrival.source;
    final reading = event.arrival.read();
    final link = reading.link;
    if (link == null) {
      final refusal = reading.refusal ?? SessionLinkRefusal.invalid;
      emit(
        state.copyWith(
          multiplayer: multiplayer.copyWith(
            linkArrivals: arrival,
            sessionLink: SessionLinkRefused(
              refusal: refusal,
              source: source,
              arrival: arrival,
            ),
          ),
          statusMessage: 'The link was not opened.',
          statusSeverity: IssueSeverity.warning,
          errorMessage: sessionLinkRefusalMessage(refusal),
        ),
      );
      return;
    }
    emit(
      state.copyWith(
        multiplayer: multiplayer.copyWith(
          linkArrivals: arrival,
          sessionLink: SessionLinkConsentPlaceholder(
            link: link,
            source: source,
            arrival: arrival,
          ),
        ),
        statusMessage:
            'Received ${sessionLinkKindLabel(link.kind)}. Joining from a '
            'link is not available in this build yet, so nothing was '
            'joined.',
        clearError: true,
      ),
    );
  }

  Future<void> _onSessionLinkDismissed(
    SessionLinkDismissed event,
    Emitter<LauncherState> emit,
  ) async {
    emit(
      state.copyWith(
        multiplayer: state.multiplayer.copyWith(
          sessionLink: const SessionLinkIdle(),
        ),
      ),
    );
  }

  Future<void> _onSessionLinkHandlingStarted(
    Emitter<LauncherState> emit,
  ) async {
    final handling = _sessionLinkHandling;
    if (handling == null) return;
    final status = await handling.start();
    emit(
      state.copyWith(
        multiplayer: state.multiplayer.copyWith(linkHandling: status),
      ),
    );
  }

  Future<void> _onSessionLinkHandlingChanged(
    SessionLinkHandlingChanged event,
    Emitter<LauncherState> emit,
  ) async {
    final handling = _sessionLinkHandling;
    if (handling == null) {
      emit(
        state.copyWith(
          statusMessage: 'Link handling is not available here.',
          statusSeverity: IssueSeverity.warning,
        ),
      );
      return;
    }
    try {
      // The registration follows the saved setting and reports back through
      // SessionLinkHandlingUpdated.
      await handling.setEnabled(event.enabled);
    } on Object catch (error) {
      emit(
        state.copyWith(
          statusMessage: 'The link setting was not saved.',
          statusSeverity: IssueSeverity.error,
          errorMessage: '$error',
        ),
      );
    }
  }

  Future<void> _onSessionLinkHandlingUpdated(
    SessionLinkHandlingUpdated event,
    Emitter<LauncherState> emit,
  ) async {
    final status = event.status;
    final failed = status.registration == SessionLinkRegistration.failed;
    emit(
      state.copyWith(
        multiplayer: state.multiplayer.copyWith(linkHandling: status),
        statusMessage: _linkHandlingMessage(status),
        statusSeverity: failed ? IssueSeverity.warning : IssueSeverity.info,
      ),
    );
  }

  String _linkHandlingMessage(SessionLinkHandlingStatus status) =>
      switch (status.registration) {
        SessionLinkRegistration.failed =>
          'The link setting was saved, but the system registration did not '
              'change. ${status.detail}',
        SessionLinkRegistration.managedBySystem ||
        SessionLinkRegistration.unsupported => status.detail,
        _ =>
          status.enabled
              ? 'TopiaForge opens topiaforge:// links.'
              : 'TopiaForge no longer opens topiaforge:// links.',
      };
}
