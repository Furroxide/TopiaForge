import 'dart:async';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'multiplayer_join_content_fixtures.dart';
import 'multiplayer_join_owned_game.dart';
import 'multiplayer_join_rig.dart';

LocalMultiplayerRepository _facade(JoinRig rig) {
  final multiplayer = LocalMultiplayerRepository(
    dataRoot: rig.dataRoot,
    service: MultiplayerServiceConfiguration.none,
    controlPlane: const UnavailableControlPlane(),
    content: rig.repository,
    gameProbe: (_) async => false,
  );
  addTearDown(multiplayer.dispose);
  return multiplayer;
}

/// A consented join of a LAN listing of a server that runs [archives], all
/// installed, taken through its content stages to launching.
Future<JoinSessionState> _launchingJoin(
  JoinRig rig,
  LocalMultiplayerRepository multiplayer,
  List<TestArchive> archives, {
  bool closeOwnedGame = false,
}) async {
  final server = await verifiedServer([
    for (final archive in archives) archive.package,
  ]);
  final plan = planJoin(server, {
    for (final archive in archives)
      archive.sha256Hex: JoinPackageResolution.installed(archive.manifest),
  });
  var state =
      [
        JoinDescriptorVerified(server),
        JoinPlanReady(plan),
        JoinConsentGiven(closeOwnedGameAcknowledged: closeOwnedGame),
      ].fold(
        JoinSession.start(
          JoinListingTarget(
            serverId: server.serverId,
            source: ServerListingSource.lan,
          ),
          privacy: SessionPrivacy.relayOnly,
        ),
        JoinSession.reduce,
      );
  while (state.stage != JoinStage.launching && !state.isEnded) {
    final event = await multiplayer.answerJoin(state, install: rig.install);
    state = JoinSession.reduce(state, event!);
  }
  expect(state.stage, JoinStage.launching);
  return state;
}

/// The archives of the packages the running game loads.
Future<List<TestArchive>> _running(OwnedGame game) async {
  final snapshot = await game.rig.repository.loadSnapshot();
  final preview = await game.rig.repository.previewLaunch(
    game.rig.install,
    snapshot.profiles.first,
  );
  final archives = {
    for (final archive in [
      StandardJoin.alpha,
      StandardJoin.beta,
      StandardJoin.gamma,
    ])
      '${archive.id}@${archive.version}': archive,
  };
  return [
    for (final package in preview.packages)
      archives['${package.id}@${package.version}']!,
  ];
}

File _join(OwnedGame game) => File(
  p.join(
    managerStaging(game.rig.game).path,
    'session-join-${LaunchStorageKeys.request(game.requestId)}.json',
  ),
);

void main() {
  test('a game this launcher started that runs the server\'s mods joins live, '
      'without a restart', () async {
    final game = await OwnedGame.start();
    final multiplayer = _facade(game.rig);
    final runs = await _running(game);
    expect(runs, isNotEmpty);
    final launching = await _launchingJoin(game.rig, multiplayer, runs);
    expect(
      await multiplayer.joinGameRunning(game.rig.install),
      JoinGameRunning.owned,
    );

    final launch = await multiplayer.launchJoin(
      launching,
      install: game.rig.install,
      intentId: 'join-live',
    );

    expect(launch, isA<JoinLaunchStarted>());
    launch as JoinLaunchStarted;
    expect(launch.live, isTrue);
    expect(game.running, isTrue, reason: 'the game keeps running');
    expect(game.taken, isEmpty, reason: 'nothing asked it to quit');
    final staged = LaunchTransport.readSessionJoin(
      _join(game).readAsStringSync(),
    );
    expect(staged.requestId, game.requestId);
    expect(staged.joinId, launch.requestId);
    expect(staged.packageSetDigest, launch.activity!.packageDigest);
    expect(staged.session.target, isA<LaunchListingTarget>());
    expect(staged.session.intentId, 'join-live');
    expect(launch.activity!.command, 'join-session');
    expect(launch.activity!.requestId, launch.requestId);

    // A cancel before the game took it withdraws it, so nothing joins.
    expect(
      await multiplayer.withdrawJoinLaunch(game.rig.install, launch),
      isTrue,
    );
    expect(_join(game).existsSync(), isFalse);
  });

  test(
    'a game this launcher started that runs other mods is asked to quit, '
    'then starts again for the server, when the consent covered it',
    () async {
      final game = await OwnedGame.start();
      final multiplayer = _facade(game.rig);
      final launching = await _launchingJoin(game.rig, multiplayer, [
        StandardJoin.alpha,
      ], closeOwnedGame: true);

      final launch = await multiplayer.launchJoin(
        launching,
        install: game.rig.install,
        intentId: 'join-restart',
      );

      expect(launch, isA<JoinLaunchStarted>());
      launch as JoinLaunchStarted;
      expect(launch.live, isFalse);
      expect(game.taken, hasLength(1), reason: 'asked to quit, as QUIT would');
      expect(game.stopped, 0);
      expect(launch.replacedGame?.exit, ReplacedGameExit.quit);
      expect(launch.replacedGame?.safeMode, isFalse);
      expect(game.running, isTrue, reason: 'it started again for the server');
      expect(_join(game).existsSync(), isFalse);
    },
  );

  test('without that consent, the running game ends the join and keeps '
      'running', () async {
    final game = await OwnedGame.start();
    final multiplayer = _facade(game.rig);
    final launching = await _launchingJoin(game.rig, multiplayer, [
      StandardJoin.alpha,
    ]);

    final launch = await multiplayer.launchJoin(
      launching,
      install: game.rig.install,
      intentId: 'join-no-consent',
    );

    final failure = (launch as JoinLaunchRefused).failure;
    expect(failure.code, JoinFailureCode.gameRunning);
    expect(failure.gameOwnership, JoinGameOwnership.owned);
    expect(failure.detail, contains('Save your game and quit it'));
    expect(game.taken, isEmpty);
    expect(game.running, isTrue);
  });

  test('a quit that leaves the next start in safe mode ends the join before '
      'anything starts', () async {
    final game = await OwnedGame.start(recordsCleanExit: false);
    final multiplayer = _facade(game.rig);
    final launching = await _launchingJoin(game.rig, multiplayer, [
      StandardJoin.alpha,
    ], closeOwnedGame: true);

    final launch = await multiplayer.launchJoin(
      launching,
      install: game.rig.install,
      intentId: 'join-unclean',
    );

    final failure = (launch as JoinLaunchRefused).failure;
    expect(failure.code, JoinFailureCode.launchBlocked);
    expect(failure.detail, contains('did not close cleanly'));
    expect(failure.detail, contains('cannot join a session'));
    expect(game.running, isFalse, reason: 'nothing started in safe mode');
  });

  test(
    'a join the game hands over is taken once, for that game only',
    () async {
      final game = await OwnedGame.start();
      final arrivals = <JoinHandoffArrival>[];
      final arrived = Completer<void>();
      final subscription = game.rig.repository.sessionHandoffs.listen((
        arrival,
      ) {
        arrivals.add(arrival);
        if (!arrived.isCompleted) arrived.complete();
      });
      addTearDown(subscription.cancel);
      final staging = managerStaging(game.rig.game)
        ..createSync(recursive: true);
      const server =
          'c5f39060271ebd2f22d9bf155a26bf459274e31b795145fd9d862dc206d98bb3';
      File write(String requestId, String handoffId) =>
          File(
            p.join(
              staging.path,
              'session-handoff-${LaunchStorageKeys.request(requestId)}.json',
            ),
          )..writeAsStringSync(
            LaunchTransport.writeSessionHandoff(
              SessionHandoff(
                requestId: requestId,
                handoffId: handoffId,
                target: LaunchHandoffListing(
                  serverId: server,
                  source: ServerListingSource.lan,
                ),
              ),
            ),
          );
      // Another process's handoff is not this launcher's to take.
      final foreign = write(
        '0f1e2d3c4b5a69788796a5b4c3d2e1f0',
        'handoff-foreign',
      );
      final own = write(game.requestId, 'handoff-own');

      await arrived.future.timeout(const Duration(seconds: 10));

      expect(arrivals, hasLength(1));
      final handoff = arrivals.single.handoff;
      expect(handoff.handoffId, 'handoff-own');
      expect(handoff.requestId, game.requestId);
      expect((handoff.target as LaunchHandoffListing).serverId, server);
      expect(arrivals.single.install.path, game.rig.install.path);
      expect('${arrivals.single}', isNot(contains(server)));
      expect(own.existsSync(), isFalse, reason: 'taken once');
      expect(foreign.existsSync(), isTrue);
    },
  );
}
