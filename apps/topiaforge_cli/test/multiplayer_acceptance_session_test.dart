import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/live_acceptance_session.dart';
import 'live_acceptance_test_fixture.dart';

void main() {
  for (final role in LaunchSessionRole.values) {
    for (final fault in [
      'none',
      'ack',
      'progress',
      'intent',
      'request',
      'process',
    ]) {
      test(
        'V5 ${role.name} confirmation requires correlated evidence: $fault',
        () async {
          final fixture = AcceptanceFixture();
          try {
            final context = fixture.admitIsolation(fixture.options());
            final settings = MultiplayerSettings(displayName: 'Acceptance');
            final request = role == LaunchSessionRole.host
                ? LaunchSessionRequest.host(
                    intentId: 'intent',
                    settings: settings,
                    privacy: SessionPrivacy.relayOnly,
                    host: LaunchHostOptions(name: 'Acceptance'),
                  )
                : LaunchSessionRequest.join(
                    intentId: 'intent',
                    settings: settings,
                    privacy: SessionPrivacy.relayOnly,
                    target: LaunchDirectTarget(
                      host: '127.0.0.1',
                      port: 12345,
                      serverId: 'a' * 64,
                      descriptorSha256: 'b' * 64,
                      packageDigest: 'c' * 64,
                    ),
                  );
            var now = DateTime.utc(2026);
            final process = LaunchProcessIdentity(
              pid: 42,
              startTimeUtc: now,
              executablePath: p.join(context.gameRoot, 'Game.exe'),
              nativeStartToken: 'windows:42',
            );
            var stopped = 0;
            final id = fault == 'request' ? 'foreign' : 'expected';
            final session = OwnedAcceptanceSession(
              context: context,
              process: process,
              expectedSession: request,
              acknowledgement: () async => fault == 'ack'
                  ? null
                  : AcceptanceIsolationAcknowledgement({
                      'requestId': 'expected',
                    }, 'd' * 64),
              outcome: () async => LaunchOutcome(
                kind: 'launch',
                requestId: id,
                sequence: 1,
                status: 'succeeded',
                phase: 'idle',
                command: role.command,
                multiplayer: LaunchOutcomeMultiplayer(
                  role: role,
                  intentId: 'intent',
                  serverId: 'a' * 64,
                ),
              ),
              progress: () async => fault == 'progress'
                  ? null
                  : LaunchProgress(
                      requestId: id,
                      sequence: 2,
                      phase: 'idle',
                      session: LaunchProgressSession(
                        role: role,
                        intentId: fault == 'intent' ? 'foreign' : 'intent',
                        serverId: 'a' * 64,
                        state: role == LaunchSessionRole.host
                            ? LaunchSessionState.hosting
                            : LaunchSessionState.inSession,
                      ),
                    ),
              processLiveness: (receipt) async {
                expect(identical(receipt, process), isTrue);
                return fault != 'process';
              },
              stopAndConfirmExit: (receipt) async {
                expect(identical(receipt, process), isTrue);
                stopped++;
              },
              dispose: () async {},
              clock: () => now,
              delay: (duration) async {
                now = now.add(duration);
              },
            );
            if (fault == 'none') {
              await session.confirm(const Duration(seconds: 1));
            } else {
              await expectLater(
                session.confirm(const Duration(seconds: 1)),
                throwsStateError,
              );
            }
            await session.close();
            await session.close();
            expect(stopped, 1);
          } finally {
            fixture.dispose();
          }
        },
      );
    }
  }
}
