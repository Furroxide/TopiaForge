import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_join_support.dart';

const _request = '0f1e2d3c4b5a69788796a5b4c3d2e1f0';

LaunchProgress _progress(String phase, LaunchSessionRole role, String state) =>
    LaunchProgress(
      requestId: _request,
      sequence: 1,
      phase: phase,
      session: LaunchProgressSession(
        role: role,
        intentId: 'intent-1',
        state: LaunchSessionState.values.singleWhere(
          (item) => item.wireName == state,
        ),
      ),
    );

LaunchOutcome _launch({
  required String status,
  String phase = 'idle',
  String? sessionId,
  LaunchMultiplayerFailure? failure,
}) => LaunchOutcome(
  kind: 'launch',
  requestId: _request,
  command: 'join-session',
  sessionId: sessionId,
  sequence: 4,
  status: status,
  phase: phase,
  multiplayer: LaunchOutcomeMultiplayer(
    role: LaunchSessionRole.join,
    intentId: 'intent-1',
    failure: failure,
  ),
);

void main() {
  group('progress V2', () {
    test('only a join connects or synchronizes', () {
      expect(
        _progress('connecting', LaunchSessionRole.join, 'connecting').phase,
        'connecting',
      );
      expect(
        () => _progress('connecting', LaunchSessionRole.host, 'starting'),
        throwsFormatException,
      );
      expect(
        () => LaunchProgress(
          requestId: _request,
          sequence: 1,
          phase: 'connecting',
        ),
        throwsFormatException,
      );
    });

    test('each role has its own states, and both start pending', () {
      for (final state in LaunchSessionState.values) {
        final host = state.allows(LaunchSessionRole.host);
        final join = state.allows(LaunchSessionRole.join);
        expect(host || join, isTrue, reason: state.name);
        expect(host && join, state == LaunchSessionState.pending);
      }
    });

    test('version 1 is written exactly as before', () {
      final value = LaunchProgress(
        requestId: 'request-01',
        sessionId: 'session-01',
        sequence: 3,
        phase: 'running',
        nativeBusy: false,
      );
      expect(value.schemaVersion, 1);
      expect(
        LaunchTransport.writeProgress(value),
        '{"schemaVersion":1,"requestId":"request-01","sessionId":"session-01",'
        '"sequence":3,"phase":"running","nativeBusy":false}',
      );
    });
  });

  group('outcome V2', () {
    test('every join failure moves the join reducer', () async {
      final plan = await planJoin([
        (
          testPackage('example.sandbox'),
          installedOf(testManifest('example.sandbox')),
        ),
      ]);
      final connecting =
          [
            JoinTargetResolved(hostAServerId),
            JoinDescriptorVerified(plan.server),
            JoinPlanReady(plan),
            const JoinConsentGiven(),
            const JoinProfilePrepared(),
            const JoinGameStarted(),
          ].fold(
            startJoin(JoinLinkTarget(SessionLink.server(hostAServerId))),
            JoinSession.reduce,
          );
      expect(connecting.stage, JoinStage.connecting);
      final synchronizing = JoinSession.reduce(
        connecting,
        const JoinConnected(),
      );
      for (final failure in LaunchMultiplayerFailure.values) {
        final code = failure.joinFailureCode;
        if (!failure.allows(LaunchSessionRole.join)) {
          expect(code, isNull, reason: failure.name);
          continue;
        }
        final moved = [connecting, synchronizing].any((state) {
          final next = JoinSession.reduce(state, JoinFailed(code!));
          return !identical(next, state);
        });
        expect(moved, isTrue, reason: failure.wireName);
      }
    });

    test('every end reason is the join reducer\'s', () {
      for (final reason in LaunchSessionEndReason.values) {
        expect(reason.joinEndReason.name, reason.name);
      }
    });

    test('a launch succeeds at Running with a session or Idle without one', () {
      expect(
        _launch(
          status: 'succeeded',
          phase: 'running',
          sessionId: 'session-1',
        ).status,
        'succeeded',
      );
      expect(_launch(status: 'succeeded').status, 'succeeded');
      expect(
        () => _launch(status: 'succeeded', phase: 'running'),
        throwsFormatException,
      );
      expect(
        () => _launch(status: 'succeeded', phase: 'synchronizing'),
        throwsFormatException,
      );
      expect(
        _launch(
          status: 'failed',
          phase: 'connecting',
          failure: LaunchMultiplayerFailure.serverFull,
        ).multiplayer!.failure!.joinFailureCode,
        JoinFailureCode.serverFull,
      );
    });

    test('version 1 keeps its commands, version 2 keeps the session ones', () {
      expect(
        () => LaunchOutcome(
          kind: 'launch',
          requestId: _request,
          command: 'join-session',
          sequence: 1,
          status: 'failed',
          phase: 'idle',
          error: LaunchOperationError(code: 'unknown', message: 'x'),
        ),
        throwsFormatException,
      );
      expect(
        () => LaunchOutcome(
          kind: 'launch',
          requestId: _request,
          command: 'main-menu',
          sequence: 1,
          status: 'succeeded',
          phase: 'idle',
          multiplayer: LaunchOutcomeMultiplayer(
            role: LaunchSessionRole.join,
            intentId: 'intent-1',
          ),
        ),
        throwsFormatException,
      );
    });
  });

  group('launch activity', () {
    LaunchActivity activity(String command) => LaunchActivity(
      requestId: _request,
      profileId: 'mp-0123456789abcdef',
      profileRevision: 0,
      installIdentity: 'install',
      packageDigest: '0123456789abcdef',
      command: command,
    );

    test('session launches are activities with a terminal record', () {
      for (final command in sessionLaunchCommands) {
        final value = activity(command);
        expect(value.isSessionLaunch, isTrue);
        expect(value.expectsTerminalOutcome, isTrue);
        expect(value.sessionStarted, isFalse);
      }
      expect(activity('main-menu').expectsTerminalOutcome, isFalse);
      expect(activity('launch-target').expectsTerminalOutcome, isTrue);
      expect(() => activity('spectate'), throwsFormatException);
    });

    test('a join takes its own V2 records and refuses V1 ones', () {
      final join = activity('join-session')
          .applyProgress(
            _progress('connecting', LaunchSessionRole.join, 'connecting'),
          )
          .applyOutcome(
            _launch(
              status: 'succeeded',
              phase: 'running',
              sessionId: 'session-1',
            ),
          );
      expect(join.progress!.session!.state, LaunchSessionState.connecting);
      expect(join.sessionStarted, isTrue);
      final menu = LaunchOutcome(
        kind: 'launch',
        requestId: _request,
        command: 'main-menu',
        sequence: 9,
        status: 'succeeded',
        phase: 'idle',
      );
      expect(activity('join-session').applyOutcome(menu).launchOutcome, isNull);
    });
  });
}
