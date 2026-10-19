part of 'launcher_data_test.dart';

Future<void> _noPause(Duration _) async {}

/// A game this launcher started, as a restart sees it. Its manager takes the
/// launcher's quit request for the launch it was started with and quits, or
/// never takes it, or takes it and hangs.
final class _FakeOwnedGame {
  _FakeOwnedGame(this.gameRoot, {this.takes = true, this.exits = true});

  final Directory gameRoot;

  /// Whether its manager takes the request: false as if it never loaded.
  final bool takes;

  /// Whether it exits once it has taken the request: false as if it hung.
  final bool exits;
  var running = false;
  var starts = 0;
  var stops = 0;
  final taken = <LauncherQuitRequest>[];

  /// The launch request this game was started with.
  String? requestId;
  var _exitOnNextLook = false;

  /// Set to have the game exit on its own, on the look just after the next
  /// probe sees it running.
  var exitsUnasked = false;
  var _looksBeforeExit = -1;

  /// Set to have its liveness unknown while a quit request waits, as when
  /// its handle cannot be read.
  var unknownWhileAsked = false;

  Directory get staging =>
      Directory(p.join(gameRoot.path, 'BepInEx', 'TopiaForge', 'staging'));

  List<String> get quitRequests => staging.existsSync()
      ? [
          for (final entity in staging.listSync())
            if (p.basename(entity.path).startsWith('launcher-quit-'))
              p.basename(entity.path),
        ]
      : const [];

  Future<bool> probe(GameInstall _) async {
    if (exitsUnasked && running) {
      exitsUnasked = false;
      _looksBeforeExit = 1;
    }
    return running;
  }

  Future<bool?> liveness(LaunchProcessIdentity _) async {
    if (unknownWhileAsked && running && quitRequests.isNotEmpty) return null;
    if (_looksBeforeExit == 0) running = false;
    if (_looksBeforeExit >= 0) _looksBeforeExit--;
    if (_exitOnNextLook) {
      _exitOnNextLook = false;
      running = false;
    }
    if (running && takes && requestId != null) {
      // The game's manager takes only the request for its own launch.
      final file = File(
        p.join(
          staging.path,
          'launcher-quit-${LaunchStorageKeys.request(requestId!)}.json',
        ),
      );
      if (file.existsSync()) {
        taken.add(LaunchTransport.readLauncherQuit(file.readAsStringSync()));
        file.deleteSync();
        _exitOnNextLook = exits;
      }
    }
    return running;
  }

  Future<int> start(GameProcessRequest _) async {
    running = true;
    return ++starts + 600;
  }

  Future<bool> stop(LaunchProcessIdentity _) async {
    stops++;
    running = false;
    return true;
  }
}

void _registerOwnedGameQuitTests({
  required Directory Function() dataRoot,
  required Directory Function() repositoryRoot,
  required Directory Function() gameRoot,
}) {
  group('restarting a game this launcher started', () {
    const profile = LauncherProfile(id: 'menu', name: 'Menu');

    Future<(LocalLauncherRepository, GameInstall, LaunchResult)> launched(
      _FakeOwnedGame game, {
      OwnedGameQuitPolicy quit = const OwnedGameQuitPolicy(pause: _noPause),
    }) async {
      final (repo, install) = await _prepareProfileLaunchRepository(
        dataRoot: dataRoot(),
        repositoryRoot: repositoryRoot(),
        gameRoot: gameRoot(),
        probe: game.probe,
        starter: game.start,
        identityReader: (pid, path) async => LaunchProcessIdentity(
          pid: pid,
          startTimeUtc: DateTime.utc(2026),
          executablePath: path,
          nativeStartToken: 'generation-$pid',
        ),
        liveness: game.liveness,
        stopper: game.stop,
        quit: quit,
      );
      final first = await repo.launch(install, profile);
      expect(first.processStarted, isTrue, reason: first.message);
      expect(first.process, isNotNull);
      game.requestId = first.requestId;
      return (repo, install, first);
    }

    OwnedGameQuitPolicy counted(void Function(Duration) onPause) =>
        OwnedGameQuitPolicy(
          answerWithin: const Duration(seconds: 2),
          exitWithin: const Duration(seconds: 6),
          lookEvery: const Duration(milliseconds: 250),
          pause: (delay) async => onPause(delay),
        );

    test('asks the game to quit, and stops nothing when it does', () async {
      final game = _FakeOwnedGame(gameRoot());
      final (repo, install, first) = await launched(game);

      final restarted = await repo.restart(install, profile);
      expect(restarted.processStarted, isTrue, reason: restarted.message);
      expect(restarted.replacedGame, ReplacedGameExit.quit);
      expect(restarted.message, startsWith(ReplacedGameExit.quit.message));
      expect(game.stops, 0);
      expect(game.starts, 2);
      // The request named the game's own launch, and nothing is left behind.
      expect(game.taken.single.requestId, first.requestId);
      expect(game.taken.single.quitId, isNot(first.requestId));
      expect(game.quitRequests, isEmpty);
    });

    test('stops a game that never takes the request, once its answer window '
        'has passed', () async {
      var paused = Duration.zero;
      final game = _FakeOwnedGame(gameRoot(), takes: false);
      final (repo, install, _) = await launched(
        game,
        quit: counted((delay) => paused += delay),
      );

      final restarted = await repo.restart(install, profile);
      expect(restarted.processStarted, isTrue, reason: restarted.message);
      expect(restarted.replacedGame, ReplacedGameExit.stopped);
      expect(restarted.message, startsWith(ReplacedGameExit.stopped.message));
      expect(paused, const Duration(seconds: 2));
      expect(game.stops, 1);
      expect(game.quitRequests, isEmpty, reason: 'It is withdrawn.');
    });

    test('stops a game that takes the request but does not exit, once its '
        'exit window has passed', () async {
      var paused = Duration.zero;
      final game = _FakeOwnedGame(gameRoot(), exits: false);
      final (repo, install, _) = await launched(
        game,
        quit: counted((delay) => paused += delay),
      );

      final restarted = await repo.restart(install, profile);
      expect(restarted.processStarted, isTrue, reason: restarted.message);
      expect(restarted.replacedGame, ReplacedGameExit.stopped);
      expect(paused, const Duration(seconds: 6));
      expect(game.taken, hasLength(1));
      expect(game.stops, 1);
    });

    test('never asks a game it did not start to quit', () async {
      final game = _FakeOwnedGame(gameRoot())..running = true;
      final (repo, install) = await _prepareProfileLaunchRepository(
        dataRoot: dataRoot(),
        repositoryRoot: repositoryRoot(),
        gameRoot: gameRoot(),
        probe: game.probe,
        starter: game.start,
        liveness: game.liveness,
        stopper: game.stop,
      );

      final result = await repo.restart(install, profile);
      expect(result.processStarted, isFalse);
      expect(result.message, contains('not owned'));
      expect(result.replacedGame, isNull);
      expect(game.quitRequests, isEmpty);
      expect(game.stops, 0);
      expect(game.starts, 0);
    });

    test('a launcher closed while it waits withdraws the request and stops '
        'nothing', () async {
      late LocalLauncherRepository repository;
      final game = _FakeOwnedGame(gameRoot(), takes: false);
      final (repo, install, _) = await launched(
        game,
        quit: OwnedGameQuitPolicy(
          pause: (_) async => unawaited(repository.dispose()),
        ),
      );
      repository = repo;

      final result = await repo.restart(install, profile);
      expect(result.processStarted, isFalse);
      expect(result.message, contains('No process was stopped.'));
      expect(game.stops, 0);
      expect(game.running, isTrue);
      expect(game.quitRequests, isEmpty);
    });

    test(
      'a game that exits before it is asked is reported as exited',
      () async {
        final game = _FakeOwnedGame(gameRoot());
        final (repo, install, _) = await launched(game);
        game.exitsUnasked = true;

        final restarted = await repo.restart(install, profile);
        expect(restarted.processStarted, isTrue, reason: restarted.message);
        expect(restarted.replacedGame, ReplacedGameExit.exited);
        expect(game.taken, isEmpty);
        expect(game.quitRequests, isEmpty);
        expect(game.stops, 0);
      },
    );

    test('a request that cannot be written leaves stopping the game', () async {
      final game = _FakeOwnedGame(gameRoot());
      final (repo, install, first) = await launched(game);
      // Something other than a file holds the request's name.
      final blocked = Directory(
        p.join(
          game.staging.path,
          'launcher-quit-${LaunchStorageKeys.request(first.requestId!)}.json',
        ),
      )..createSync();

      final restarted = await repo.restart(install, profile);
      expect(restarted.processStarted, isTrue, reason: restarted.message);
      expect(restarted.replacedGame, ReplacedGameExit.stopped);
      expect(game.stops, 1);
      expect(blocked.existsSync(), isTrue);
    });

    test(
      'an unknown answer about the process is never taken as a quit',
      () async {
        var paused = Duration.zero;
        final game = _FakeOwnedGame(gameRoot(), takes: false)
          ..unknownWhileAsked = true;
        final (repo, install, _) = await launched(
          game,
          quit: counted((delay) => paused += delay),
        );

        final restarted = await repo.restart(install, profile);
        expect(restarted.replacedGame, ReplacedGameExit.stopped);
        expect(paused, const Duration(seconds: 2));
        expect(game.stops, 1);
      },
    );
  });
}
