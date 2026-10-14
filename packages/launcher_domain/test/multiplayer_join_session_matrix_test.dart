import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_fixtures.dart';
import 'multiplayer_join_support.dart';

/// Every stage against every event: the reducer's whole transition table,
/// checked against an independent copy of the plan's table (6.4).
void main() {
  // The plan's failures, stage by stage. Cancellation is checked separately.
  const failures = <JoinStage, Set<JoinFailureCode>>{
    JoinStage.resolving: {
      JoinFailureCode.invalidLink,
      JoinFailureCode.inviteExpired,
      JoinFailureCode.serviceUnavailable,
      JoinFailureCode.rateLimited,
      JoinFailureCode.offline,
    },
    JoinStage.fetchingDescriptor: {
      JoinFailureCode.unreachable,
      JoinFailureCode.offline,
      JoinFailureCode.descriptorInvalid,
      // Invite and listing descriptors come from the control plane.
      JoinFailureCode.serviceUnavailable,
      JoinFailureCode.rateLimited,
    },
    JoinStage.checking: {
      JoinFailureCode.gameBuildMismatch,
      JoinFailureCode.runtimeOutdated,
      JoinFailureCode.contentUnavailable,
      JoinFailureCode.admissionBlocked,
      JoinFailureCode.standaloneOnServer,
    },
    JoinStage.awaitingConsent: {},
    JoinStage.downloading: {
      JoinFailureCode.downloadFailed,
      JoinFailureCode.integrityFailed,
      JoinFailureCode.manifestMismatch,
    },
    JoinStage.installing: {JoinFailureCode.installFailed},
    JoinStage.preparingProfile: {JoinFailureCode.profileSaveFailed},
    JoinStage.launching: {
      JoinFailureCode.repairNeeded,
      JoinFailureCode.gameRunning,
      JoinFailureCode.launchBlocked,
      JoinFailureCode.startFailed,
    },
    JoinStage.connecting: {
      JoinFailureCode.timeout,
      JoinFailureCode.natTraversalFailed,
      JoinFailureCode.relayUnavailable,
      JoinFailureCode.serverFull,
      JoinFailureCode.passwordRejected,
      JoinFailureCode.kicked,
      JoinFailureCode.protocolMismatch,
      JoinFailureCode.admissionChanged,
    },
    JoinStage.synchronizing: {
      JoinFailureCode.snapshotFailed,
      JoinFailureCode.disconnected,
    },
    JoinStage.inSession: {},
    JoinStage.ended: {},
  };
  const advances = <(JoinStage, String), JoinStage>{
    (JoinStage.resolving, 'resolved'): JoinStage.fetchingDescriptor,
    (JoinStage.fetchingDescriptor, 'descriptorVerified'): JoinStage.checking,
    (JoinStage.checking, 'planReady'): JoinStage.awaitingConsent,
    (JoinStage.awaitingConsent, 'consentGiven'): JoinStage.downloading,
    (JoinStage.downloading, 'downloadProgressed'): JoinStage.downloading,
    (JoinStage.downloading, 'downloadsCompleted'): JoinStage.installing,
    (JoinStage.installing, 'installCompleted'): JoinStage.preparingProfile,
    (JoinStage.preparingProfile, 'profilePrepared'): JoinStage.launching,
    (JoinStage.launching, 'gameStarted'): JoinStage.connecting,
    (JoinStage.connecting, 'connected'): JoinStage.synchronizing,
    (JoinStage.synchronizing, 'synchronized'): JoinStage.inSession,
  };
  const cancelAtOnce = {
    JoinStage.resolving,
    JoinStage.fetchingDescriptor,
    JoinStage.checking,
    JoinStage.awaitingConsent,
    JoinStage.downloading,
    JoinStage.connecting,
    JoinStage.synchronizing,
  };
  const cancelAfterStep = {
    JoinStage.installing,
    JoinStage.preparingProfile,
    JoinStage.launching,
  };

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
    );
  });

  JoinFailed failed(JoinFailureCode code) => switch (code) {
    JoinFailureCode.installFailed => const JoinFailed.installFailed(
      rollbackComplete: true,
    ),
    JoinFailureCode.gameRunning => const JoinFailed.gameRunning(
      ownership: JoinGameOwnership.owned,
    ),
    _ => JoinFailed(code),
  };

  final events = <String, JoinEvent Function()>{
    'resolved': () => JoinTargetResolved(hostAServerId),
    'descriptorVerified': () => JoinDescriptorVerified(server),
    'planReady': () => JoinPlanReady(plan),
    'consentGiven': () =>
        const JoinConsentGiven(privacy: MultiplayerAddressPrivacy.relayOnly),
    'downloadProgressed': () =>
        JoinDownloadProgressed(bytesDone: 5, bytesTotal: 10),
    'downloadsCompleted': () => const JoinDownloadsCompleted(),
    'installCompleted': () => const JoinInstallCompleted(),
    'profilePrepared': () => const JoinProfilePrepared(),
    'gameStarted': () => const JoinGameStarted(),
    'connected': () => const JoinConnected(),
    'synchronized': () => const JoinSynchronized(),
    'sessionEnded': () => const JoinSessionEnded(JoinSessionEndReason.left),
    'cancelRequested': () => const JoinCancelRequested(),
    for (final code in JoinFailureCode.values)
      if (code != JoinFailureCode.cancelled)
        'failed:${code.name}': () => failed(code),
  };

  /// A state at [stage], reached by the happy path.
  JoinSessionState at(JoinStage stage) {
    var state = JoinSession.start(
      JoinLinkTarget(SessionLink.server(hostAServerId)),
    );
    for (final name in [
      'resolved',
      'descriptorVerified',
      'planReady',
      'consentGiven',
      'downloadsCompleted',
      'installCompleted',
      'profilePrepared',
      'gameStarted',
      'connected',
      'synchronized',
      'sessionEnded',
    ]) {
      if (state.stage == stage) return state;
      state = JoinSession.reduce(state, events[name]!());
    }
    expect(state.stage, stage);
    return state;
  }

  test('the public failure table is the plan table', () {
    expect(JoinSession.failuresByStage, failures);
    expect(JoinSession.cancellableStages, cancelAtOnce);
    expect(JoinSession.deferredCancelStages, cancelAfterStep);
    expect(JoinStage.values.toSet(), failures.keys.toSet());
  });

  test('every failure but cancellation has a stage that produces it', () {
    final produced = {for (final codes in failures.values) ...codes};
    expect(JoinFailureCode.values.toSet().difference(produced), {
      JoinFailureCode.cancelled,
    });
  });

  for (final stage in JoinStage.values) {
    group('from ${stage.name}', () {
      for (final MapEntry(key: name, value: make) in events.entries) {
        test(name, () {
          final before = at(stage);
          final after = JoinSession.reduce(before, make());
          final advanced = advances[(stage, name)];
          final code = name.startsWith('failed:')
              ? JoinFailureCode.values.byName(name.substring(7))
              : null;

          if (stage == JoinStage.ended) {
            expect(after, same(before), reason: 'ended is terminal');
          } else if (advanced != null) {
            expect(after.stage, advanced);
            expect(after.outcome, isNull);
            if (name == 'downloadProgressed') {
              expect(after.progress!.bytesDone, 5);
            }
          } else if (stage == JoinStage.inSession && name == 'sessionEnded') {
            expect(after.stage, JoinStage.ended);
            expect(after.outcome!.sessionEnd, JoinSessionEndReason.left);
          } else if (name == 'cancelRequested' &&
              cancelAtOnce.contains(stage)) {
            expect(after.stage, JoinStage.ended);
            expect(after.outcome!.failure!.code, JoinFailureCode.cancelled);
            expect(after.outcome!.failure!.stage, stage);
          } else if (name == 'cancelRequested' &&
              cancelAfterStep.contains(stage)) {
            expect(after.stage, stage);
            expect(after.cancelRequested, isTrue);
          } else if (code != null && failures[stage]!.contains(code)) {
            if (code == JoinFailureCode.admissionChanged) {
              expect(after.stage, JoinStage.fetchingDescriptor);
              expect(after.replans, 1);
            } else {
              expect(after.stage, JoinStage.ended);
              expect(after.outcome!.failure!.code, code);
              expect(after.outcome!.failure!.stage, stage);
            }
          } else {
            expect(after, same(before), reason: 'not an event for $stage');
          }
        });
      }
    });
  }
}
