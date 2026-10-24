part of '../local_launcher_repository.dart';

/// [MultiplayerContentHost] for [LocalLauncherRepository]: a join's exact
/// downloads and installs, its server profile, and the cleanup of server
/// content. Each method hands over to the part that implements it.
///
/// Nothing installs or cleans up beside a running game: the game writes its
/// manager state when it quits, over whatever was written while it ran,
/// which would drop the disabled records of mods new to the player. The
/// package inbox follows the same rule, holding what arrives until the next
/// start. An install closes a game this launcher started only when the
/// player's consent covered it, and only once everything is staged: it asks
/// the game to quit, as its Quit button would, and goes on only when the
/// game's next start loads its mods ([_closeOwnedGameForInstall]). A game it
/// did not start must be closed by the player. Downloads and the server
/// profile do not touch the game, so they go ahead.
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
  /// Whether an install may go ahead with the game as it is: null when
  /// nothing runs, or when a game this launcher started runs and the consent
  /// covered closing it, which [_closeOwnedGameForInstall] does once
  /// everything is staged. The failure otherwise, before anything is staged.
  Future<JoinFailed?> _gameBlockingJoinInstall(
    GameInstall install, {
    required bool closeOwnedGame,
  }) async {
    if (!await _runningForLaunch(install)) return null;
    if (_ownedLaunchProcesses[_launchInstallIdentity(install)] == null) {
      return _foreignGameRunning;
    }
    return closeOwnedGame
        ? null
        : const JoinFailed.gameRunning(
            ownership: JoinGameOwnership.owned,
            detail:
                'Robotopia is running. Save your game and quit it, then join '
                'again; what was downloaded is kept. Nothing was installed.',
          );
  }

  /// Makes sure no game runs before an install changes anything, and that a
  /// game this launcher started leaves its next start, which the join makes,
  /// loading its mods. Null once both hold.
  ///
  /// A game this launcher started that still runs is asked to quit, as its
  /// own Quit button would, and stopped only if it does not
  /// ([_closeOwnedGame]); one that has ended on its own since it was
  /// started, while the join downloaded or staged, is already gone. What its
  /// next start does is read from the startup journal it left, never
  /// inferred from how it ended ([ReplacedGame.safeMode]): a game that
  /// recorded no clean exit, because it was stopped, or quit before its mods
  /// finished unloading, or had crashed, opens its next start in safe mode,
  /// where a session is refused. Such a join ends here, before anything is
  /// installed, and says how to clear that; what was downloaded is kept. A
  /// game this launcher did not start, one whose process cannot be verified,
  /// and one that could not be stopped end the join as
  /// [JoinFailureCode.gameRunning]. A launcher that closes while it waits
  /// stops nothing and installs nothing. When this launcher started no game
  /// here, no journal is read: that is for the join's launch.
  Future<JoinFailed?> _closeOwnedGameForInstall(GameInstall install) async {
    final identity = _launchInstallIdentity(install);
    final process = _ownedLaunchProcesses[identity];
    final running = await _runningForLaunch(install);
    if (process == null) return running ? _foreignGameRunning : null;
    bool? alive;
    ReplacedGame? closed;
    try {
      // Nothing runs, so the game ended on its own since it was started.
      alive = running ? await _gameProcessLiveness(process) : false;
      if (alive == true) {
        closed = await _closeOwnedGame(install, process);
        if (closed == null) {
          // The launcher closed while it waited; an untaken request is
          // withdrawn, and the game is left as it is.
          return _nothingInstalled(
            'The launcher closed while it asked Robotopia to quit, so nothing '
            'was stopped.',
          );
        }
      } else if (alive == false) {
        // It ended on its own before the install came to close it.
        closed = await _replacedGame(install, ReplacedGameExit.exited);
      }
    } on Object catch (error) {
      alive = null;
      await _appendLauncherLogBestEffort(
        'Closing the game for a join install failed: $error',
      );
    }
    if (alive == null || closed == null) return _ownedGameNotClosed;
    // Closed, or already gone: this launcher no longer owns a game here.
    _ownedLaunchProcesses.remove(identity);
    _ownedLaunchRequests.remove(process);
    final retired = _launchMonitors.entries
        .where((entry) => entry.value.current.installIdentity == identity)
        .toList();
    for (final entry in retired) {
      await entry.value.dispose();
      _launchMonitors.remove(entry.key);
    }
    // The game this launcher started has ended, so one that runs now is
    // another copy.
    if (await _runningForLaunch(install)) return _foreignGameRunning;
    if (closed.safeMode) {
      await _appendLauncherLogBestEffort(
        'The game this launcher started was closed for a join install, but '
        'its next start opens in safe mode, so nothing was installed.',
      );
      return _nothingInstalled(
        '${closed.message(started: false)} A game in safe mode cannot join a '
        'session. Start Robotopia from the launcher once and quit it normally, '
        'then join again; what was downloaded is kept.',
      );
    }
    await _appendLauncherLogBestEffort(
      closed.exit == ReplacedGameExit.exited
          ? 'The game this launcher started had already exited when a join '
                'came to install.'
          : 'Closed the game this launcher started, to install a join.',
    );
    return null;
  }
}

const _foreignGameRunning = JoinFailed.gameRunning(
  ownership: JoinGameOwnership.foreign,
  detail:
      'Robotopia is running, and this launcher did not start it. Close the '
      'game, then join again. Nothing was installed.',
);

const _ownedGameNotClosed = JoinFailed.gameRunning(
  ownership: JoinGameOwnership.owned,
  detail:
      'Robotopia is running and could not be closed. Close the game, then join '
      'again. Nothing was installed.',
);
