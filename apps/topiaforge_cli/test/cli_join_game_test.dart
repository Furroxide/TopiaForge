import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';
import 'package:topiaforge/src/cli_join_status.dart';
import 'cli_join_fixture.dart';

void main() {
  late JoinFixture rig;
  setUp(() async {
    rig = JoinFixture();
    await rig.initialize();
  });
  tearDown(() => rig.dispose());
  LaunchActivity failed(LaunchMultiplayerFailure code) {
    final base = rig.base();
    return base.applyOutcome(
      LaunchOutcome(
        kind: 'launch',
        requestId: base.requestId,
        command: base.command,
        sequence: 1,
        status: 'failed',
        phase: 'idle',
        multiplayer: LaunchOutcomeMultiplayer(
          role: LaunchSessionRole.join,
          intentId: rig.intent,
          serverId: rig.plan.server.serverId,
          failure: code,
          detail: 'private 203.0.113.99',
        ),
      ),
    );
  }

  for (final code in JoinSession.failuresByStage[JoinStage.launching]!) {
    test('TFJOIN170 ${code.name} has next steps', () async {
      rig.launchResult = JoinLaunchRefused(
        code == JoinFailureCode.gameRunning
            ? const JoinFailed.gameRunning(ownership: JoinGameOwnership.foreign)
            : JoinFailed(code, detail: 'private 203.0.113.99'),
      );
      expect(await rig.run(), 1);
      expect(rig.messages.last, startsWith('TFJOIN170:'));
      expect(rig.messages.last, isNot(contains('203.0.113.99')));
    });
  }
  for (final code in LaunchMultiplayerFailure.values) {
    final failure = code.joinFailureCode;
    if (failure == null ||
        !{
          ...JoinSession.failuresByStage[JoinStage.connecting]!,
          ...JoinSession.failuresByStage[JoinStage.synchronizing]!,
        }.contains(failure)) {
      continue;
    }
    test('TFJOIN180 ${code.name} has safe next steps', () async {
      rig.activityOnLaunch = () => failed(code);
      expect(await rig.run(), 1);
      expect(rig.messages.last, startsWith('TFJOIN180:'));
      expect(rig.messages.last, isNot(contains('203.0.113.99')));
    });
  }
  test(
    'one admission change replans and unchanged consent is reused',
    () async {
      rig.terminal = true;
      rig.activityOnLaunch = () => rig.launches == 1
          ? failed(LaunchMultiplayerFailure.admissionChanged)
          : rig.success();
      expect(await rig.run(), 0);
      expect(rig.launches, 2);
      expect(rig.prompts.where((p) => p == 'Join [y/N]').length, 1);
    },
  );
  test('a changed re-plan asks for consent again', () async {
    rig.terminal = true;
    rig.activityOnLaunch = () => rig.launches == 1
        ? failed(LaunchMultiplayerFailure.admissionChanged)
        : rig.success();
    rig.beforeAnswer = (stage) async {
      if (stage == JoinStage.fetchingDescriptor && rig.launches == 1) {
        rig.plan = await planJoin([
          (
            testPackage('example.second'),
            availableOf(testManifest('example.second')),
          ),
        ]);
      }
    };
    expect(await rig.run(), 0);
    expect(rig.launches, 2);
    expect(rig.prompts.where((p) => p == 'Join [y/N]').length, 2);
    expect(
      rig.messages.where((line) => line.startsWith('Creates profile')).length,
      1,
    );
    expect(
      rig.messages.where((line) => line.startsWith('Updates profile')).length,
      1,
    );
  });
  test('a verified process exit cannot count as joined', () async {
    rig.activityOnLaunch = () => rig.success().copyWith(processExited: true);
    expect(await rig.run(), 1);
    expect(rig.histories, 0);
  });
  test('every stage has a reserved code', () {
    expect(JoinStage.values.map(cliJoinStage).toSet(), {
      'TFJOIN110',
      'TFJOIN120',
      'TFJOIN130',
      'TFJOIN140',
      'TFJOIN150',
      'TFJOIN160',
      'TFJOIN170',
      'TFJOIN180',
    });
  });
}
