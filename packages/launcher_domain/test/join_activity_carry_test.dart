import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_fixtures.dart';
import 'multiplayer_join_support.dart';

void main() {
  late JoinSessionState launching;
  setUpAll(() async {
    final plan = await planJoin([]);
    launching =
        [
          JoinTargetResolved(hostAServerId),
          JoinDescriptorVerified(plan.server),
          JoinPlanReady(plan),
          const JoinConsentGiven(),
          const JoinProfilePrepared(),
        ].fold(
          startJoin(JoinLinkTarget(SessionLink.server(hostAServerId))),
          JoinSession.reduce,
        );
    expect(launching.stage, JoinStage.launching);
  });

  LaunchActivity activity({
    String status = 'failed',
    LaunchMultiplayerFailure? failure,
    LaunchSessionEndReason? end,
    bool exited = false,
  }) => LaunchActivity(
    requestId: 'request',
    profileId: 'profile',
    profileRevision: 0,
    installIdentity: 'fixture',
    packageDigest: 'digest',
    command: 'join-session',
    processExited: exited,
    launchOutcome: end == null
        ? LaunchOutcome(
            kind: 'launch',
            requestId: 'request',
            command: 'join-session',
            sequence: 1,
            status: status,
            phase: 'idle',
            error: failure == null && status == 'failed'
                ? LaunchOperationError(
                    code: 'unavailable',
                    message: 'Provider at 203.0.113.99 is unavailable.',
                  )
                : null,
            multiplayer: LaunchOutcomeMultiplayer(
              role: LaunchSessionRole.join,
              intentId: 'intent',
              failure: failure,
              detail: failure == null
                  ? null
                  : 'World at 203.0.113.99 could not load. Try again.',
            ),
          )
        : null,
    sessionOutcome: end == null
        ? null
        : LaunchOutcome(
            kind: 'session',
            requestId: 'request',
            sequence: 2,
            status: 'succeeded',
            phase: 'idle',
            multiplayer: LaunchOutcomeMultiplayer(
              role: LaunchSessionRole.join,
              intentId: 'intent',
              endReason: end,
            ),
          ),
  );
  JoinSessionState apply(JoinSessionState state, LaunchActivity value) =>
      joinEventsFor(state, value).fold(state, JoinSession.reduce);

  for (final code in [
    LaunchMultiplayerFailure.snapshotFailed,
    LaunchMultiplayerFailure.worldFollowFailed,
  ]) {
    for (final alreadyConnecting in [false, true]) {
      test(
        '$code settles without synchronizing progress: $alreadyConnecting',
        () {
          final state = alreadyConnecting
              ? JoinSession.reduce(launching, const JoinGameStarted())
              : launching;
          final result = apply(state, activity(failure: code));
          final failure = result.outcome!.failure!;
          expect(failure.code, code.joinFailureCode);
          expect(failure.stage, JoinStage.synchronizing);
          final text = JoinFailureText.of(failure);
          expect(text.retry, isTrue);
          expect(text.bodyFor(failure), contains('could not load'));
          expect(text.bodyFor(failure), isNot(contains('203.0.113.99')));
        },
      );
    }
  }
  test('a refused launch settles at start with safe useful recovery', () {
    final result = apply(launching, activity());
    final failure = result.outcome!.failure!;
    expect(failure.code, JoinFailureCode.startFailed);
    expect(failure.stage, JoinStage.launching);
    expect(failure.detail, contains('is unavailable'));
    expect(failure.detail, contains('Diagnostics'));
    expect(failure.detail, isNot(contains('203.0.113.99')));
  });
  test('a refused launch after acknowledgement settles as disconnected', () {
    final connecting = JoinSession.reduce(launching, const JoinGameStarted());
    final failure = apply(connecting, activity()).outcome!.failure!;
    expect(failure.code, JoinFailureCode.disconnected);
    expect(failure.stage, JoinStage.connecting);
    expect(failure.detail, contains('stopped the join'));
  });
  test('follow terminal retains its specific end and recovery message', () {
    final result = apply(
      launching,
      activity(end: LaunchSessionEndReason.worldFollowFailed),
    );
    expect(result.outcome!.sessionEnd, JoinSessionEndReason.worldFollowFailed);
    expect(
      joinStatusOf(result, null),
      "Robotopia could not follow the host's world, so it left the session.",
    );
  });
  test('verified exit wins over a successful join in the same update', () {
    final result = apply(
      launching,
      activity(status: 'succeeded', exited: true),
    );
    expect(result.outcome!.sessionEnd, JoinSessionEndReason.gameExited);
  });
}
