/// A game this launcher started, as a join's install comes to close it. Its
/// manager takes the launcher's quit request for the launch the game was
/// started with and quits, or never takes it, or the game has already ended,
/// or ends just before it is stopped; stopping it works or fails, and
/// another copy of the game may run once it has ended. Like the manager, it
/// keeps a startup journal: startup complete while it runs, and a clean exit
/// when it quits cleanly.
library;

import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'multiplayer_join_content_fixtures.dart';
import 'multiplayer_join_rig.dart';

Future<void> _noPause(Duration _) async {}

/// The player of [StandardJoin], whose game this launcher started and still
/// runs when the install comes to close it.
final class OwnedGame {
  OwnedGame._({
    required this.verifiable,
    required this.quits,
    required this.recordsCleanExit,
    required this.exitsBeforeAsked,
    required this.exitsBeforeStopped,
    required this.stops,
    required this.anotherCopyRuns,
    this.onClose,
  });

  late final JoinRig rig;

  /// The launch request the game was started with.
  late final String requestId;

  /// Whether its process answers liveness; false as if its handle cannot be
  /// read, and then the launcher never asks it to quit.
  final bool verifiable;

  /// Whether its manager takes the launcher's request and quits; false as if
  /// the manager never loaded, or the game hangs.
  final bool quits;

  /// Whether its quit records a clean exit; false as if its mods took longer
  /// than the manager's 10 seconds to unload, or it crashed.
  final bool recordsCleanExit;

  /// Whether it ends on its own as the install comes to close it: after
  /// the install's look once everything is staged, before it is asked.
  final bool exitsBeforeAsked;

  /// Whether it ends on its own just before the launcher stops it, so the
  /// stop finds it gone.
  final bool exitsBeforeStopped;

  /// Whether stopping it works.
  final bool stops;

  /// Whether another copy of the game, one this launcher did not start, runs
  /// once this one has ended.
  final bool anotherCopyRuns;

  /// Runs as the game quits or exits, as its quit writes the manager state.
  final void Function()? onClose;

  /// The install's steps, with `quit`, `stopped` or `exited` where the game
  /// ended among them.
  final steps = <String>[];

  /// The quit requests its manager took.
  final taken = <LauncherQuitRequest>[];
  final launchRequests = <String>[];
  var running = false;
  var stopped = 0;
  var _quitting = false;
  var _closing = false;
  var _ended = false;

  /// Starts the game through the repository, with the player of
  /// [StandardJoin] prepared. [pause] is the quit policy's wait between
  /// looks; disposing the repository there closes the launcher mid-wait.
  static Future<OwnedGame> start({
    bool verifiable = true,
    bool quits = true,
    bool recordsCleanExit = true,
    bool exitsBeforeAsked = false,
    bool exitsBeforeStopped = false,
    bool stops = true,
    bool anotherCopyRuns = false,
    void Function(String step)? onStep,
    void Function()? onClose,
    Future<void> Function(Duration delay)? pause,
  }) async {
    final game = OwnedGame._(
      verifiable: verifiable,
      quits: quits,
      recordsCleanExit: recordsCleanExit,
      exitsBeforeAsked: exitsBeforeAsked,
      exitsBeforeStopped: exitsBeforeStopped,
      stops: stops,
      anotherCopyRuns: anotherCopyRuns,
      onClose: onClose,
    );
    game.rig = await JoinRig.create(
      withRuntime: true,
      hook: (step) {
        game.steps.add(step);
        onStep?.call(step);
      },
      probe: (_) async {
        // The install's look at the game once everything is staged.
        if (game.steps.contains('staged')) game._closing = true;
        return game.running || (game.anotherCopyRuns && game._ended);
      },
      creator: (request) async {
        game.launchRequests.add(
          File(
            request.environment[ProfileLaunchConfigurationV4
                .environmentVariable]!,
          ).readAsStringSync(),
        );
        game.running = true;
        game.record('startup-complete');
        final identity = LaunchProcessIdentity(
          pid: 901,
          executablePath: game.rig.install.executablePath,
          startTimeUtc: DateTime.utc(2026, 10, 18),
          nativeStartToken: 'generation:1',
        );
        return LaunchProcessReceipt(pid: identity.pid, identity: identity);
      },
      liveness: (_) => game._liveness(),
      stopper: (_) => game._stop(),
      ownedGameQuit: OwnedGameQuitPolicy(pause: pause ?? _noPause),
    );
    await preparePlayer(game.rig);
    final profile = (await game.rig.repository.loadSnapshot()).profiles.first;
    final launched = await game.rig.repository.launch(
      game.rig.install,
      profile,
    );
    expect(launched.process, isNotNull, reason: launched.message);
    game.requestId = launched.requestId!;
    return game;
  }

  File get journal => File(
    p.join(rig.game.path, 'BepInEx', 'TopiaForge', 'startup-journal.json'),
  );

  /// Writes the startup journal as the manager does.
  void record(String state) => journal
    ..parent.createSync(recursive: true)
    ..writeAsStringSync(
      jsonEncode({
        'currentModId': '',
        'schemaVersion': 1,
        'sessionId': '0f1e2d3c4b5a69788796a5b4c3d2e1f0',
        'startedAtUtc': '2026-10-19T00:00:00.0000000Z',
        'state': state,
        'updatedAtUtc': '2026-10-19T00:00:01.0000000Z',
      }),
    );

  /// The quit requests waiting in manager staging.
  List<String> get quitRequests {
    final staging = managerStaging(rig.game);
    if (!staging.existsSync()) return const [];
    return [
      for (final entity in staging.listSync())
        if (p.basename(entity.path).startsWith('launcher-quit-'))
          p.basename(entity.path),
    ];
  }

  Future<bool?> _liveness() async {
    if (!verifiable) return null;
    if (running && exitsBeforeAsked && _closing) {
      // It ended on its own between the install's look and its close.
      _end('exited');
    } else if (_quitting) {
      // The look after it took the request.
      _quitting = false;
      _end('quit');
    } else if (running && quits) {
      // The manager takes only the request for its own launch.
      final file = File(
        p.join(
          managerStaging(rig.game).path,
          'launcher-quit-${LaunchStorageKeys.request(requestId)}.json',
        ),
      );
      if (file.existsSync()) {
        taken.add(LaunchTransport.readLauncherQuit(file.readAsStringSync()));
        file.deleteSync();
        _quitting = true;
      }
    }
    return running;
  }

  /// Ends the game on its own, as a crash or a quit from its own menu
  /// would, before the install comes to close it.
  void exitOnItsOwn() => _end('exited');

  void _end(String step) {
    running = false;
    _ended = true;
    if (recordsCleanExit) record('clean-exit');
    onClose?.call();
    steps.add(step);
  }

  Future<bool> _stop() async {
    if (!stops) throw StateError('The process could not be stopped.');
    if (exitsBeforeStopped) {
      // It ended on its own a moment before: the stop finds it gone.
      _end('exited');
      return false;
    }
    stopped++;
    running = false;
    _ended = true;
    steps.add('stopped');
    return true;
  }
}
