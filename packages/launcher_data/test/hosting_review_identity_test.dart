import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_join_rig.dart';

void main() {
  for (final changeInstall in [false, true]) {
    test('a host review binds its install (changed: $changeInstall)', () async {
      var creates = 0;
      final first = await JoinRig.create(
        withRuntime: true,
        creator: (_) async {
          creates++;
          throw StateError('No process is created by this test.');
        },
      );
      final other = await JoinRig.create(withRuntime: true);
      final multiplayer = LocalMultiplayerRepository(
        dataRoot: first.dataRoot,
        service: MultiplayerServiceConfiguration.none,
        content: first.repository,
        gameProbe: (_) async => false,
      );
      addTearDown(multiplayer.dispose);
      final profile = (await first.repository.loadSnapshot()).profiles.first;
      final plan =
          await multiplayer.prepareHosting(
                first.install,
                profile,
                HostingChoices(
                  defaults: MultiplayerHostingDefaults(name: 'Reviewed'),
                ),
                developerMode: false,
                cancellation: CancellationToken(),
              )
              as HostingPlan;
      final preview = await first.repository.previewLaunch(
        first.install,
        profile,
      );
      final otherPreview = await first.repository.previewLaunch(
        other.install,
        profile,
      );
      expect(plan.installIdentity, preview.installIdentity);
      expect(otherPreview.installIdentity, isNot(preview.installIdentity));
      expect(otherPreview.packageDigest, preview.packageDigest);
      final before = await multiplayer.loadPreferences();
      final result = await multiplayer.startHosting(
        changeInstall ? other.install : first.install,
        plan,
        intentId: 'host-other-install',
        acknowledgeDisclosure: true,
        developerMode: false,
        cancellation: CancellationToken(),
      );
      expect(
        creates,
        changeInstall ? 0 : 1,
        reason: 'Only the install reviewed may launch.',
      );
      expect(result.processStarted, isFalse);
      final after = await multiplayer.loadPreferences();
      if (changeInstall) {
        expect(result.message, contains('Review hosting again'));
        expect(after.hosting.toJson(), before.hosting.toJson());
        expect(after.settings.toJson(), before.settings.toJson());
      } else {
        expect(after.hosting.toJson(), plan.choices.defaults.toJson());
        expect(after.settings.needsPrivacyDisclosure, isFalse);
      }
    });
  }
}
