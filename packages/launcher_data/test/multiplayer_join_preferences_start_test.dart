import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'multiplayer_join_content_fixtures.dart';
import 'multiplayer_join_owned_game.dart';

void main() {
  test('an owned restart uses the name the game saves as it quits', () async {
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
            MultiplayerPreferencesJournal('5f0c2a9e8d7b6a5f4e3d2c1b0a998877', [
              DisplayNameChange(sequence: 1, displayName: 'Robin'),
            ]),
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
    await multiplayer.changePreferences(
      const MultiplayerPreferencesChange(displayName: 'Sam'),
    );
    expect(
      await multiplayer.syncWithGame(game.rig.install),
      isA<MultiplayerPreferencesSynced>(),
    );
    final server = await verifiedServer([StandardJoin.alpha.package]);
    final plan = planJoin(server, {
      StandardJoin.alpha.sha256Hex: JoinPackageResolution.installed(
        StandardJoin.alpha.manifest,
      ),
    });
    var state = consentedJoin(
      server,
      plan,
      onLocalNetwork: true,
      closeOwnedGame: true,
    );
    while (state.stage != JoinStage.launching && !state.isEnded) {
      state = JoinSession.reduce(
        state,
        (await multiplayer.answerJoin(state, install: game.rig.install))!,
      );
    }
    expect(state.stage, JoinStage.launching);
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
    expect(
      LaunchTransport.readProfileV5(
        game.launchRequests.last,
      ).session.displayName,
      projected.displayName,
      reason: 'The launch request must not override the name just projected.',
    );
  });
}
