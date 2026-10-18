part of '../local_launcher_repository.dart';

/// [MultiplayerContentHost] for [LocalLauncherRepository]: a join's exact
/// downloads and installs, its server profile, and the cleanup of server
/// content. Each method hands over to the part that implements it.
///
/// Nothing installs or cleans up beside a running game: the game writes its
/// manager state when it quits, over whatever was written while it ran,
/// which would drop the disabled records of mods new to the player. The
/// package inbox follows the same rule, holding what arrives until the next
/// start. An install closes a game this launcher started, as the join's
/// restart would; a game it did not start must be closed by the player.
/// Downloads and the server profile do not touch the game, so they go ahead.
mixin _MultiplayerContentHosting implements MultiplayerContentHost {
  LocalLauncherRepository get _host => this as LocalLauncherRepository;

  /// One store per manager folder, so the writes of one install's record run
  /// one after another.
  final Map<String, ServerContentStore> _serverContentStores = {};

  @override
  Future<JoinEvent?> downloadJoinContent(
    GameInstall install,
    JoinSessionState state, {
    void Function(JoinDownloadProgressed progress)? onProgress,
    CancellationToken? cancellation,
  }) => _host._downloadJoinContent(
    state,
    onProgress: onProgress,
    cancellation: cancellation,
  );

  @override
  Future<JoinEvent?> installJoinContent(
    GameInstall install,
    JoinSessionState state, {
    void Function(JoinInstallProgressed progress)? onProgress,
    CancellationToken? cancellation,
  }) => _host._installJoinContent(
    install,
    state,
    onProgress: onProgress,
    cancellation: cancellation,
  );

  @override
  Future<JoinEvent?> prepareJoinProfile(
    GameInstall install,
    JoinSessionState state,
  ) => _host._prepareJoinProfile(install, state);

  @override
  Future<ServerContentCleanupPlan> planServerContentCleanup(
    GameInstall install, {
    Set<String>? serverProfileIds,
  }) => _host._planServerContentCleanup(install, scope: serverProfileIds);

  @override
  Future<ServerContentCleanupResult> applyServerContentCleanup(
    GameInstall install,
    ServerContentCleanupPlan plan,
  ) => _host._applyServerContentCleanup(install, plan);
}

extension _JoinGameClosing on LocalLauncherRepository {
  /// Makes sure no game runs before an install. A game this launcher
  /// started is closed, as the join's restart would close it at launch; one
  /// it did not start, or one that will not close, ends the join. Null once
  /// nothing runs.
  Future<JoinFailed?> _closeGameForInstall(GameInstall install) async {
    if (!await _runningForLaunch(install)) return null;
    final identity = _launchInstallIdentity(install);
    final process = _ownedLaunchProcesses[identity];
    bool? alive;
    if (process != null) {
      try {
        alive = await _gameProcessLiveness(process);
        if (alive == true) await _gameProcessStopper(process);
      } on Object catch (error) {
        alive = null;
        await _appendLauncherLogBestEffort(
          'Closing the game for a join install failed: $error',
        );
      }
    }
    if (process != null && alive != null) {
      // Stopped, or already gone: this launcher no longer owns a game here.
      _ownedLaunchProcesses.remove(identity);
      final retired = _launchMonitors.entries
          .where((entry) => entry.value.current.installIdentity == identity)
          .toList();
      for (final entry in retired) {
        await entry.value.dispose();
        _launchMonitors.remove(entry.key);
      }
      if (!await _runningForLaunch(install)) {
        await _appendLauncherLogBestEffort(
          'Closed the game this launcher started, to install a join.',
        );
        return null;
      }
    }
    return process == null || alive == false
        ? const JoinFailed.gameRunning(
            ownership: JoinGameOwnership.foreign,
            detail:
                'Robotopia is running, and this launcher did not start it. '
                'Close the game, then join again. Nothing was installed.',
          )
        : const JoinFailed.gameRunning(
            ownership: JoinGameOwnership.owned,
            detail:
                'Robotopia is running and could not be closed. Close the '
                'game, then join again. Nothing was installed.',
          );
  }
}
