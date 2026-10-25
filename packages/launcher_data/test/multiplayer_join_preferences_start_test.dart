import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'multiplayer_join_content_fixtures.dart';
import 'multiplayer_join_owned_game.dart';

/// A game this launcher started that journals [changes] as it quits, as a
/// player's last choices in game are saved, with the multiplayer facade over
/// its launcher. The launcher has already run a round with the game.
Future<(OwnedGame, LocalMultiplayerRepository)> _quittingWith(
  List<MultiplayerPreferenceChange> changes, {
  String? displayName,
}) async {
  late final OwnedGame game;
  game = await OwnedGame.start(
    onClose: () {
      File(
        p.join(
          managerStaging(game.rig.game).path,
          'multiplayer-preferences-journal-v1.json',
        ),
      ).writeAsStringSync(
        LaunchTransport.writePreferencesJournal(
          MultiplayerPreferencesJournal(
            '5f0c2a9e8d7b6a5f4e3d2c1b0a998877',
            changes,
          ),
        ),
      );
    },
  );
  final multiplayer = LocalMultiplayerRepository(
    dataRoot: game.rig.dataRoot,
    service: MultiplayerServiceConfiguration.none,
    controlPlane: const UnavailableControlPlane(),
    content: game.rig.repository,
    gameProbe: (_) async => false,
  );
  addTearDown(multiplayer.dispose);
  if (displayName != null) {
    await multiplayer.changePreferences(
      MultiplayerPreferencesChange(displayName: displayName),
    );
  }
  expect(
    await multiplayer.syncWithGame(game.rig.install),
    isA<MultiplayerPreferencesSynced>(),
  );
  return (game, multiplayer);
}

/// [join] taken through its content stages to launching.
Future<JoinSessionState> _toLaunching(
  OwnedGame game,
  LocalMultiplayerRepository multiplayer,
  JoinSessionState join,
) async {
  var state = join;
  while (state.stage != JoinStage.launching && !state.isEnded) {
    state = JoinSession.reduce(
      state,
      (await multiplayer.answerJoin(state, install: game.rig.install))!,
    );
  }
  expect(state.stage, JoinStage.launching);
  return state;
}

void main() {
  test('an owned restart uses the name and network choice the game saves as '
      'it quits', () async {
    final (game, multiplayer) = await _quittingWith([
      DisplayNameChange(sequence: 1, displayName: 'Robin'),
      NetworkChange(sequence: 2, lanDiscovery: false, upnp: false),
    ], displayName: 'Sam');
    final server = await verifiedServer([StandardJoin.alpha.package]);
    final plan = planJoin(server, {
      StandardJoin.alpha.sha256Hex: JoinPackageResolution.installed(
        StandardJoin.alpha.manifest,
      ),
    });
    final state = await _toLaunching(
      game,
      multiplayer,
      consentedJoin(server, plan, onLocalNetwork: true, closeOwnedGame: true),
    );
    final launch = await multiplayer.launchJoin(
      state,
      install: game.rig.install,
      intentId: 'join-renamed-player',
    );
    expect(launch, isA<JoinLaunchStarted>());
    expect(game.taken, hasLength(1));
    final projected = await LaunchStagingStore(
      game.rig.install.path,
    ).readPreferences();
    expect(projected!.displayName, 'Robin');
    expect(projected.lanDiscovery, isFalse);
    final session = LaunchTransport.readProfileV5(
      game.launchRequests.last,
    ).session;
    expect(
      session.displayName,
      projected.displayName,
      reason: 'The launch request must not override the name just projected.',
    );
    expect(session.transport.lan, projected.lanDiscovery);
  });

  test('a network choice the game saves as it quits can still stop the start: '
      'a LAN listing is not joined once LAN discovery is off', () async {
    final (game, multiplayer) = await _quittingWith([
      NetworkChange(sequence: 1, lanDiscovery: false, upnp: false),
    ]);
    final server = await verifiedServer([StandardJoin.alpha.package]);
    final plan = planJoin(server, {
      StandardJoin.alpha.sha256Hex: JoinPackageResolution.installed(
        StandardJoin.alpha.manifest,
      ),
    });
    final listed =
        [
          JoinDescriptorVerified(server),
          JoinPlanReady(plan),
          const JoinConsentGiven(closeOwnedGameAcknowledged: true),
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
    final state = await _toLaunching(game, multiplayer, listed);

    final launch = await multiplayer.launchJoin(
      state,
      install: game.rig.install,
      intentId: 'join-lan-turned-off',
    );

    // The game quit for the join and its choice reached the settings, so
    // the request built before it quit is not used, and nothing starts.
    expect(game.taken, hasLength(1));
    final failure = (launch as JoinLaunchRefused).failure;
    expect(failure.code, JoinFailureCode.launchBlocked);
    expect(failure.detail, contains('Turn LAN discovery on'));
    expect(game.launchRequests, hasLength(1));
    expect(
      (await multiplayer.loadPreferences()).settings.lanDiscoveryEnabled,
      isFalse,
    );
  });
}
