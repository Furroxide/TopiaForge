part of 'launcher_data_test.dart';

Future<void> _noPause(Duration _) async {}

/// A game this launcher started, as a restart sees it. Its manager takes the
/// launcher's quit request for the launch it was started with and quits, or
/// never takes it, or takes it and hangs. Like the manager, it keeps a startup
/// journal: startup complete while it runs, and a clean exit when it quits
/// cleanly.
final class _FakeOwnedGame {
  _FakeOwnedGame(
    this.gameRoot, {
    this.takes = true,
    this.exits = true,
    this.recordsCleanExit = true,
  });

  final Directory gameRoot;

  /// Whether its manager takes the request: false as if it never loaded.
  final bool takes;

  /// Whether it exits once it has taken the request: false as if it hung.
  final bool exits;

  /// Whether its quit records a clean exit: false as if its mods took longer
  /// than the manager's 10 seconds to unload.
  final bool recordsCleanExit;
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

  File get journal => File(
    p.join(gameRoot.path, 'BepInEx', 'TopiaForge', 'startup-journal.json'),
  );

  /// Writes the startup journal as the manager does.
  void record(String state, {String currentModId = ''}) => journal
    ..parent.createSync(recursive: true)
    ..writeAsStringSync(
      jsonEncode({
        'currentModId': currentModId,
        'schemaVersion': 1,
        'sessionId': '0f1e2d3c4b5a69788796a5b4c3d2e1f0',
        'startedAtUtc': '2026-10-19T00:00:00.0000000Z',
        'state': state,
        'updatedAtUtc': '2026-10-19T00:00:01.0000000Z',
      }),
    );

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
      if (recordsCleanExit) record('clean-exit');
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
    record('startup-complete');
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
      GameProcessStarter? starter,
    }) async {
      final (repo, install) = await _prepareProfileLaunchRepository(
        dataRoot: dataRoot(),
        repositoryRoot: repositoryRoot(),
        gameRoot: gameRoot(),
        probe: game.probe,
        starter: starter ?? game.start,
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
      expect(restarted.replacedGame?.exit, ReplacedGameExit.quit);
      expect(restarted.replacedGame?.safeMode, isFalse);
      expect(
        restarted.message,
        startsWith('The running game quit when asked.'),
      );
      expect(restarted.message, isNot(contains('safe mode')));
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
      expect(restarted.replacedGame?.exit, ReplacedGameExit.stopped);
      expect(restarted.replacedGame?.safeMode, isTrue);
      expect(
        restarted.message,
        startsWith(
          'The running game did not quit when asked, so it was stopped. This '
          'start opens in safe mode, with every mod off; restart it from the '
          'launcher to load them.',
        ),
      );
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
      expect(restarted.replacedGame?.exit, ReplacedGameExit.stopped);
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

    for (final (window, takes) in [('answer', false), ('exit', true)]) {
      test('a launcher closed during the last pause of the $window window '
          'stops nothing either', () async {
        late LocalLauncherRepository repository;
        var pauses = 0;
        final game = _FakeOwnedGame(gameRoot(), takes: takes, exits: false);
        final (repo, install, _) = await launched(
          game,
          quit: OwnedGameQuitPolicy(
            answerWithin: const Duration(milliseconds: 200),
            exitWithin: const Duration(milliseconds: 300),
            lookEvery: const Duration(milliseconds: 100),
            // The window's last pause: the next look is past its bound.
            pause: (_) async {
              if (++pauses == (takes ? 3 : 2)) unawaited(repository.dispose());
            },
          ),
        );
        repository = repo;

        final result = await repo.restart(install, profile);
        expect(result.processStarted, isFalse);
        expect(result.message, contains('No process was stopped.'));
        expect(game.stops, 0);
        expect(game.running, isTrue);
        expect(game.starts, 1);
        expect(game.quitRequests, isEmpty);
      });
    }

    test(
      'a game that exits before it is asked is reported as exited',
      () async {
        final game = _FakeOwnedGame(gameRoot());
        final (repo, install, _) = await launched(game);
        game.exitsUnasked = true;

        final restarted = await repo.restart(install, profile);
        expect(restarted.processStarted, isTrue, reason: restarted.message);
        expect(restarted.replacedGame?.exit, ReplacedGameExit.exited);
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
      expect(restarted.replacedGame?.exit, ReplacedGameExit.stopped);
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
        expect(restarted.replacedGame?.exit, ReplacedGameExit.stopped);
        expect(paused, const Duration(seconds: 2));
        expect(game.stops, 1);
      },
    );

    test('a game that quits without recording a clean exit leaves this start '
        'in safe mode, and says so', () async {
      final game = _FakeOwnedGame(gameRoot(), recordsCleanExit: false);
      final (repo, install, _) = await launched(game);

      final restarted = await repo.restart(install, profile);
      expect(restarted.processStarted, isTrue, reason: restarted.message);
      expect(restarted.replacedGame?.exit, ReplacedGameExit.quit);
      expect(restarted.replacedGame?.safeMode, isTrue);
      expect(
        restarted.message,
        startsWith(
          'The running game quit when asked, but did not close cleanly. This '
          'start opens in safe mode, with every mod off; restart it from the '
          'launcher to load them.',
        ),
      );
      expect(game.stops, 0);
    });

    test('safe mode is claimed only when the journal left for the next start '
        'means it', () async {
      // What the manager makes of the journal at its next start.
      final cases = <(String, void Function(_FakeOwnedGame), bool)>[
        ('a recorded clean exit', (game) => game.record('clean-exit'), false),
        ('no journal', (game) => game.journal.deleteSync(), false),
        ('startup complete, no clean exit', (game) {}, true),
        ('startup never completed', (game) => game.record('loaded'), true),
        ('an unknown state', (game) => game.record('closing'), true),
        (
          'a journal it cannot read',
          (game) => game.journal.writeAsStringSync('{"state":'),
          true,
        ),
        (
          'another schema',
          (game) => game.journal.writeAsStringSync(
            jsonEncode({'schemaVersion': 2, 'state': 'clean-exit'}),
          ),
          true,
        ),
        // One mod is turned off then, not every mod, and the manager says so.
        (
          'a mod that was loading',
          (game) => game.record('loading', currentModId: 'example.loading'),
          false,
        ),
      ];
      for (final (name, journal, safeMode) in cases) {
        final game = _FakeOwnedGame(gameRoot())..exitsUnasked = true;
        final (repo, install, _) = await launched(game);
        journal(game);

        final restarted = await repo.restart(install, profile);
        expect(restarted.replacedGame?.exit, ReplacedGameExit.exited);
        expect(restarted.replacedGame?.safeMode, safeMode, reason: name);
        expect(
          restarted.message.contains('safe mode'),
          safeMode,
          reason: '$name: ${restarted.message}',
        );
        await repo.dispose();
      }
    });

    test('a stopped game whose manager never ran its startup claims no safe '
        'mode', () async {
      // The journal is still the previous run's, which ended cleanly.
      final game = _FakeOwnedGame(gameRoot(), takes: false);
      final (repo, install, _) = await launched(game);
      game.record('clean-exit');

      final restarted = await repo.restart(install, profile);
      expect(restarted.replacedGame?.exit, ReplacedGameExit.stopped);
      expect(restarted.replacedGame?.safeMode, isFalse);
      expect(
        restarted.message,
        startsWith(
          'The running game did not quit when asked, so it was stopped. '
          'Started replacement',
        ),
      );
    });

    test('a stop that no replacement follows says the next start opens in '
        'safe mode, not this one', () async {
      final game = _FakeOwnedGame(gameRoot(), takes: false);
      final (repo, install, _) = await launched(
        game,
        starter: (request) async {
          if (game.starts == 1) throw StateError('Creation was refused.');
          return game.start(request);
        },
      );

      final result = await repo.restart(install, profile);
      expect(result.processStarted, isFalse);
      expect(game.stops, 1);
      expect(
        result.message,
        startsWith(
          'The running game did not quit when asked, so it was stopped. Its '
          'next start opens in safe mode, with every mod off. TopiaForge could '
          'not be started.',
        ),
      );
    });
  });
}
