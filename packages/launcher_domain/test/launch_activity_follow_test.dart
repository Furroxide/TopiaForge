import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

LaunchActivity _join() => LaunchActivity(
  requestId: 'join-request',
  profileId: 'profile',
  profileRevision: 1,
  installIdentity: 'install',
  packageDigest: 'cbf29ce484222325',
  command: 'join-session',
);

LaunchProgress _progress(
  int sequence,
  String? world, {
  String request = 'join-request',
  String intent = 'join-intent',
  bool v2 = true,
}) => LaunchTransport.readProgress(
  LaunchTransport.writeProgress(
    LaunchProgress(
      requestId: request,
      sessionId: world,
      sequence: sequence,
      phase: world == null ? 'idle' : 'running',
      session: !v2
          ? null
          : LaunchProgressSession(
              role: LaunchSessionRole.join,
              intentId: intent,
              state: LaunchSessionState.inSession,
            ),
    ),
  ),
);

LaunchOutcome _outcome(
  int sequence,
  String world, {
  bool ended = false,
  String intent = 'join-intent',
}) => LaunchTransport.readOutcome(
  LaunchTransport.writeOutcome(
    LaunchOutcome(
      kind: ended ? 'session' : 'launch',
      requestId: 'join-request',
      command: ended ? null : 'join-session',
      sessionId: world,
      sequence: sequence,
      status: 'succeeded',
      phase: ended ? 'idle' : 'running',
      multiplayer: LaunchOutcomeMultiplayer(
        role: LaunchSessionRole.join,
        intentId: intent,
        endReason: ended ? LaunchSessionEndReason.left : null,
      ),
    ),
  ),
);

void main() {
  test('a joined activity follows world changes and accepts its final end', () {
    final started = _join()
        .applyProgress(_progress(1, 'first-world'))
        .applyOutcome(_outcome(2, 'first-world'));
    final next = started.applyProgress(_progress(3, 'second-world'));
    expect(next.progress!.sessionId, 'second-world');
    expect(next.launchOutcome!.sessionId, 'first-world');
    final ended = next.applyOutcome(_outcome(4, 'second-world', ended: true));
    expect(ended.sessionOutcome!.sessionId, 'second-world');
    expect(ended.applyProgress(_progress(5, 'third-world')), same(ended));
  });

  test(
    'a terminal record can arrive before the first world acknowledgement',
    () {
      final ended = _join().applyOutcome(
        _outcome(4, 'second-world', ended: true),
      );
      final complete = ended.applyOutcome(_outcome(2, 'first-world'));
      expect(complete.launchOutcome!.sessionId, 'first-world');
      expect(complete.sessionOutcome!.sessionId, 'second-world');
    },
  );

  test('a delayed acknowledgement survives a newer world or menu progress', () {
    for (final world in ['second-world', null]) {
      final current = _join()
          .applyProgress(_progress(3, world))
          .applyOutcome(_outcome(2, 'first-world'));
      expect(current.launchOutcome, isNotNull);
      expect(current.progress!.sessionId, world);
    }
  });

  test('world changes still require the same request and V2 join intent', () {
    final current = _join().applyProgress(_progress(3, 'first-world'));
    for (final invalid in [
      _progress(4, 'second-world', request: 'foreign-request'),
      _progress(4, 'second-world', intent: 'foreign-intent'),
      _progress(4, 'first-world', intent: 'foreign-intent'),
      _progress(4, 'second-world', v2: false),
      _progress(2, 'second-world'),
    ]) {
      expect(current.applyProgress(invalid), same(current));
    }
    final legacy = _join().applyProgress(
      _progress(1, 'first-world', v2: false),
    );
    expect(legacy.applyProgress(_progress(2, 'second-world')), same(legacy));
    final acknowledged = current.applyOutcome(_outcome(2, 'first-world'));
    expect(
      acknowledged.applyProgress(_progress(4, null)).progress!.sessionId,
      isNull,
    );
  });

  test('an outcome for another join intent is refused in another world', () {
    final current = _join().applyProgress(_progress(1, 'first-world'));
    for (final ended in [false, true]) {
      final foreign = _outcome(
        2,
        'second-world',
        ended: ended,
        intent: 'foreign-intent',
      );
      expect(current.applyOutcome(foreign), same(current));
    }
    final own = current.applyOutcome(_outcome(2, 'second-world'));
    expect(own.launchOutcome!.sessionId, 'second-world');
  });
}
