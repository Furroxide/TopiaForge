import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';
import 'package:topiaforge/src/cli_multiplayer_text.dart';

import 'cli_host_fixture.dart' as host;
import 'cli_join_fixture.dart' as join;

void main() {
  group('host direct permission', () {
    late host.HostFixture rig;
    setUp(() => rig = host.HostFixture()..terminal = true);
    tearDown(() => rig.dispose());

    for (final visibility in ['lan', 'invite', 'public']) {
      test(
        '$visibility keeps relay privacy when direct is unnecessary',
        () async {
          rig.onLaunch = () => rig.controller.add(rig.success());
          expect(
            await rig.run([
              '--yes',
              '--allow-direct',
              '--visibility',
              visibility,
            ]),
            0,
          );
          expect(
            rig.request!.transport.privacy,
            MultiplayerAddressPrivacy.relayOnly,
          );
          expect(rig.request!.transport.directConsent, isNull);
          expect(rig.messages, isNot(contains(CliMultiplayerText.direct)));
          expect(rig.prompts, isNot(contains('Allow for this session [y/N]')));
        },
      );
    }

    test(
      'required direct hosting asks after the disclosure, for one request',
      () async {
        rig.features = noRelay;
        rig.onLaunch = () => rig.controller.add(rig.success());
        expect(
          await rig.run(['--yes', '--allow-direct', '--visibility', 'invite']),
          0,
        );
        expect(rig.prompts, [
          'I understand [y/N]',
          'Allow for this session [y/N]',
          'Start hosting [y/N]',
        ]);
        expect(
          rig.request!.transport.privacy,
          MultiplayerAddressPrivacy.direct,
        );
        expect(rig.request!.transport.directConsent!.intentId, 'intent-one');
        expect(
          rig.settings.defaultPrivacy,
          MultiplayerAddressPrivacy.relayOnly,
        );
      },
    );

    test('declining required direct hosting never launches', () async {
      rig
        ..features = noRelay
        ..settings = rig.settings.acknowledgePrivacyDisclosure()
        ..consent = false;
      expect(
        await rig.run(['--yes', '--allow-direct', '--visibility', 'invite']),
        130,
      );
      expect(rig.prompts, ['Allow for this session [y/N]']);
      expect(rig.launches, 0);
    });
  });

  group('join direct permission', () {
    late join.JoinFixture rig;
    setUp(() async {
      rig = join.JoinFixture()..terminal = true;
      await rig.initialize();
    });
    tearDown(() => rig.dispose());

    for (final target in ['0123456789', 'localhost:27250']) {
      test('$target keeps relay privacy when direct is unnecessary', () async {
        expect(await rig.run([target, '--yes', '--allow-direct']), 0);
        expect(rig.launchState!.privacy, MultiplayerAddressPrivacy.relayOnly);
        expect(rig.messages, isNot(contains(CliMultiplayerText.direct)));
        expect(rig.prompts, ['I understand [y/N]', 'Join [y/N]']);
      });
    }
  });
}

const noRelay = MultiplayerServiceFeatures(
  hosting: true,
  invites: true,
  signaling: true,
  relay: false,
  killSwitch: true,
  heartbeat: Duration(seconds: 10),
  listingTtl: Duration(seconds: 30),
  relayCredentialTtl: Duration(minutes: 5),
);
