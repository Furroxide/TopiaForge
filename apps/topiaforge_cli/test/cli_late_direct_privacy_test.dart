import 'dart:async';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'cli_join_fixture.dart';

void main() {
  const target = 'printer.local:27250';
  late JoinFixture rig;
  setUp(() async {
    rig = JoinFixture()..terminal = true;
    await rig.initialize();
  });
  tearDown(() => rig.dispose());

  void requireDirectAt(JoinStage stage, {bool always = false}) {
    rig.answerForState = (state) =>
        state.stage == stage &&
            (always || state.privacy == MultiplayerAddressPrivacy.relayOnly)
        ? JoinFailed(JoinFailureCode.directNotAllowed)
        : null;
  }

  int directPrompts() =>
      rig.prompts.where((p) => p == 'Allow for this session [y/N]').length;
  Iterable<JoinSessionState> directCalls() => rig.answerStates.where(
    (s) => s.privacy == MultiplayerAddressPrivacy.direct,
  );

  test(
    'late descriptor refusal restarts once after explicit direct consent',
    () async {
      requireDirectAt(JoinStage.fetchingDescriptor);
      rig.onConsent = (prompt) {
        if (prompt == 'Allow for this session [y/N]') {
          expect(directCalls(), isEmpty);
          expect(rig.launches, 0);
        }
      };
      expect(await rig.run([target, '--yes', '--allow-direct']), 0);
      expect(directPrompts(), 1);
      expect(rig.prompts, [
        'I understand [y/N]',
        'Allow for this session [y/N]',
        'Join [y/N]',
      ]);
      expect(
        rig.answerStates
            .where((s) => s.stage == JoinStage.fetchingDescriptor)
            .length,
        2,
      );
      final original = rig.answerStates.first.target;
      expect(
        rig.answerStates.every((s) => identical(s.target, original)),
        isTrue,
      );
      expect(rig.launches, 1);
      expect(rig.settings.defaultPrivacy, MultiplayerAddressPrivacy.relayOnly);
    },
  );

  test(
    'late checking refusal retains the learned typed-target server identity',
    () async {
      requireDirectAt(JoinStage.checking);
      expect(await rig.run([target, '--yes', '--allow-direct']), 0);
      final restarted = directCalls().first;
      expect(restarted.stage, JoinStage.fetchingDescriptor);
      expect(restarted.serverId, rig.plan.server.serverId);
      expect(directPrompts(), 1);
    },
  );

  test(
    're-resolved invite cannot silently change the learned server',
    () async {
      requireDirectAt(JoinStage.checking);
      final fallback = rig.answerForState!;
      rig.answerForState = (state) =>
          state.privacy == MultiplayerAddressPrivacy.direct &&
              state.stage == JoinStage.resolving
          ? JoinTargetResolved('f' * 64)
          : fallback(state);
      expect(await rig.run(['0123456789', '--yes', '--allow-direct']), 1);
      expect(directPrompts(), 1);
      expect(
        directCalls().where((s) => s.stage == JoinStage.fetchingDescriptor),
        isEmpty,
      );
      expect(rig.launches, 0);
      expect(rig.messages.last, contains('identity changed'));
    },
  );

  test(
    'declining a late direct request never contacts the direct route',
    () async {
      requireDirectAt(JoinStage.fetchingDescriptor);
      rig.onConsent = (p) {
        if (p == 'Allow for this session [y/N]') rig.agree = false;
      };
      expect(await rig.run([target, '--yes', '--allow-direct']), 130);
      expect(directCalls(), isEmpty);
      expect(rig.launches, 0);
    },
  );

  test('cancellation during late direct consent never restarts', () async {
    requireDirectAt(JoinStage.fetchingDescriptor);
    rig.onConsent = (p) {
      if (p == 'Allow for this session [y/N]') rig.cancel.complete();
    };
    expect(await rig.run([target, '--yes', '--allow-direct']), 130);
    expect(directCalls(), isEmpty);
    expect(rig.launches, 0);
  });

  test(
    'settings changed during late direct consent refuse before direct contact',
    () async {
      requireDirectAt(JoinStage.fetchingDescriptor);
      rig.onConsent = (p) {
        if (p == 'Allow for this session [y/N]') {
          rig.settings = rig.settings.withDisplayName('Sam');
        }
      };
      expect(await rig.run([target, '--yes', '--allow-direct']), 1);
      expect(directCalls(), isEmpty);
      expect(rig.messages.last, contains('settings changed'));
    },
  );

  test('another direct refusal cannot trigger an unbounded retry', () async {
    requireDirectAt(JoinStage.fetchingDescriptor, always: true);
    expect(await rig.run([target, '--yes', '--allow-direct']), 1);
    expect(directPrompts(), 1);
    expect(
      rig.answerStates
          .where((s) => s.stage == JoinStage.fetchingDescriptor)
          .length,
      2,
    );
    expect(rig.launches, 0);
  });

  test(
    'a late refusal without the flag does not offer direct access',
    () async {
      requireDirectAt(JoinStage.fetchingDescriptor);
      expect(await rig.run([target, '--yes']), 1);
      expect(directPrompts(), 0);
      expect(directCalls(), isEmpty);
      expect(rig.launches, 0);
    },
  );

  test(
    'a post-launch refusal explains the running game and never relaunches',
    () async {
      final entered = Completer<void>();
      rig
        ..automaticSuccess = false
        ..beforeLaunch = () async {
          entered.complete();
        };
      final run = rig.run(['0123456789', '--yes', '--allow-direct']);
      await entered.future;
      await pumpEventQueue();
      final activity = rig.base();
      rig.controller.add(
        activity.applyOutcome(
          LaunchOutcome(
            kind: 'launch',
            requestId: activity.requestId,
            command: 'join-session',
            sequence: 1,
            status: 'failed',
            phase: 'idle',
            multiplayer: LaunchOutcomeMultiplayer(
              role: LaunchSessionRole.join,
              intentId: rig.intent,
              failure: LaunchMultiplayerFailure.directNotAllowed,
            ),
          ),
        ),
      );
      expect(await run, 3);
      expect(rig.launches, 1);
      expect(rig.withdrawals, 0);
      expect(directPrompts(), 0);
      expect(rig.messages.last, contains('left running'));
    },
  );
}
