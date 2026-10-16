import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

LaunchActivity activity() => LaunchActivity(
  requestId: 'request',
  profileId: 'profile',
  profileRevision: 3,
  installIdentity: 'install',
  packageDigest: 'cbf29ce484222325',
  command: 'launch-target',
);
LaunchProgress progress(
  int sequence, {
  String request = 'request',
  String session = 'session',
  String phase = 'preparing',
}) => LaunchProgress(
  requestId: request,
  sessionId: session,
  sequence: sequence,
  phase: phase,
);
LaunchOutcome outcome({
  String kind = 'launch',
  int sequence = 5,
  String request = 'request',
  String session = 'session',
}) => LaunchOutcome(
  kind: kind,
  requestId: request,
  command: kind == 'launch' ? 'launch-target' : null,
  sessionId: session,
  sequence: sequence,
  status: 'succeeded',
  phase: kind == 'launch' ? 'running' : 'idle',
);

void main() {
  test('verified process exit does not invent a terminal session outcome', () {
    final ended = activity()
        .applyOutcome(outcome())
        .copyWith(processExited: true);
    expect(ended.sessionStarted, isTrue);
    expect(ended.processExited, isTrue);
    expect(ended.sessionOutcome, isNull);
    expect(ended.copyWith(processExited: false).processExited, isTrue);
  });
  test('failed launch cannot be revived by late progress', () {
    final failed = activity().applyOutcome(
      LaunchOutcome(
        kind: 'launch',
        requestId: 'request',
        command: 'launch-target',
        sequence: 5,
        status: 'failed',
        phase: 'idle',
        error: LaunchOperationError(
          code: 'unavailable',
          message: 'Provider unavailable',
        ),
      ),
    );
    expect(failed.applyProgress(progress(6)), same(failed));
  });
  test('process creation and Running progress alone are unconfirmed', () {
    final pending = activity().applyProgress(progress(4, phase: 'running'));
    expect(pending.acknowledged, isFalse);
    expect(pending.sessionStarted, isFalse);
    expect(pending.copyWith(unconfirmed: true).unconfirmed, isTrue);
  });
  test(
    'matching acknowledgement settles unconfirmed without fabricating terminal state',
    () {
      final confirmed = activity()
          .copyWith(unconfirmed: true)
          .applyOutcome(outcome());
      expect(confirmed.sessionStarted, isTrue);
      expect(confirmed.unconfirmed, isFalse);
      expect(confirmed.sessionOutcome, isNull);
    },
  );
  test('foreign and stale progress cannot replace current state', () {
    final current = activity().applyProgress(progress(3));
    expect(
      current.applyProgress(progress(4, request: 'foreign')),
      same(current),
    );
    expect(current.applyProgress(progress(2)), same(current));
    expect(
      current.applyProgress(progress(4, session: 'foreign')),
      same(current),
    );
  });
  test('terminal-first arrival retains both separate outcomes', () {
    final terminal = activity().applyOutcome(
      outcome(kind: 'session', sequence: 8),
    );
    final complete = terminal.applyOutcome(outcome());
    expect(complete.sessionOutcome!.sequence, 8);
    expect(complete.launchOutcome!.sequence, 5);
    expect(complete.sequence, 8);
    expect(complete.applyProgress(progress(9)), same(complete));
  });
  test('different request or session outcomes are ignored', () {
    final current = activity().applyProgress(progress(2));
    expect(current.applyOutcome(outcome(request: 'foreign')), same(current));
    expect(current.applyOutcome(outcome(session: 'foreign')), same(current));
  });
  test(
    'first terminal acknowledgement is immutable despite repeated writes',
    () {
      final current = activity().applyOutcome(outcome());
      expect(current.applyOutcome(outcome(sequence: 10)), same(current));
    },
  );
}
