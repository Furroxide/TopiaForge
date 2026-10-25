part of 'multiplayer_repository.dart';

/// The launching stage of a join, for [LocalMultiplayerRepository]: the join
/// request built from the player's settings, and the copy of the preferences
/// the game reads written before the game starts.
extension _JoinLaunches on LocalMultiplayerRepository {
  Future<JoinLaunch> _launchJoin(
    JoinSessionState state, {
    required GameInstall install,
    required String intentId,
    required bool developerMode,
  }) async {
    final sessions = _sessions;
    final plan = state.plan;
    if (sessions == null ||
        state.stage != JoinStage.launching ||
        plan == null) {
      return JoinLaunchRefused(
        JoinFailed(
          JoinFailureCode.startFailed,
          detail: 'This launcher cannot start a join here, so nothing started.',
        ),
      );
    }
    final load = await _store.loadSettings();
    if (load.recovery == MultiplayerStoreRecovery.unreadable) {
      return JoinLaunchRefused(
        JoinFailed(JoinFailureCode.launchBlocked, detail: _settingsUnreadable),
      );
    }
    final settings = load.settings;
    final LaunchSessionRequest session;
    try {
      final target = LaunchSessionTarget.forPlan(state.target, plan);
      final refusal = _requestRefusal(target, settings);
      if (refusal != null) {
        return JoinLaunchRefused(
          JoinFailed(JoinFailureCode.launchBlocked, detail: refusal),
        );
      }
      session = LaunchSessionRequest.join(
        intentId: intentId,
        settings: settings,
        privacy: state.privacy == MultiplayerAddressPrivacy.direct
            ? settings.privacyForSession(allowDirect: true)
            : SessionPrivacy.relayOnly,
        target: target,
        serviceOrigin: _serviceOrigin,
        developerMode: developerMode,
      );
    } on StateError {
      // A direct session needs the disclosure, which the settings no longer
      // record, as when they were reset since the join began.
      return JoinLaunchRefused(
        JoinFailed(
          JoinFailureCode.launchBlocked,
          detail:
              'A direct connection needs the address disclosure, and your '
              'settings no longer record that you read it, so nothing '
              'started. Join again to read it.',
        ),
      );
    } on Object {
      return JoinLaunchRefused(
        JoinFailed(
          JoinFailureCode.launchBlocked,
          detail:
              'The launcher could not prepare this join for the game, so '
              'nothing started. Join again.',
        ),
      );
    }
    return sessions.launchJoinSession(
      install,
      state,
      session,
      beforeStart: () => _projectPreferences(install),
    );
  }

  /// Why the game could not take a join of [target], when it could not.
  String? _requestRefusal(
    LaunchSessionTarget target,
    MultiplayerSettings settings,
  ) {
    if (target.needsService && _serviceOrigin == null) {
      return 'This server is found through the TopiaForge multiplayer service, '
          'which this build does not name, so the game could not reach it. '
          'Join it on your local network or by its address instead.';
    }
    if (target is LaunchListingTarget &&
        target.source == ServerListingSource.lan &&
        !settings.lanDiscoveryEnabled) {
      return 'This server was found on your local network, and LAN discovery '
          'is off, so the game could not find it. Turn LAN discovery on in '
          'Settings, under Multiplayer, then join again.';
    }
    return null;
  }

  /// Brings the copy of the preferences the game reads up to date before it
  /// starts. A copy that cannot be written stops the launch: the game would
  /// start with the player's earlier choices, voice chat among them.
  Future<JoinFailed?> _projectPreferences(GameInstall install) async {
    final round = await syncWithGame(install);
    return switch (round) {
      MultiplayerPreferencesSynced() => null,
      MultiplayerPreferencesRoundSkipped() => JoinFailed(
        JoinFailureCode.launchBlocked,
        detail: _settingsUnreadable,
      ),
      MultiplayerPreferencesRoundFailed() => JoinFailed(
        JoinFailureCode.launchBlocked,
        detail:
            "Robotopia's copy of your multiplayer settings could not be "
            'updated, so the game was not started with your earlier ones. '
            "Check that TopiaForge can write to the game's folder and its own "
            'data folder, then join again.',
      ),
    };
  }
}

const _settingsUnreadable =
    'Your multiplayer settings could not be read just now, so nothing '
    'started. Another program may be using the file; try again in a moment.';

/// The downloading stage of a join, for [LocalMultiplayerRepository].
extension _JoinDownloads on LocalMultiplayerRepository {
  /// Answers the downloading stage through [content], with the packages
  /// only the host serves taken from the host as the descriptor fetch
  /// reached it (`JoinHostPackages`). A LAN host stays heard until the
  /// stage ends, however it ends: a stop asked for meanwhile waits for it,
  /// and browsing it had to start stops after it.
  Future<JoinEvent?> _download(
    MultiplayerContentHost content,
    GameInstall install,
    JoinSessionState state,
    void Function(JoinEvent progress)? onProgress,
    CancellationToken? cancellation,
  ) async {
    final request = state.downloadRequest;
    final host = request == null || request.hostDownloads.isEmpty
        ? null
        : _resolver.hostPackagesFor(request, mayBrowse: _lanAllowed);
    try {
      return await content.downloadJoinContent(
        install,
        state,
        onProgress: onProgress,
        cancellation: cancellation,
        hostPackages: host,
      );
    } finally {
      host?.release();
    }
  }

  /// Whether the stored settings let LAN browsing run: readable, with
  /// discovery on, as [startLanBrowsing] reads them.
  Future<bool> _lanAllowed() async {
    final load = await _store.loadSettings();
    return load.recovery != MultiplayerStoreRecovery.unreadable &&
        load.settings.lanDiscoveryEnabled;
  }
}
