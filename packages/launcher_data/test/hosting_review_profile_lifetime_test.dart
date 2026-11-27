import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_join_rig.dart';

void main() {
  for (final externalWriter in [false, true]) {
    test('a removed host profile stays removed after reload '
        '(external writer: $externalWriter)', () async {
      var creates = 0;
      final rig = await JoinRig.create(
        withRuntime: true,
        creator: (_) async {
          creates++;
          throw StateError('No process is created by this test.');
        },
      );
      final multiplayer = LocalMultiplayerRepository(
        dataRoot: rig.dataRoot,
        service: MultiplayerServiceConfiguration.none,
        content: rig.repository,
        gameProbe: (_) async => false,
      );
      addTearDown(multiplayer.dispose);
      final profile = (await rig.repository.loadSnapshot()).profiles.first;
      final plan =
          await multiplayer.prepareHosting(
                rig.install,
                profile,
                HostingChoices(
                  defaults: MultiplayerHostingDefaults(name: 'Reviewed'),
                ),
                developerMode: false,
                cancellation: CancellationToken(),
              )
              as HostingPlan;
      final writer = externalWriter ? rig.open() : rig.repository;
      await writer.loadSnapshot();
      await writer.saveProfiles([
        const LauncherProfile(id: 'replacement', name: 'Replacement'),
      ], 'replacement');
      final snapshot = await rig.repository.loadSnapshot();
      expect(snapshot.profiles.any((item) => item.id == profile.id), isFalse);
      final result = await multiplayer.startHosting(
        rig.install,
        plan,
        intentId: 'removed-profile',
        acknowledgeDisclosure: true,
        developerMode: false,
        cancellation: CancellationToken(),
      );
      expect(result.processStarted, isFalse);
      expect(creates, 0, reason: 'A removed reviewed profile cannot launch.');
      expect(
        (await rig.repository.loadSnapshot()).profiles.single.id,
        'replacement',
      );
    });
  }
}
