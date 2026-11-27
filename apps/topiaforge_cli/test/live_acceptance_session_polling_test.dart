import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/live_acceptance_session.dart';

import 'live_acceptance_test_fixture.dart';

void main() {
  late AcceptanceFixture f;
  setUp(() => f = AcceptanceFixture());
  tearDown(() => f.dispose());

  for (final scenario in [
    (ack: false, running: false, stopFails: false, liveness: true),
    (ack: false, running: true, stopFails: false, liveness: true),
    (ack: true, running: false, stopFails: false, liveness: true),
    (ack: true, running: true, stopFails: false, liveness: true),
    (ack: true, running: true, stopFails: true, liveness: true),
    (ack: true, running: true, stopFails: false, liveness: false),
    (ack: true, running: true, stopFails: false, liveness: null),
  ]) {
    test(
      'production polling ACK=${scenario.ack} Running=${scenario.running} '
      'stopFails=${scenario.stopFails} liveness=${scenario.liveness}',
      () async {
        late OwnedAcceptanceSession session;
        late LaunchProcessIdentity original;
        late LaunchProcessIdentity unrelated;
        final stopped = <LaunchProcessIdentity>[];
        final probed = <LaunchProcessIdentity>[];
        var originalAlive = scenario.liveness;
        var disposals = 0;
        var polls = 0;
        var clock = DateTime.now().toUtc();
        final runner = f.runner(
          commandRunner: (_) async => 0,
          sessionLauncher: (_, context, challenge) async {
            f.writePassingRun();
            original = LaunchProcessIdentity(
              pid: 4242,
              startTimeUtc: clock,
              executablePath: p.join(context.gameRoot, 'Game.exe'),
              nativeStartToken: 'windows:1234',
            );
            unrelated = LaunchProcessIdentity(
              pid: 4243,
              startTimeUtc: clock,
              executablePath: original.executablePath,
              nativeStartToken: 'windows:5678',
            );
            final profile = ProfileLaunchConfigurationV4(
              profileId: 'synthetic-qa',
              profileRevision: 1,
              requestId: 'monitor-request',
              command: 'launch-target',
              safeMode: false,
              inheritManagerModState: false,
              enabledMods: const [],
              selectedVersions: const {},
              packages: const [],
              plan: LaunchPlanDescriptor(
                targetId: 'dev.topiaforge.sdk-acceptance.menu',
                gamemodeId: 'dev.topiaforge.sdk-acceptance.mode',
                worldId: 'dev.topiaforge.sdk-acceptance.world',
                transition: 'additive-arena',
                request: LaunchRequest(
                  targetId: 'dev.topiaforge.sdk-acceptance.menu',
                ),
                packages: const [],
              ),
            );
            final store = LaunchStagingStore(context.gameRoot);
            await store.writeRequest(profile);
            final request = await store.writeAcceptanceRequest(
              context: context,
              profile: profile,
              challenge: challenge,
              now: clock,
            );
            File channel(String name) => File(
              p.join(
                context.managerRoot,
                'staging',
                '$name-${LaunchStorageKeys.request(profile.requestId)}.json',
              ),
            );
            // Both outcome channels are genuine guarded JSON documents. Neither
            // the completed session nor passing instrumentation can substitute for
            // the missing private isolation ACK and Running launch outcome.
            channel('session-outcome').writeAsStringSync(
              jsonEncode(
                LaunchOutcome(
                  kind: 'session',
                  requestId: profile.requestId,
                  sequence: 6,
                  status: 'succeeded',
                  phase: 'idle',
                  sessionId: 'completed-session',
                ).toJson(),
              ),
            );
            if (scenario.running) {
              channel('launch-outcome').writeAsStringSync(
                jsonEncode(
                  LaunchOutcome(
                    kind: 'launch',
                    requestId: profile.requestId,
                    command: 'launch-target',
                    sequence: 4,
                    status: 'succeeded',
                    phase: 'running',
                    sessionId: 'completed-session',
                  ).toJson(),
                ),
              );
            }
            if (scenario.ack) {
              channel('acceptance-isolation-ack').writeAsStringSync(
                jsonEncode({
                  'schemaVersion': 1,
                  'requestId': profile.requestId,
                  'challenge': challenge,
                  'requestSha256': request.sha256Digest,
                  'status': 'admitted',
                  'process': {
                    'pid': original.pid,
                    'nativeStartToken': original.nativeStartToken,
                    'executablePath': original.executablePath,
                  },
                  'observedOsIdentity': context.identity.toJson(),
                  'observedRoots': context.runtimeRoots(),
                  'reasons': <Object?>[],
                }),
              );
            }
            session = OwnedAcceptanceSession(
              context: context,
              process: original,
              acknowledgement: () {
                polls++;
                return store.readAcceptanceAcknowledgement(request, original);
              },
              outcome: () =>
                  store.readOutcome(profile.requestId, session: false),
              processLiveness: (receipt) async {
                probed.add(receipt);
                expect(identical(receipt, original), isTrue);
                return originalAlive;
              },
              stopAndConfirmExit: (receipt) async {
                stopped.add(receipt);
                expect(identical(receipt, original), isTrue);
                if (scenario.stopFails) {
                  throw StateError('Owned exit unconfirmed.');
                }
                originalAlive = false;
              },
              dispose: () async {
                disposals++;
              },
              clock: () => clock,
              delay: (duration) async {
                clock = clock.add(duration);
              },
            );
            return session;
          },
        );
        final success =
            scenario.ack &&
            scenario.running &&
            !scenario.stopFails &&
            scenario.liveness == true;
        if (success) {
          final result = await runner.run(f.options());
          expect(result.succeeded, isTrue);
          expect(session.isolationEvidence['processExitConfirmed'], isTrue);
        } else {
          await expectLater(
            runner.run(f.options()),
            throwsA(
              isA<StateError>().having(
                (error) => error.message,
                'reason',
                contains('unconfirmed'),
              ),
            ),
          );
        }
        expect(stopped, hasLength(1));
        expect(identical(stopped.single, original), isTrue);
        expect(
          stopped.any((receipt) => identical(receipt, unrelated)),
          isFalse,
        );
        expect(disposals, 1);
        expect(originalAlive, scenario.stopFails ? scenario.liveness : false);
        expect(polls, greaterThanOrEqualTo(1));
        expect(probed, isNotEmpty);
        if (!scenario.ack || scenario.stopFails) {
          expect(() => session.isolationEvidence, throwsStateError);
        }
        if (scenario.stopFails) {
          await expectLater(session.close(), throwsStateError);
        } else {
          await session.close();
        }
        expect(stopped, hasLength(1), reason: 'Owned cleanup is idempotent.');
        expect(disposals, 1);
        expect(
          File(p.join(f.output.path, 'acceptance-result.json')).existsSync(),
          success,
        );
        expect(
          Directory(p.join(f.sourceGame.path, 'BepInEx')).existsSync(),
          isFalse,
        );
      },
    );
  }
}
