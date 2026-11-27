import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_fixtures.dart';
import 'multiplayer_join_support.dart';

/// What a join does when the control plane no longer has what its target
/// named: an invite that misses, or a server it no longer lists. Both can
/// happen while resolving, and again once a re-plan asks the service for the
/// descriptor a second time.
void main() {
  late VerifiedServerDescriptor server;
  late JoinPlan plan;
  setUpAll(() async {
    server = await verifiedForTest(
      testDescriptor(packages: [testPackage('example.sandbox')]),
    );
    plan = const JoinPlanner().plan(
      server: server,
      resolutions: {
        testSha('example.sandbox'): availableOf(
          testManifest('example.sandbox'),
        ),
      },
      environment: testEnvironment,
      mayContactHost: true,
    );
  });

  final invite = JoinLinkTarget(SessionLink.invite('RJ9JM-V1V09'));
  final serverLink = JoinLinkTarget(SessionLink.server(hostAServerId));
  final listing = JoinListingTarget(
    serverId: hostAServerId,
    source: ServerListingSource.internet,
  );

  JoinSessionState run(JoinSessionState state, List<JoinEvent> events) =>
      events.fold(state, JoinSession.reduce);

  /// A join of [target] that reached connecting, then saw admission change
  /// once, so it fetches the descriptor again.
  JoinSessionState replanning(JoinTarget target) {
    final state = run(startJoin(target), [
      JoinTargetResolved(hostAServerId),
      JoinDescriptorVerified(server),
      JoinPlanReady(plan),
      const JoinConsentGiven(),
      const JoinDownloadsCompleted(),
      const JoinInstallCompleted(),
      const JoinProfilePrepared(),
      const JoinGameStarted(),
      JoinFailed(JoinFailureCode.admissionChanged),
    ]);
    expect(state.stage, JoinStage.fetchingDescriptor);
    expect(state.replans, 1);
    expect(state.descriptorRequest!.target, same(target));
    return state;
  }

  JoinFailure failureOf(JoinSessionState state) {
    expect(state.stage, JoinStage.ended);
    return state.outcome!.failure!;
  }

  test('a server the service no longer lists ends the join while '
      'resolving', () {
    for (final target in [serverLink, listing]) {
      final state = startJoin(target);
      expect(state.stage, JoinStage.resolving);
      final failure = failureOf(
        JoinSession.reduce(
          state,
          JoinFailed(
            JoinFailureCode.serverNotListed,
            detail: 'That server is not listed any more.',
          ),
        ),
      );
      expect(failure.code, JoinFailureCode.serverNotListed);
      expect(failure.stage, JoinStage.resolving);
      expect(failure.detail, 'That server is not listed any more.');
    }
  });

  test('an invite that misses ends the join while resolving', () {
    final failure = failureOf(
      JoinSession.reduce(
        startJoin(invite),
        JoinFailed(JoinFailureCode.inviteExpired),
      ),
    );
    expect(failure.code, JoinFailureCode.inviteExpired);
    expect(failure.stage, JoinStage.resolving);
  });

  test('a re-plan that finds the server gone ends the join while fetching '
      'the descriptor', () {
    for (final target in [serverLink, listing]) {
      final failure = failureOf(
        JoinSession.reduce(
          replanning(target),
          JoinFailed(JoinFailureCode.serverNotListed),
        ),
      );
      expect(failure.code, JoinFailureCode.serverNotListed);
      expect(failure.stage, JoinStage.fetchingDescriptor);
    }
  });

  test('a re-plan whose invite misses ends the join while fetching the '
      'descriptor', () {
    final failure = failureOf(
      JoinSession.reduce(
        replanning(invite),
        JoinFailed(JoinFailureCode.inviteExpired),
      ),
    );
    expect(failure.code, JoinFailureCode.inviteExpired);
    expect(failure.stage, JoinStage.fetchingDescriptor);
  });

  test('neither miss is a failure of any later step', () {
    final checking = run(startJoin(serverLink), [
      JoinTargetResolved(hostAServerId),
      JoinDescriptorVerified(server),
    ]);
    expect(checking.stage, JoinStage.checking);
    for (final code in [
      JoinFailureCode.inviteExpired,
      JoinFailureCode.serverNotListed,
    ]) {
      expect(JoinSession.reduce(checking, JoinFailed(code)), same(checking));
    }
  });
}
