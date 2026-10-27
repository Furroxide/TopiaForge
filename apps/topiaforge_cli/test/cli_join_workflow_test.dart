import 'dart:async';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';
import 'package:topiaforge/src/cli_join_status.dart';
import 'package:topiaforge/src/cli_multiplayer_text.dart';
import 'cli_join_fixture.dart';

void main() {
  late JoinFixture rig;
  setUp(() async {
    rig = JoinFixture();
    await rig.initialize();
  });
  tearDown(() => rig.dispose());
  test('no terminal requires yes before any effects', () async {
    expect(await rig.run(['0123456789']), 2);
    expect(rig.changes, 0);
    expect(rig.answers, 0);
    expect(rig.launches, 0);
    expect(rig.messages.single, startsWith('TFJOIN100:'));
  });
  test('yes never bypasses a terminal disclosure', () async {
    rig
      ..terminal = true
      ..agree = false;
    expect(await rig.run(), 130);
    expect(rig.prompts, ['I understand [y/N]']);
    expect(rig.changes, 0);
    expect(rig.launches, 0);
  });
  test('yes never bypasses terminal Join consent', () async {
    rig
      ..terminal = true
      ..agree = false
      ..settings = rig.settings.acknowledgePrivacyDisclosure();
    expect(await rig.run(), 130);
    expect(rig.prompts, ['Join [y/N]']);
    expect(rig.launches, 0);
    expect(rig.steps, isNot(contains(JoinStage.downloading)));
  });
  test(
    'noninteractive yes prints the sheet and follows the matching fast receipt',
    () async {
      expect(await rig.run(), 0);
      expect(rig.prompts, isEmpty);
      expect(rig.launches, 1);
      expect(rig.histories, 1);
      expect(rig.messages, containsAllInOrder(CliMultiplayerText.disclosure));
      expect(
        rig.messages,
        containsAll([
          'Mods (1)',
          'Mod | Source | SHA-256 | Size',
          'What these mods may do',
          'physics',
          JoinConsentText.warning,
          CliMultiplayerText.voiceOff,
        ]),
      );
      expect(rig.messages.join('\n'), contains(testSha('example.sandbox')));
      for (final stage in [
        'TFJOIN110',
        'TFJOIN120',
        'TFJOIN140',
        'TFJOIN150',
        'TFJOIN160',
        'TFJOIN170',
        'TFJOIN180',
      ]) {
        expect(
          rig.messages.any((line) => line.startsWith(stage)),
          isTrue,
          reason: stage,
        );
      }
    },
  );
  test(
    'public direct target needs explicit flag before contacting it',
    () async {
      expect(await rig.run(['203.0.113.99:27250', '--yes']), 1);
      expect(rig.answers, 0);
      expect(rig.launches, 0);
      expect(rig.messages.join(), isNot(contains('203.0.113.99')));
    },
  );
  test('direct consent is per request and yes still prompts', () async {
    rig.terminal = true;
    expect(await rig.run(['203.0.113.99:27250', '--yes', '--allow-direct']), 0);
    expect(rig.prompts, contains('Allow for this session [y/N]'));
    expect(rig.messages, contains(CliMultiplayerText.direct));
    expect(rig.launchState!.privacy, MultiplayerAddressPrivacy.direct);
    expect(
      rig.settings.privacyForSession(allowDirect: false).allowsDirect,
      isFalse,
    );
  });
  test('host packages require developer mode before any contact', () async {
    expect(await rig.run(['0123456789', '--yes', '--allow-host-packages']), 2);
    expect(rig.answers, 0);
    expect(rig.changes, 0);
  });
  test(
    'developer host packages require a separate flag and terminal agreement',
    () async {
      await rig.initialize(hosted: true);
      rig.developer = true;
      expect(await rig.run(['localhost:27250', '--yes']), 2);
      expect(rig.launches, 0);
      rig.terminal = true;
      expect(
        await rig.run(['localhost:27250', '--yes', '--allow-host-packages']),
        0,
      );
      expect(rig.prompts, contains('${JoinHostServedText.checkbox} [y/N]'));
      expect(rig.messages, contains(JoinHostServedText.warning));
      expect(rig.launchState!.hostServedAcknowledged, isTrue);
    },
  );
  test(
    'declining the separate host-package acknowledgement starts nothing',
    () async {
      await rig.initialize(hosted: true);
      rig
        ..developer = true
        ..terminal = true;
      rig.onConsent = (prompt) {
        rig.agree = !prompt.startsWith(JoinHostServedText.checkbox);
      };
      expect(
        await rig.run(['localhost:27250', '--yes', '--allow-host-packages']),
        130,
      );
      expect(rig.launches, 0);
      expect(rig.steps, isNot(contains(JoinStage.downloading)));
    },
  );
  test('declining direct consent never contacts the target', () async {
    rig.terminal = true;
    rig.onConsent = (prompt) {
      rig.agree = !prompt.startsWith('Allow for this session');
    };
    expect(
      await rig.run(['203.0.113.99:27250', '--yes', '--allow-direct']),
      130,
    );
    expect(rig.answers, 0);
  });
  test('settings changing at consent stop before downloads', () async {
    rig.terminal = true;
    rig.onConsent = (prompt) {
      if (prompt == 'Join [y/N]') {
        rig.settings = rig.settings.giveVoiceConsent().withVoice(true);
      }
    };
    expect(await rig.run(), 1);
    expect(rig.launches, 0);
    expect(rig.steps, isNot(contains(JoinStage.downloading)));
  });
  test('settings changing during preparation stop before launch', () async {
    rig.beforeAnswer = (stage) async {
      if (stage == JoinStage.preparingProfile) {
        rig.settings = rig.settings.withDisplayName('Sam');
      }
    };
    expect(await rig.run(), 1);
    expect(rig.launches, 0);
  });
  test('cancel during install waits for the transaction to settle', () async {
    final entered = Completer<void>(), drain = Completer<void>();
    rig.beforeAnswer = (stage) async {
      if (stage == JoinStage.installing) {
        entered.complete();
        await drain.future;
      }
    };
    var completed = false;
    final result = rig.run().then((code) {
      completed = true;
      return code;
    });
    await entered.future;
    rig.cancel.complete();
    await Future<void>.delayed(Duration.zero);
    expect(completed, isFalse);
    drain.complete();
    expect(await result, 130);
    expect(rig.launches, 0);
    expect(rig.steps, isNot(contains(JoinStage.preparingProfile)));
  });
  for (final live in [false, true]) {
    test(
      'cancel during ${live ? 'live staging' : 'spawn'} leaves the game alone',
      () async {
        rig.live = live;
        rig.beforeLaunch = () async {
          rig.cancel.complete();
          await Future<void>.delayed(Duration.zero);
        };
        expect(await rig.run(), 130);
        expect(rig.launches, 1);
        expect(rig.withdrawals, live ? 1 : 0);
      },
    );
  }
  test('foreign game refuses before preferences or contact', () async {
    rig.running = JoinGameRunning.foreign;
    expect(await rig.run(), 1);
    expect(rig.changes, 0);
    expect(rig.answers, 0);
  });
  test('unreadable settings refuse', () async {
    rig.readable = false;
    expect(await rig.run(), 1);
    expect(rig.answers, 0);
  });
  test('unconfirmed join leaves the spawned game running', () async {
    rig.automaticSuccess = false;
    expect(
      await runZoned(
        () => rig.run(),
        zoneSpecification: ZoneSpecification(
          createTimer: (self, parent, zone, duration, callback) =>
              parent.createTimer(zone, Duration.zero, callback),
        ),
      ),
      3,
    );
    expect(rig.launches, 1);
    expect(rig.withdrawals, 0);
    expect(rig.messages.last, startsWith('TFJOIN180:'));
  });
  test('history failure cannot turn a joined game into failure', () async {
    rig.throwHistory = true;
    expect(await rig.run(), 0);
    expect(rig.messages.join(), isNot(contains('private path')));
  });
  test('raw exceptions are withheld', () async {
    rig.throwAnswer = true;
    expect(await rig.run(), 1);
    expect(rig.messages.join(), isNot(contains('203.0.113.99')));
  });
  for (final stage in JoinSession.failuresByStage.entries) {
    if (stage.key.index > JoinStage.preparingProfile.index) continue;
    for (final code in stage.value) {
      test(
        '${cliJoinStage(stage.key)} ${stage.key.name} ${code.name} has safe next steps',
        () async {
          rig
            ..failureStage = stage.key
            ..failure = switch (code) {
              JoinFailureCode.installFailed => const JoinFailed.installFailed(
                rollbackComplete: false,
              ),
              JoinFailureCode.gameRunning => const JoinFailed.gameRunning(
                ownership: JoinGameOwnership.foreign,
              ),
              JoinFailureCode.identityChanged => JoinFailed.identityChanged(
                servedServerId: 'b' * 64,
              ),
              _ => JoinFailed(code, detail: 'private 203.0.113.99'),
            };
          expect(
            await rig.run(
              code == JoinFailureCode.identityChanged
                  ? ['localhost:27250', '--yes']
                  : ['0123456789', '--yes'],
            ),
            1,
          );
          expect(rig.launches, 0);
          expect(rig.messages.last, startsWith('${cliJoinStage(stage.key)}:'));
          expect(rig.messages.last, isNot(contains('203.0.113.99')));
          expect(rig.messages.last.length, greaterThan(35));
        },
      );
    }
  }
}
