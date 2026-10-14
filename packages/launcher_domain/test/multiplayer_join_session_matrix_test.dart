import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_fixtures.dart';
import 'multiplayer_join_support.dart';

/// Every stage against every event: the reducer's whole transition table,
/// checked against an independent copy of the plan's table (6.4).
void main() {
  // The plan's failures, stage by stage, as JoinFailed reports them.
  // Cancellation and game exits have their own events, checked separately.
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
      // Relay-only refuses a host beyond the local network; a typed address
      // that serves another server than before is an identity change. Only
      // a typed address learns its server, so only its join can produce one.
      JoinFailureCode.directNotAllowed,
      JoinFailureCode.identityChanged,
    },
    JoinStage.checking: {
      JoinFailureCode.gameBuildMismatch,
      JoinFailureCode.runtimeOutdated,
      JoinFailureCode.contentUnavailable,
      JoinFailureCode.admissionBlocked,
      JoinFailureCode.standaloneOnServer,
      // A plan that needs the host when the host may not be contacted.
      JoinFailureCode.directNotAllowed,
    },
    JoinStage.awaitingConsent: {},
    JoinStage.downloading: {
      JoinFailureCode.downloadFailed,
      JoinFailureCode.integrityFailed,
      JoinFailureCode.manifestMismatch,
      // A host download the contact rules forbid once names are resolved.
      JoinFailureCode.directNotAllowed,
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
      JoinFailureCode.disconnected,
      // The game resolves the target again, and refuses a host the
      // session's privacy forbids once the network has changed.
      JoinFailureCode.directNotAllowed,
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
  const endedByGameExit = {
    JoinStage.launching,
    JoinStage.connecting,
    JoinStage.synchronizing,
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
      mayContactHost: true,
    );
  });

  JoinFailed failed(JoinFailureCode code) => switch (code) {
    JoinFailureCode.installFailed => const JoinFailed.installFailed(
      rollbackComplete: true,
    ),
    JoinFailureCode.gameRunning => const JoinFailed.gameRunning(
      ownership: JoinGameOwnership.owned,
    ),
    JoinFailureCode.identityChanged => JoinFailed.identityChanged(
      servedServerId: 'b' * 64,
    ),
    _ => JoinFailed(code),
  };

  final events = <String, JoinEvent Function()>{
    'resolved': () => JoinTargetResolved(hostAServerId),
    'descriptorVerified': () => JoinDescriptorVerified(server),
    'planReady': () => JoinPlanReady(plan),
    'consentGiven': () => const JoinConsentGiven(),
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
    'gameExited': () => const JoinGameExited(),
    for (final code in JoinFailureCode.values)
      if (code != JoinFailureCode.cancelled &&
          code != JoinFailureCode.gameExited)
        'failed:${code.name}': () => failed(code),
  };

  /// A state at [stage], reached by the happy path of a server link, or of
  /// [target] when it is given.
  JoinSessionState at(JoinStage stage, [JoinTarget? target]) {
    var state = startJoin(
      target ?? JoinLinkTarget(SessionLink.server(hostAServerId)),
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
    expect(JoinSession.gameExitStages, endedByGameExit);
    expect(JoinStage.values.toSet(), failures.keys.toSet());
  });

  test('every failure but cancellation and game exits has a stage that '
      'produces it', () {
    final produced = {for (final codes in failures.values) ...codes};
    expect(JoinFailureCode.values.toSet().difference(produced), {
      JoinFailureCode.cancelled,
      JoinFailureCode.gameExited,
    });
    expect(() => JoinFailed(JoinFailureCode.gameExited), throwsArgumentError);
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
          } else if (name == 'gameExited' && endedByGameExit.contains(stage)) {
            expect(after.stage, JoinStage.ended);
            expect(after.outcome!.failure!.code, JoinFailureCode.gameExited);
            expect(after.outcome!.failure!.stage, stage);
          } else if (name == 'gameExited' && stage == JoinStage.inSession) {
            expect(after.stage, JoinStage.ended);
            expect(after.outcome!.sessionEnd, JoinSessionEndReason.gameExited);
          } else if (code == JoinFailureCode.identityChanged &&
              failures[stage]!.contains(code)) {
            // The server link names its server; a typed address does not.
            expect(after, same(before), reason: 'the link names its server');
            final typed = at(
              stage,
              JoinAddressTarget(host: '192.168.1.20', port: 27250),
            );
            final changed = JoinSession.reduce(typed, make());
            expect(changed.stage, JoinStage.ended);
            expect(changed.outcome!.failure!.code, code);
            expect(changed.outcome!.failure!.stage, stage);
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

  test('a game that exits or a link that drops ends a join while '
      'connecting', () {
    final connecting = at(JoinStage.connecting);
    final exited = JoinSession.reduce(connecting, const JoinGameExited());
    expect(exited.outcome!.failure!.code, JoinFailureCode.gameExited);
    final dropped = JoinSession.reduce(
      connecting,
      JoinFailed(JoinFailureCode.disconnected, detail: 'The link dropped.'),
    );
    expect(dropped.outcome!.failure!.code, JoinFailureCode.disconnected);
    expect(dropped.outcome!.failure!.detail, 'The link dropped.');
    // A launch failure is not a connecting failure: the game is already up.
    expect(
      JoinSession.reduce(connecting, JoinFailed(JoinFailureCode.startFailed)),
      same(connecting),
    );
  });
}
