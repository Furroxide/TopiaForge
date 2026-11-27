import 'dart:convert';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'cli_join_fixture.dart';

void main() {
  late JoinFixture rig;
  setUp(() async {
    rig = JoinFixture();
    await rig.initialize();
  });
  tearDown(() => rig.dispose());

  LaunchActivity outcome({bool terminal = false, bool follow = false}) {
    final base = rig.base();
    final record = LaunchTransport.readOutcome(
      jsonEncode({
        'schemaVersion': 2,
        'kind': terminal ? 'session' : 'launch',
        'requestId': base.requestId,
        if (!terminal) 'command': base.command,
        'sequence': 1,
        'status': terminal ? 'succeeded' : 'failed',
        'phase': 'idle',
        'blocks': [],
        'multiplayer': {
          'role': 'join',
          'intentId': rig.intent,
          if (terminal)
            'endReason': 'future-private-end'
          else
            'failure': follow
                ? 'world-follow-failed'
                : 'future-private-failure',
          'detail': 'private 203.0.113.99 secret-provider',
        },
      }),
    );
    return base.applyOutcome(record);
  }

  test(
    'follow failure before progress returns specific safe recovery',
    () async {
      rig.activityOnLaunch = () => outcome(follow: true);
      expect(await rig.run(), 1);
      expect(rig.messages.last, startsWith('TFJOIN180:'));
      expect(rig.messages.last, contains('could not follow the host'));
      expect(rig.messages.last, isNot(contains('203.0.113.99')));
      expect(rig.histories, 0);
      expect(rig.launches, 1);
      expect(rig.controller.hasListener, isFalse);
    },
  );
  test(
    'future failure returns canonical advice without provider detail',
    () async {
      rig.activityOnLaunch = outcome;
      expect(await rig.run(), 1);
      expect(rig.messages.last, startsWith('TFJOIN180:'));
      expect(rig.messages.last, contains(LaunchOutcomeMultiplayer.otherDetail));
      expect(rig.messages.join('\n'), isNot(contains('secret-provider')));
      expect(rig.messages.join('\n'), isNot(contains('203.0.113.99')));
      expect(rig.histories, 0);
      expect(rig.launches, 1);
      expect(rig.controller.hasListener, isFalse);
    },
  );
  test('future terminal before acknowledgement cannot report joined', () async {
    rig.activityOnLaunch = () => outcome(terminal: true);
    expect(await rig.run(), 1);
    expect(rig.histories, 0);
    expect(rig.launches, 1);
    expect(rig.messages.join('\n'), isNot(contains('Joined the session.')));
    expect(rig.messages.join('\n'), isNot(contains('secret-provider')));
    expect(rig.controller.hasListener, isFalse);
  });
}
