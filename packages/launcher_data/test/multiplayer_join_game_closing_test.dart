import 'dart:async';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_join_content_fixtures.dart';
import 'multiplayer_join_owned_game.dart';

/// How a join's install closes the game this launcher started, once
/// everything is staged and the consent covered it, and what it does with
/// how the game ended. The game is asked to quit, as its Quit button would,
/// and stopped only if it does not. Whether the join goes on is read from the
/// startup journal the game left, never inferred from how it ended: a game
/// whose next start opens in safe mode, where a session is refused, ends the
/// join before anything is installed, with what to do.
void main() {
  /// The join of [StandardJoin] with its downloads cached and the consent
  /// covering the close, and everything a failed join must leave as it is.
  Future<(JoinSessionState, Map<String, Object>)> prepared(
    OwnedGame game,
  ) async {
    final (server, plan) = await StandardJoin.plan();
    game.rig.cache(StandardJoin.downloads);
    return (
      installingJoin(server, plan, closeOwnedGame: true),
      await game.rig.everything(StandardJoin.downloads),
    );
  }

  Future<JoinEvent?> install(OwnedGame game, JoinSessionState installing) =>
      game.rig.repository.installJoinContent(game.rig.install, installing);

  const whatToDo =
      'A game in safe mode cannot join a session. Start Robotopia from the '
      'launcher once and quit it normally, then join again; what was '
      'downloaded is kept. Nothing was installed.';

  /// A join that ended before anything was installed, as the reducer and
  /// the game folder see it.
  Future<void> expectNothingInstalled(
    OwnedGame game,
    JoinSessionState installing,
    JoinFailed failure,
    Map<String, Object> before,
  ) async {
    expect(failure.code, JoinFailureCode.installFailed);
    expect(failure.rollbackComplete, isTrue);
    expect(await game.rig.everything(StandardJoin.downloads), before);
    expect(joinFolders(game.rig.game), isEmpty);
    expect(game.quitRequests, isEmpty, reason: 'taken or withdrawn');
    final ended = JoinSession.reduce(installing, failure);
    expect(ended.stage, JoinStage.ended);
    expect(ended.outcome!.failure!.detail, failure.detail);
  }

  group('a join whose install closes the game this launcher started', () {
    test('goes on when the game quits when asked and records its clean '
        'exit', () async {
      final game = await OwnedGame.start();
      final (installing, _) = await prepared(game);
      expect(await install(game, installing), isA<JoinInstallCompleted>());
      expect(game.running, isFalse);
      expect(game.stopped, 0);
      expect(game.taken.single.requestId, game.requestId);
      expect(game.quitRequests, isEmpty);
      expect(game.steps, containsAllInOrder(['staged', 'quit', 'state']));
      expect(
        packageFolder(game.rig.game, 'server.delta', '1.0.0').existsSync(),
        isTrue,
      );
    });

    test(
      'ends with nothing installed when the game quits when asked but '
      'records no clean exit, since its next start opens in safe mode',
      () async {
        final game = await OwnedGame.start(recordsCleanExit: false);
        final (installing, before) = await prepared(game);
        final failure = await install(game, installing) as JoinFailed;
        expect(
          failure.detail,
          'The running game quit when asked, but did not close cleanly. Its next '
          'start opens in safe mode, with every mod off. $whatToDo',
        );
        expect(game.running, isFalse, reason: 'it did quit');
        expect(game.stopped, 0);
        await expectNothingInstalled(game, installing, failure, before);
      },
    );

    test('stops a game that never takes the request, and ends with nothing '
        'installed since its next start opens in safe mode', () async {
      final game = await OwnedGame.start(quits: false);
      final (installing, before) = await prepared(game);
      final failure = await install(game, installing) as JoinFailed;
      expect(
        failure.detail,
        'The running game did not quit when asked, so it was stopped. Its next '
        'start opens in safe mode, with every mod off. $whatToDo',
      );
      expect(game.stopped, 1);
      expect(game.running, isFalse);
      await expectNothingInstalled(game, installing, failure, before);
    });

    test('goes on after stopping a game whose manager never ran, since its '
        'next start loads its mods', () async {
      final game = await OwnedGame.start(quits: false);
      // The journal is still the previous run's, which ended cleanly.
      game.record('clean-exit');
      final (installing, _) = await prepared(game);
      expect(await install(game, installing), isA<JoinInstallCompleted>());
      expect(game.stopped, 1);
      expect(game.quitRequests, isEmpty, reason: 'withdrawn');
      expect(game.steps, containsAllInOrder(['staged', 'stopped', 'state']));
    });

    test('goes on when the game had already exited cleanly as the install '
        'came to close it', () async {
      final game = await OwnedGame.start(exitsBeforeAsked: true);
      final (installing, _) = await prepared(game);
      expect(await install(game, installing), isA<JoinInstallCompleted>());
      expect(game.stopped, 0);
      expect(game.taken, isEmpty, reason: 'nothing to ask');
      expect(game.steps, containsAllInOrder(['staged', 'exited', 'state']));
    });

    test('ends with nothing installed when the game had crashed as the '
        'install came to close it, since its next start opens in safe '
        'mode', () async {
      final game = await OwnedGame.start(
        exitsBeforeAsked: true,
        recordsCleanExit: false,
      );
      final (installing, before) = await prepared(game);
      final failure = await install(game, installing) as JoinFailed;
      expect(
        failure.detail,
        'The running game had already exited without closing cleanly. Its '
        'next start opens in safe mode, with every mod off. $whatToDo',
      );
      expect(game.stopped, 0);
      expect(game.taken, isEmpty);
      await expectNothingInstalled(game, installing, failure, before);
    });

    test('stops nothing and installs nothing when the launcher closes while '
        'it waits for the game to quit', () async {
      late OwnedGame game;
      game = await OwnedGame.start(
        quits: false,
        pause: (_) async => unawaited(game.rig.repository.dispose()),
      );
      final (installing, _) = await prepared(game);
      final failure = await install(game, installing) as JoinFailed;
      expect(failure.code, JoinFailureCode.installFailed);
      expect(failure.rollbackComplete, isTrue);
      expect(
        failure.detail,
        'The launcher closed while it asked Robotopia to quit, so nothing was '
        'stopped. Nothing was installed.',
      );
      expect(game.running, isTrue);
      expect(game.stopped, 0);
      expect(game.quitRequests, isEmpty, reason: 'withdrawn');
      expect(
        packageFolder(game.rig.game, 'server.delta', '1.0.0').existsSync(),
        isFalse,
      );
      expect(joinFolders(game.rig.game), isEmpty);
    });

    test('ends the join as owned when the game neither quits nor can be '
        'stopped', () async {
      final game = await OwnedGame.start(quits: false, stops: false);
      final (installing, before) = await prepared(game);
      final failure = await install(game, installing) as JoinFailed;
      expect(failure.code, JoinFailureCode.gameRunning);
      expect(failure.gameOwnership, JoinGameOwnership.owned);
      expect(failure.detail, contains('could not be closed'));
      expect(game.running, isTrue);
      expect(game.quitRequests, isEmpty, reason: 'withdrawn before the stop');
      expect(await game.rig.everything(StandardJoin.downloads), before);
      expect(joinFolders(game.rig.game), isEmpty);
    });
  });
}
