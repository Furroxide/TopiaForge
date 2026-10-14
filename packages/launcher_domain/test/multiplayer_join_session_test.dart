import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_fixtures.dart';
import 'multiplayer_join_support.dart';

void main() {
  late VerifiedServerDescriptor server;
  late JoinPlan downloadPlan;
  late JoinPlan installedPlan;
  late JoinPlan changedPlan;
  late JoinPlan hostServedPlan;
  late VerifiedServerDescriptor hostServedServer;
  final sandbox = testManifest('example.sandbox', capabilities: ['physics']);
  final target = JoinLinkTarget(SessionLink.server(hostAServerId));

  setUpAll(() async {
    const planner = JoinPlanner();
    server = await verifiedForTest(
      testDescriptor(packages: [testPackage('example.sandbox')]),
    );
    final sha = testSha('example.sandbox');
    downloadPlan = planner.plan(
      server: server,
      resolutions: {sha: availableOf(sandbox)},
      environment: testEnvironment,
    );
    installedPlan = planner.plan(
      server: server,
      resolutions: {sha: installedOf(sandbox)},
      environment: testEnvironment,
    );
    final changed = await verifiedForTest(
      testDescriptor(
        packages: [testPackage('example.sandbox', version: '1.1.0')],
        issuedAt: 1791849700,
      ),
    );
    changedPlan = planner.plan(
      server: changed,
      resolutions: {
        testSha('example.sandbox', '1.1.0'): availableOf(
          testManifest('example.sandbox', version: '1.1.0'),
        ),
      },
      environment: testEnvironment,
    );
    hostServedServer = await verifiedForTest(
      testDescriptor(
        packages: [testPackage('example.local')],
        hostServesPackages: true,
      ),
    );
    hostServedPlan = planner.plan(
      server: hostServedServer,
      resolutions: {
        testSha('example.local'): JoinPackageResolution.hostOffered(),
      },
      environment: environmentWith(developerMode: true),
    );
  });

  JoinSessionState run(JoinSessionState state, List<JoinEvent> events) =>
      events.fold(state, JoinSession.reduce);

  JoinSessionState toConsent([JoinTarget? from]) =>
      run(startJoin(from ?? target), [
        JoinTargetResolved(hostAServerId),
        JoinDescriptorVerified(server),
        JoinPlanReady(downloadPlan),
      ]);

  JoinSessionState toConnecting() => run(toConsent(), const [
    JoinConsentGiven(),
    JoinDownloadsCompleted(),
    JoinInstallCompleted(),
    JoinProfilePrepared(),
    JoinGameStarted(),
  ]);

  JoinFailure failureOf(JoinSessionState state) {
    expect(state.stage, JoinStage.ended);
    return state.outcome!.failure!;
  }

  group('the join flow', () {
    test('walks every step into the session and out', () {
      var state = startJoin(target, privacy: MultiplayerAddressPrivacy.direct);
      expect(state.stage, JoinStage.resolving);
      expect(state.serverId, isNull);
      expect(state.privacy, MultiplayerAddressPrivacy.direct);
      expect(state.descriptorRequest, isNull);
      state = JoinSession.reduce(state, JoinTargetResolved(hostAServerId));
      expect(state.stage, JoinStage.fetchingDescriptor);
      expect(state.serverId, hostAServerId);
      expect(state.descriptorRequest!.expectedServerId, hostAServerId);
      expect(state.descriptorRequest!.allowsDirect, isTrue);
      state = JoinSession.reduce(state, JoinDescriptorVerified(server));
      expect(state.stage, JoinStage.checking);
      expect(state.descriptor, same(server));
      state = JoinSession.reduce(state, JoinPlanReady(downloadPlan));
      expect(state.stage, JoinStage.awaitingConsent);
      expect(state.plan, same(downloadPlan));
      expect(state.consentKey, isNull);
      state = JoinSession.reduce(state, const JoinConsentGiven());
      expect(state.stage, JoinStage.downloading);
      expect(state.consentKey, downloadPlan.consentKey);
      expect(state.privacy, MultiplayerAddressPrivacy.direct);
      expect(state.progress!.bytesTotal, 0);
      state = JoinSession.reduce(
        state,
        JoinDownloadProgressed(bytesDone: 5, bytesTotal: 10),
      );
      expect(state.progress!.fraction, 0.5);
      state = run(state, const [
        JoinDownloadsCompleted(),
        JoinInstallCompleted(),
        JoinProfilePrepared(),
        JoinGameStarted(),
        JoinConnected(),
        JoinSynchronized(),
      ]);
      expect(state.stage, JoinStage.inSession);
      expect(state.progress, isNull);
      expect(state.isEnded, isFalse);
      state = JoinSession.reduce(
        state,
        const JoinSessionEnded(JoinSessionEndReason.hostEnded),
      );
      expect(state.isEnded, isTrue);
      expect(state.outcome!.sessionEnd, JoinSessionEndReason.hostEnded);
      expect(state.outcome!.failure, isNull);
      expect(state.outcome!.wasCancelled, isFalse);
    });

    test('skips downloading when every package is installed', () {
      final state = run(startJoin(target), [
        JoinTargetResolved(hostAServerId),
        JoinDescriptorVerified(server),
        JoinPlanReady(installedPlan),
        const JoinConsentGiven(),
      ]);
      expect(state.stage, JoinStage.preparingProfile);
      expect(state.progress, isNull);
    });

    test('every session end reason is kept', () {
      for (final reason in JoinSessionEndReason.values) {
        final state = run(toConnecting(), [
          const JoinConnected(),
          const JoinSynchronized(),
          JoinSessionEnded(reason),
        ]);
        expect(state.outcome!.sessionEnd, reason);
      }
    });

    test('an invite resolves to whichever server the service names', () {
      final invite = JoinLinkTarget(SessionLink.invite('ABCDE-12345'));
      expect(invite.pinnedServerId, isNull);
      final state = JoinSession.reduce(
        startJoin(invite),
        JoinTargetResolved('b' * 64),
      );
      expect(state.stage, JoinStage.fetchingDescriptor);
      expect(state.serverId, 'b' * 64);
    });

    test('a target that names a server cannot resolve to another', () {
      for (final pinned in [
        target,
        JoinListingTarget(
          serverId: hostAServerId,
          source: ServerListingSource.internet,
        ),
      ]) {
        final failure = failureOf(
          JoinSession.reduce(startJoin(pinned), JoinTargetResolved('b' * 64)),
        );
        expect(failure.code, JoinFailureCode.invalidLink);
        expect(failure.stage, JoinStage.resolving);
      }
    });

    test('a descriptor for another server is invalid', () {
      // The invite resolved to server b, but host A's descriptor arrived.
      final invite = JoinLinkTarget(SessionLink.invite('ABCDE12345'));
      final state = run(startJoin(invite), [
        JoinTargetResolved('b' * 64),
        JoinDescriptorVerified(server),
      ]);
      final failure = failureOf(state);
      expect(failure.code, JoinFailureCode.descriptorInvalid);
      expect(failure.stage, JoinStage.fetchingDescriptor);
    });
  });

  group('checking and consent', () {
    test('an unjoinable plan ends the join with its first blocker', () async {
      final blocked = const JoinPlanner().plan(
        server: server,
        resolutions: const {},
        environment: environmentWith(gameBuild: '2478'),
      );
      final state = run(startJoin(target), [
        JoinTargetResolved(hostAServerId),
        JoinDescriptorVerified(server),
        JoinPlanReady(blocked),
      ]);
      final failure = failureOf(state);
      expect(failure.code, JoinFailureCode.gameBuildMismatch);
      expect(failure.stage, JoinStage.checking);
      expect(failure.blockers, blocked.blockers);
      expect(failure.blockers.map((item) => item.code), [
        JoinBlockerCode.gameBuildMismatch,
        JoinBlockerCode.contentUnavailable,
      ]);
      expect(failure.detail, blocked.blockers.first.message);
    });

    test('each blocker becomes the matching failure', () async {
      final cases = <JoinBlockerCode, JoinPlan>{
        JoinBlockerCode.contentUnavailable: const JoinPlanner().plan(
          server: server,
          resolutions: const {},
          environment: testEnvironment,
        ),
      };
      final solo = await verifiedForTest(
        testDescriptor(packages: [testPackage('example.solo')]),
      );
      cases[JoinBlockerCode.standaloneOnServer] = const JoinPlanner().plan(
        server: solo,
        resolutions: {
          testSha('example.solo'): installedOf(
            testManifest('example.solo', mode: 'standalone'),
          ),
        },
        environment: testEnvironment,
      );
      final newer = await verifiedForTest(
        testDescriptor(protocolVersion: '2.0.0', runtimeVersion: '0.3.0'),
      );
      cases[JoinBlockerCode.runtimeOutdated] = const JoinPlanner().plan(
        server: newer,
        resolutions: const {},
        environment: testEnvironment,
      );
      final older = await verifiedForTest(
        testDescriptor(protocolVersion: '0.9.0', runtimeVersion: '0.1.0'),
      );
      cases[JoinBlockerCode.admissionBlocked] = const JoinPlanner().plan(
        server: older,
        resolutions: const {},
        environment: testEnvironment,
      );
      cases.forEach((code, plan) {
        final state = run(
          startJoin(
            JoinListingTarget(
              serverId: hostAServerId,
              source: ServerListingSource.internet,
            ),
          ),
          [
            JoinTargetResolved(hostAServerId),
            JoinDescriptorVerified(plan.server),
            JoinPlanReady(plan),
          ],
        );
        expect(failureOf(state).code.name, code.name, reason: code.name);
      });
    });

    test('a plan for an older descriptor is stale and ignored', () async {
      final checking = run(startJoin(target), [
        JoinTargetResolved(hostAServerId),
        JoinDescriptorVerified(server),
      ]);
      expect(
        JoinSession.reduce(checking, JoinPlanReady(changedPlan)),
        same(checking),
      );
    });

    test('host-served packages need the developer-mode acknowledgement', () {
      final waiting = run(startJoin(target), [
        JoinTargetResolved(hostAServerId),
        JoinDescriptorVerified(hostServedServer),
        JoinPlanReady(hostServedPlan),
      ]);
      expect(waiting.stage, JoinStage.awaitingConsent);
      const plain = JoinConsentGiven();
      expect(JoinSession.reduce(waiting, plain), same(waiting));
      final consented = JoinSession.reduce(
        waiting,
        const JoinConsentGiven(hostServedAcknowledged: true),
      );
      expect(consented.stage, JoinStage.downloading);
    });
  });

  group('the automatic re-plan', () {
    test('happens once, and skips consent when nothing consented changed', () {
      var state = JoinSession.reduce(
        toConnecting(),
        JoinFailed(JoinFailureCode.admissionChanged),
      );
      expect(state.stage, JoinStage.fetchingDescriptor);
      expect(state.replans, 1);
      expect(state.descriptor, isNull);
      expect(state.plan, isNull);
      expect(state.consentKey, downloadPlan.consentKey);
      expect(state.privacy, MultiplayerAddressPrivacy.relayOnly);
      state = run(state, [
        JoinDescriptorVerified(server),
        JoinPlanReady(installedPlan),
      ]);
      expect(
        state.stage,
        JoinStage.preparingProfile,
        reason: 'no second consent',
      );
      state = run(state, const [JoinProfilePrepared(), JoinGameStarted()]);
      expect(state.stage, JoinStage.connecting);
      final failure = failureOf(
        JoinSession.reduce(state, JoinFailed(JoinFailureCode.admissionChanged)),
      );
      expect(failure.code, JoinFailureCode.admissionChanged);
      expect(failure.stage, JoinStage.connecting);
    });

    test('asks for consent again when the plan changed', () {
      var state = JoinSession.reduce(
        toConnecting(),
        JoinFailed(JoinFailureCode.admissionChanged),
      );
      state = run(state, [
        JoinDescriptorVerified(changedPlan.server),
        JoinPlanReady(changedPlan),
      ]);
      expect(state.stage, JoinStage.awaitingConsent);
      state = JoinSession.reduce(state, const JoinConsentGiven());
      expect(state.stage, JoinStage.downloading);
      expect(state.consentKey, changedPlan.consentKey);
    });
  });

  group('cancellation', () {
    test('stops at once where the work can be abandoned', () {
      for (final state in [
        startJoin(target),
        toConsent(),
        run(toConsent(), const [JoinConsentGiven()]),
        toConnecting(),
      ]) {
        final ended = JoinSession.reduce(state, const JoinCancelRequested());
        expect(failureOf(ended).code, JoinFailureCode.cancelled);
        expect(failureOf(ended).stage, state.stage);
        expect(ended.outcome!.wasCancelled, isTrue);
      }
    });

    test('waits for installing, the profile and the launch to finish', () {
      final installing = run(toConsent(), const [
        JoinConsentGiven(),
        JoinDownloadsCompleted(),
      ]);
      final steps = <JoinSessionState, JoinEvent>{
        installing: const JoinInstallCompleted(),
        JoinSession.reduce(installing, const JoinInstallCompleted()):
            const JoinProfilePrepared(),
        run(installing, const [JoinInstallCompleted(), JoinProfilePrepared()]):
            const JoinGameStarted(),
      };
      steps.forEach((state, completion) {
        final held = JoinSession.reduce(state, const JoinCancelRequested());
        expect(held.stage, state.stage);
        expect(held.cancelRequested, isTrue);
        expect(
          JoinSession.reduce(held, const JoinCancelRequested()).cancelRequested,
          isTrue,
        );
        final ended = JoinSession.reduce(held, completion);
        expect(failureOf(ended).code, JoinFailureCode.cancelled);
        expect(failureOf(ended).stage, state.stage);
        expect(ended.cancelRequested, isFalse);
      });
    });

    test('a step that fails after a cancel request reports its failure', () {
      final held = JoinSession.reduce(
        run(toConsent(), const [JoinConsentGiven(), JoinDownloadsCompleted()]),
        const JoinCancelRequested(),
      );
      final failure = failureOf(
        JoinSession.reduce(
          held,
          const JoinFailed.installFailed(rollbackComplete: false),
        ),
      );
      expect(failure.code, JoinFailureCode.installFailed);
      expect(failure.rollbackComplete, isFalse);
    });
  });

  group('failure details', () {
    test('are carried into the outcome', () {
      final launching = run(toConsent(), const [
        JoinConsentGiven(),
        JoinDownloadsCompleted(),
        JoinInstallCompleted(),
        JoinProfilePrepared(),
      ]);
      final owned = failureOf(
        JoinSession.reduce(
          launching,
          const JoinFailed.gameRunning(
            ownership: JoinGameOwnership.owned,
            detail: 'Restart into the server?',
          ),
        ),
      );
      expect(owned.gameOwnership, JoinGameOwnership.owned);
      expect(owned.detail, 'Restart into the server?');
      final foreign = failureOf(
        JoinSession.reduce(
          launching,
          const JoinFailed.gameRunning(ownership: JoinGameOwnership.foreign),
        ),
      );
      expect(foreign.gameOwnership, JoinGameOwnership.foreign);
      final rolledBack = failureOf(
        JoinSession.reduce(
          run(toConsent(), const [
            JoinConsentGiven(),
            JoinDownloadsCompleted(),
          ]),
          const JoinFailed.installFailed(rollbackComplete: true),
        ),
      );
      expect(rolledBack.rollbackComplete, isTrue);
      expect(rolledBack.blockers, isEmpty);
    });

    test('events refuse what they cannot mean', () {
      for (final code in [
        JoinFailureCode.cancelled,
        JoinFailureCode.installFailed,
        JoinFailureCode.gameRunning,
      ]) {
        expect(() => JoinFailed(code), throwsArgumentError, reason: code.name);
      }
      expect(
        () => JoinDownloadProgressed(bytesDone: 11, bytesTotal: 10),
        throwsArgumentError,
      );
      expect(
        () => JoinDownloadProgressed(bytesDone: -1, bytesTotal: 10),
        throwsArgumentError,
      );
      expect(() => JoinTargetResolved('A' * 64), throwsArgumentError);
      expect(
        () => JoinListingTarget(serverId: 'x', source: ServerListingSource.lan),
        throwsArgumentError,
      );
    });
  });
}
