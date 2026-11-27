import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import '../../../packages/launcher_data/test/control_plane_support.dart';
import 'cli_join_fixture.dart';

void main() {
  late JoinFixture rig;
  setUp(() async {
    rig = JoinFixture()..terminal = true;
    await rig.initialize();
  });
  tearDown(() => rig.dispose());

  Future<int> withStatus(CannedAnswer answer, List<String> args) async {
    final service = await FakeControlPlane.start((request) {
      expect(request.target, '/v1/status');
      return answer;
    });
    rig.serviceStatusRead = (token) =>
        testClient().readStatus(cancellation: token);
    return service.run(() => rig.run(args));
  }

  int directPrompts() =>
      rig.prompts.where((p) => p == 'Allow for this session [y/N]').length;

  for (final invite in [true, false]) {
    test('${invite ? 'invite' : 'server link'} uses actual relay-off status '
        'before direct consent and launch', () async {
      final target = invite
          ? '0123456789'
          : SessionLink.server(rig.plan.server.serverId).canonical;
      rig.onConsent = (prompt) {
        if (prompt == 'Allow for this session [y/N]') {
          expect(rig.launches, 0);
          expect(
            rig.answerStates.every(
              (state) => state.privacy == MultiplayerAddressPrivacy.relayOnly,
            ),
            isTrue,
          );
        }
      };
      expect(
        await withStatus(statusAnswer(relay: false), [
          target,
          '--yes',
          '--allow-direct',
        ]),
        0,
      );
      expect(directPrompts(), 1);
      expect(rig.launches, 1);
      expect(rig.launchState!.privacy, MultiplayerAddressPrivacy.direct);
      expect(rig.launchState!.serverId, rig.plan.server.serverId);
    });
  }

  test('kill switch requires the same explicit prelaunch consent', () async {
    expect(
      await withStatus(statusAnswer(killSwitch: true), [
        '0123456789',
        '--yes',
        '--allow-direct',
      ]),
      0,
    );
    expect(directPrompts(), 1);
    expect(rig.launchState!.privacy, MultiplayerAddressPrivacy.direct);
  });

  test(
    'relay-off service refuses before launch without direct permission',
    () async {
      expect(
        await withStatus(statusAnswer(relay: false), ['0123456789', '--yes']),
        isNot(0),
      );
      expect(rig.launches, 0);
      expect(directPrompts(), 0);
    },
  );

  test('declining service direct consent never starts the game', () async {
    rig.onConsent = (prompt) {
      if (prompt == 'Allow for this session [y/N]') {
        rig.agree = false;
      }
    };
    expect(
      await withStatus(statusAnswer(relay: false), [
        '0123456789',
        '--yes',
        '--allow-direct',
      ]),
      130,
    );
    expect(rig.launches, 0);
    expect(directPrompts(), 1);
  });

  test(
    'unreadable service status refuses without assuming direct is usable',
    () async {
      expect(
        await withStatus(CannedAnswer.error(503, 'service-unavailable'), [
          '0123456789',
          '--yes',
          '--allow-direct',
        ]),
        isNot(0),
      );
      expect(rig.launches, 0);
      expect(directPrompts(), 0);
    },
  );

  test(
    'disabled signaling cannot be repaired with direct permission',
    () async {
      expect(
        await withStatus(statusAnswer(relay: false, signaling: false), [
          '0123456789',
          '--yes',
          '--allow-direct',
        ]),
        isNot(0),
      );
      expect(rig.launches, 0);
      expect(directPrompts(), 0);
    },
  );

  test('available relay keeps Hide my IP despite the direct flag', () async {
    expect(
      await withStatus(statusAnswer(), [
        '0123456789',
        '--yes',
        '--allow-direct',
      ]),
      0,
    );
    expect(rig.launchState!.privacy, MultiplayerAddressPrivacy.relayOnly);
    expect(directPrompts(), 0);
  });

  test('local joins do not query Internet service availability', () async {
    rig.serviceStatusRead = (_) => throw StateError('Unexpected service query');
    expect(await rig.run(['localhost:27250', '--yes', '--allow-direct']), 0);
    expect(rig.launchState!.privacy, MultiplayerAddressPrivacy.relayOnly);
    expect(directPrompts(), 0);
  });
}
