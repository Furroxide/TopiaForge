part of '../local_launcher_repository.dart';

/// How a restart closes a game this launcher started.
///
/// It asks the game to quit, as the game's own Quit button would, so its mods
/// unload and it records a clean exit, and the restarted game loads its mods
/// rather than opening in safe mode. It waits up to [answerWithin] for the
/// game to take the request and up to [exitWithin] in all for it to exit,
/// looking every [lookEvery], and only then stops it. A game whose manager
/// never takes the request, because it failed to load or the game hangs, is
/// stopped once [answerWithin] has passed.
final class OwnedGameQuitPolicy {
  const OwnedGameQuitPolicy({
    this.answerWithin = const Duration(seconds: 10),
    this.exitWithin = const Duration(seconds: 30),
    this.lookEvery = const Duration(milliseconds: 100),
    this.pause,
  });

  /// How long the game has to take the request. Its manager looks twice a
  /// second; this leaves room for a frame that a level load holds.
  final Duration answerWithin;

  /// How long the game has, from the request, to exit. The manager holds a
  /// quit for at most 10 seconds while mods unload.
  final Duration exitWithin;

  final Duration lookEvery;

  /// Waits between looks; tests pass one that returns at once. The bounds
  /// count these waits, so a test never waits on the wall clock.
  final Future<void> Function(Duration delay)? pause;
}

/// The manager's own bound on its startup journal (`JsonUtil`).
const _maxStartupJournalBytes = 4 * 1024 * 1024;

extension _OwnedGameQuit on LocalLauncherRepository {
  /// Closes [process], the verified game this launcher started in
  /// [install], and says how it ended: asked to quit, and stopped only if it
  /// did not, and whether the game's next start opens in safe mode. Null when
  /// the repository closed while it waited; then nothing was stopped, and a
  /// request the game had not taken is withdrawn.
  ///
  /// Callers pass only a process [_ownedLaunchProcesses] holds and
  /// [_gameProcessLiveness] verified. The request names that process's own
  /// launch request, which no other game knows, so nothing this launcher did
  /// not start is ever asked to quit, or stopped.
  Future<ReplacedGame?> _closeOwnedGame(
    GameInstall install,
    LaunchProcessIdentity process,
  ) async {
    if (await _gameProcessLiveness(process) == false) {
      return _replacedGame(install, ReplacedGameExit.exited);
    }
    final requestId = _ownedLaunchRequests[process];
    final store = LaunchStagingStore(install.path);
    LauncherQuitRequest? request;
    if (requestId != null) {
      try {
        request = LauncherQuitRequest(
          requestId: requestId,
          quitId: _newLaunchRequestId(),
        );
        await store.writeLauncherQuit(request);
      } on Object catch (error) {
        request = null;
        await _appendLauncherLogBestEffort(
          'The running game could not be asked to quit ($error).',
        );
      }
    }
    var reason = 'The running game was stopped.';
    if (request != null) {
      switch (await _waitForOwnedGameQuit(store, request, process)) {
        case _OwnedGameWait.quit:
          return _replacedGame(install, ReplacedGameExit.quit);
        case _OwnedGameWait.repositoryClosed:
          return null;
        case _OwnedGameWait.notTaken:
          reason =
              'The running game did not take the quit request within '
              '${_ownedGameQuit.answerWithin.inSeconds} s, so it was stopped.';
        case _OwnedGameWait.notExited:
          reason =
              'The running game took the quit request but did not exit within '
              '${_ownedGameQuit.exitWithin.inSeconds} s, so it was stopped.';
      }
    }
    // A launcher that closed while it waited stops nothing, even when it
    // closed during the last look, as the bound passed.
    if (_disposed) return null;
    await _gameProcessStopper(process);
    await _appendLauncherLogBestEffort(reason);
    return _replacedGame(install, ReplacedGameExit.stopped);
  }

  Future<ReplacedGame> _replacedGame(
    GameInstall install,
    ReplacedGameExit exit,
  ) async {
    final safeMode = await _nextStartOpensInSafeMode(install);
    if (safeMode && exit != ReplacedGameExit.stopped) {
      await _appendLauncherLogBestEffort(
        'The running game ended without recording a clean exit, so the next '
        'start opens in safe mode.',
      );
    }
    return ReplacedGame(exit, safeMode: safeMode);
  }

  /// Whether the game's next start opens in safe mode, as its manager reads
  /// the startup journal the game just closed left (`StartupJournal`): only a
  /// recorded clean exit, or no journal at all, starts normally. A journal
  /// naming a mod that was loading turns that one mod off, which the manager
  /// reports itself, and one that cannot be read now claims nothing.
  Future<bool> _nextStartOpensInSafeMode(GameInstall install) async {
    final file = File(
      p.join(_managerRoot(install).path, 'startup-journal.json'),
    );
    final List<int> bytes;
    try {
      if (FileSystemEntity.typeSync(file.path, followLinks: false) ==
          FileSystemEntityType.notFound) {
        return false;
      }
      bytes = await _readLauncherFileBounded(file, _maxStartupJournalBytes);
    } on Object {
      return false;
    }
    final Object? journal;
    try {
      journal = jsonDecode(utf8.decode(bytes));
    } on FormatException {
      // The manager cannot read it either, and starts in safe mode.
      return true;
    }
    if (journal is! Map || journal['schemaVersion'] != 1) return true;
    final state = journal['state'];
    if (state == 'clean-exit') return false;
    final loading = journal['currentModId'];
    return !(state == 'loading' &&
        loading is String &&
        ModManifest.isValidId(loading));
  }

  Future<_OwnedGameWait> _waitForOwnedGameQuit(
    LaunchStagingStore store,
    LauncherQuitRequest request,
    LaunchProcessIdentity process,
  ) async {
    final policy = _ownedGameQuit;
    final pause = policy.pause ?? (delay) => Future<void>.delayed(delay);
    var waited = Duration.zero;
    var taken = false;
    while (true) {
      // An unknown answer counts as running: only a proven exit is a quit.
      if (await _gameProcessLiveness(process) == false) {
        await store.withdrawLauncherQuit(request);
        await _appendLauncherLogBestEffort(
          'The running game quit when asked, within '
          '${waited.inMilliseconds} ms.',
        );
        return _OwnedGameWait.quit;
      }
      taken = taken || !await store.isLauncherQuitPending(request);
      if (waited >= (taken ? policy.exitWithin : policy.answerWithin)) break;
      if (_disposed) {
        await store.withdrawLauncherQuit(request);
        return _OwnedGameWait.repositoryClosed;
      }
      await pause(policy.lookEvery);
      waited += policy.lookEvery;
    }
    await store.withdrawLauncherQuit(request);
    return taken ? _OwnedGameWait.notExited : _OwnedGameWait.notTaken;
  }
}

enum _OwnedGameWait { quit, repositoryClosed, notTaken, notExited }
