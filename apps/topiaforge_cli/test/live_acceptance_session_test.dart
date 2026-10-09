import 'dart:io';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/live_acceptance_session.dart';
import 'live_acceptance_test_fixture.dart';

void main() {
  late AcceptanceFixture f;
  setUp(() => f = AcceptanceFixture());
  tearDown(() => f.dispose());
  test(
    'missing acknowledgement still cleans the owned session and never emits success',
    () async {
      final session = _Session(failConfirm: true);
      final runner = f.runner(
        commandRunner: (_) async => 0,
        sessionLauncher: (_, context, challenge) async => session,
      );
      await expectLater(runner.run(f.options()), throwsStateError);
      expect(session.closeCalls, 1);
      expect(
        File(p.join(f.output.path, 'acceptance-result.json')).existsSync(),
        isFalse,
      );
    },
  );
  test('owned cleanup failure cannot produce success evidence', () async {
    final session = _Session(failClose: true);
    final runner = f.runner(
      commandRunner: (_) async => 0,
      sessionLauncher: (_, context, challenge) async {
        f.writePassingRun();
        return session;
      },
    );
    await expectLater(runner.run(f.options()), throwsStateError);
    expect(session.closeCalls, 1);
    expect(
      File(p.join(f.output.path, 'acceptance-result.json')).existsSync(),
      isFalse,
    );
  });
  test(
    'successful evidence is written only after confirmed owned exit',
    () async {
      final session = _Session();
      final runner = f.runner(
        commandRunner: (_) async => 0,
        sessionLauncher: (_, context, challenge) async {
          f.writePassingRun();
          return session;
        },
      );
      final evidence = await runner.run(f.options());
      expect(evidence.succeeded, isTrue);
      expect(session.closeCalls, 1);
      expect(evidence.isolation['processExitConfirmed'], isTrue);
      expect(
        Directory(p.join(f.sourceGame.path, 'BepInEx')).existsSync(),
        isFalse,
      );
    },
  );
}

final class _Session implements LiveAcceptanceSession {
  _Session({this.failConfirm = false, this.failClose = false});
  final bool failConfirm;
  final bool failClose;
  int closeCalls = 0;
  bool confirmed = false;
  bool exited = false;
  Future<void>? closing;
  @override
  Future<void> confirm(Duration timeout) async {
    if (failConfirm) throw StateError('No private acknowledgement.');
    confirmed = true;
  }

  @override
  Future<void> close() => closing ??= _close();
  Future<void> _close() async {
    closeCalls++;
    if (failClose) throw StateError('Owned process exit remains unknown.');
    exited = true;
  }

  @override
  Map<String, Object?> get isolationEvidence {
    if (!confirmed || !exited) {
      throw StateError('Evidence read before owned exit.');
    }
    return {
      'kind': 'windows-user',
      'provisioningRecordSha256': 'a' * 64,
      'acknowledgementSha256': 'b' * 64,
      'acknowledgement': {'synthetic': true},
      'processExitConfirmed': true,
    };
  }
}
