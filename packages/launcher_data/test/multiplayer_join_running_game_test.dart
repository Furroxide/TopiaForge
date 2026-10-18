import 'dart:convert';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_join_content_fixtures.dart';
import 'multiplayer_join_rig.dart';

/// A join's install beside a running game. The game writes its manager state
/// when it quits, so nothing installs beside it. A game this launcher started
/// is closed only when the consent covered it, and only once everything is
/// staged, so a join that is refused or cancelled leaves the player's game
/// running, with the progress in it.
void main() {
  test('a game the launcher did not start ends the join', () async {
    final rig = await JoinRig.create(probe: (_) async => true);
    await preparePlayer(rig);
    final (server, plan) = await StandardJoin.plan();
    rig.cache(StandardJoin.downloads);
    final records = managerRecords(rig.game);
    final event = await rig.repository.installJoinContent(
      rig.install,
      installingJoin(server, plan, closeOwnedGame: true),
    );
    final failure = event as JoinFailed;
    expect(failure.code, JoinFailureCode.gameRunning);
    expect(failure.gameOwnership, JoinGameOwnership.foreign);
    expect(failure.detail, contains('Close the game, then join again'));
    expect(managerRecords(rig.game), records);
    expect(
      packageFolder(rig.game, 'server.delta', '1.0.0').existsSync(),
      isFalse,
    );
    expect(joinFolders(rig.game), isEmpty);
    final ended = JoinSession.reduce(installingJoin(server, plan), failure);
    expect(ended.outcome!.failure!.gameOwnership, JoinGameOwnership.foreign);
  });

  group('a game the launcher started', () {
    test('is closed once everything is staged, when the consent covered it, '
        'then the join installs', () async {
      final game = await _OwnedGame.start();
      final (server, plan) = await StandardJoin.plan();
      game.rig.cache(StandardJoin.downloads);
      await game.rig.installOwn(StandardJoin.gamma);
      final event = await game.rig.repository.installJoinContent(
        game.rig.install,
        installingJoin(server, plan, closeOwnedGame: true),
      );
      expect(event, isA<JoinInstallCompleted>());
      expect(game.stopped, 1);
      expect(
        game.steps,
        containsAllInOrder(['staged', 'stopped', 'state', 'record']),
        reason: 'nothing the game reads changes before it is closed',
      );
      expect(
        packageFolder(game.rig.game, 'server.delta', '1.0.0').existsSync(),
        isTrue,
      );
    });

    test('keeps running when the consent did not cover closing it', () async {
      final game = await _OwnedGame.start();
      final (server, plan) = await StandardJoin.plan();
      game.rig.cache(StandardJoin.downloads);
      final before = await game.rig.everything(StandardJoin.downloads);
      final event = await game.rig.repository.installJoinContent(
        game.rig.install,
        installingJoin(server, plan),
      );
      final failure = event as JoinFailed;
      expect(failure.code, JoinFailureCode.gameRunning);
      expect(failure.gameOwnership, JoinGameOwnership.owned);
      expect(failure.detail, contains('Save your game and quit it'));
      expect(failure.detail, contains('what was downloaded is kept'));
      expect(game.stopped, 0);
      expect(game.steps, isNot(contains('staged')));
      expect(await game.rig.everything(StandardJoin.downloads), before);
      expect(joinFolders(game.rig.game), isEmpty);
    });

    test('keeps running when the join is refused', () async {
      final game = await _OwnedGame.start();
      await game.rig.installOwn(
        TestArchive('own.alpha', '2.0.0', name: 'Alpha', payload: 'mine'),
      );
      final server = await verifiedServer([StandardJoin.serverAlpha.package]);
      final plan = planJoin(server, {
        StandardJoin.serverAlpha.sha256Hex: StandardJoin.serverAlpha.offeredAt(
          'https://packages.example/alpha.topiaforgemod',
        ),
      });
      game.rig.cache([StandardJoin.serverAlpha]);
      final event = await game.rig.repository.installJoinContent(
        game.rig.install,
        installingJoin(server, plan, closeOwnedGame: true),
      );
      final failure = event as JoinFailed;
      expect(failure.code, JoinFailureCode.installFailed);
      expect(failure.detail, contains('Nothing was installed'));
      expect(game.stopped, 0, reason: 'a join that changes nothing');
    });

    test(
      'keeps running when the join is cancelled before anything changed',
      () async {
        final cancellation = CancellationToken();
        final game = await _OwnedGame.start(
          onStep: (step) {
            if (step == 'staged') cancellation.cancel();
          },
        );
        final (server, plan) = await StandardJoin.plan();
        game.rig.cache(StandardJoin.downloads);
        final installing = JoinSession.reduce(
          installingJoin(server, plan, closeOwnedGame: true),
          const JoinCancelRequested(),
        );
        final event = await game.rig.repository.installJoinContent(
          game.rig.install,
          installing,
          cancellation: cancellation,
        );
        expect(event, isA<JoinInstallUndone>());
        expect(
          game.stopped,
          0,
          reason: 'a cancel stops the join, not the game',
        );
        expect(joinFolders(game.rig.game), isEmpty);
      },
    );

    test('that will not close ends the join as owned', () async {
      final game = await _OwnedGame.start(closes: false);
      final (server, plan) = await StandardJoin.plan();
      game.rig.cache(StandardJoin.downloads);
      final before = await game.rig.everything(StandardJoin.downloads);
      final event = await game.rig.repository.installJoinContent(
        game.rig.install,
        installingJoin(server, plan, closeOwnedGame: true),
      );
      final failure = event as JoinFailed;
      expect(failure.code, JoinFailureCode.gameRunning);
      expect(failure.gameOwnership, JoinGameOwnership.owned);
      expect(failure.detail, contains('could not be closed'));
      expect(await game.rig.everything(StandardJoin.downloads), before);
      expect(joinFolders(game.rig.game), isEmpty);
    });

    test('whose quit changed the player\'s mods ends the join', () async {
      // The manager state the game writes as it quits now records Delta, so
      // it is the player's: what was staged no longer holds.
      late _OwnedGame game;
      game = await _OwnedGame.start(
        onStop: () {
          final file = managerStateFile(game.rig.game);
          final state = jsonDecode(file.readAsStringSync()) as Map;
          final mods = state['mods'] as List;
          mods.add({
            ...(mods.first as Map),
            'id': 'server.delta',
            'name': 'Delta',
            'version': '0.9.0',
          });
          file.writeAsStringSync(jsonEncode(state));
        },
      );
      final (server, plan) = await StandardJoin.plan();
      game.rig.cache(StandardJoin.downloads);
      final event = await game.rig.repository.installJoinContent(
        game.rig.install,
        installingJoin(server, plan, closeOwnedGame: true),
      );
      final failure = event as JoinFailed;
      expect(failure.code, JoinFailureCode.installFailed);
      expect(failure.rollbackComplete, isTrue);
      expect(failure.detail, contains('changed while the join was preparing'));
      expect(serverContentFile(game.rig.game).existsSync(), isFalse);
      expect(
        packageFolder(game.rig.game, 'server.delta', '1.0.0').existsSync(),
        isFalse,
      );
      expect(joinFolders(game.rig.game), isEmpty);
    });
  });

  test('a game started while the join prepared ends the join', () async {
    var running = false;
    final rig = await JoinRig.create(
      probe: (_) async => running,
      hook: (step) {
        if (step == 'staged') running = true;
      },
    );
    await preparePlayer(rig);
    final (server, plan) = await StandardJoin.plan();
    rig.cache(StandardJoin.downloads);
    final before = await rig.everything(StandardJoin.downloads);
    final event = await rig.repository.installJoinContent(
      rig.install,
      installingJoin(server, plan, closeOwnedGame: true),
    );
    final failure = event as JoinFailed;
    expect(failure.code, JoinFailureCode.gameRunning);
    expect(failure.gameOwnership, JoinGameOwnership.foreign);
    running = false;
    expect(await rig.everything(StandardJoin.downloads), before);
    expect(joinFolders(rig.game), isEmpty);
  });
}

/// A player of [StandardJoin] whose game this launcher started and still
/// runs. [steps] records the install's steps, and `stopped` where the game
/// was closed among them.
final class _OwnedGame {
  _OwnedGame._(this.rig, this.steps);

  final JoinRig rig;
  final List<String> steps;
  var stopped = 0;

  static Future<_OwnedGame> start({
    bool closes = true,
    void Function(String step)? onStep,
    void Function()? onStop,
  }) async {
    var running = false;
    final steps = <String>[];
    late _OwnedGame game;
    late JoinRig rig;
    rig = await JoinRig.create(
      withRuntime: true,
      hook: (step) {
        steps.add(step);
        onStep?.call(step);
      },
      probe: (_) async => running,
      creator: (_) async {
        running = true;
        final identity = LaunchProcessIdentity(
          pid: 901,
          executablePath: rig.install.executablePath,
          startTimeUtc: DateTime.utc(2026, 10, 18),
          nativeStartToken: 'generation:1',
        );
        return LaunchProcessReceipt(pid: identity.pid, identity: identity);
      },
      liveness: (_) async => closes ? running : null,
      stopper: (_) async {
        if (!closes) throw StateError('not stopped');
        onStop?.call();
        game.stopped++;
        steps.add('stopped');
        running = false;
        return true;
      },
    );
    game = _OwnedGame._(rig, steps);
    await preparePlayer(rig);
    final profile = (await rig.repository.loadSnapshot()).profiles.first;
    final launched = await rig.repository.launch(rig.install, profile);
    expect(launched.process, isNotNull, reason: launched.message);
    return game;
  }
}
