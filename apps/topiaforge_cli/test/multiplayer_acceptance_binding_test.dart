import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/live_acceptance_session.dart';

import 'live_acceptance_test_fixture.dart';

void main() {
  for (final fault in [
    'none',
    'server',
    'challenge',
    'generation',
    'role',
    'intent',
    'request',
    'dead',
  ]) {
    test('join admission reads owned V5 channels and rejects $fault', () async {
      final fixture = AcceptanceFixture();
      addTearDown(fixture.dispose);
      final context = fixture.admitIsolation(fixture.options());
      final store = LaunchStagingStore(context.gameRoot);
      final profile = ProfileLaunchConfigurationV4(
        profileId: 'qa',
        profileRevision: 1,
        requestId: 'owned-join',
        command: 'main-menu',
        safeMode: false,
        inheritManagerModState: false,
        enabledMods: const [],
        selectedVersions: const {},
        packages: const [],
      );
      final request = LaunchSessionRequest.join(
        intentId: 'owned-intent',
        settings: MultiplayerSettings(displayName: 'Acceptance'),
        privacy: SessionPrivacy.relayOnly,
        target: LaunchDirectTarget(
          host: '127.0.0.1',
          port: 12345,
          serverId: 'a' * 64,
          descriptorSha256: 'b' * 64,
          packageDigest: 'c' * 64,
        ),
      );
      final staged = StagedLaunchRequest.forLaunch(profile, session: request);
      await store.writeLaunchRequest(staged);
      var now = DateTime.utc(2026);
      final binding = await store.writeAcceptanceRequest(
        context: context,
        profile: profile,
        request: staged,
        challenge: 'd' * 64,
        now: now,
      );
      final process = LaunchProcessIdentity(
        pid: 42,
        startTimeUtc: now,
        executablePath: p.join(context.gameRoot, 'inert.exe'),
        nativeStartToken: 'windows:42',
      );
      void write(String channel, Map<String, Object?> value) => File(
        p.join(
          context.managerRoot,
          'staging',
          '$channel-${LaunchStorageKeys.request(profile.requestId)}.json',
        ),
      ).writeAsStringSync(jsonEncode(value));
      write('acceptance-isolation-ack', {
        'schemaVersion': 1,
        'requestId': profile.requestId,
        'challenge': (fault == 'challenge' ? 'e' : 'd') * 64,
        'requestSha256': binding.sha256Digest,
        'status': 'admitted',
        'process': {
          'pid': process.pid,
          'nativeStartToken': fault == 'generation'
              ? 'windows:43'
              : process.nativeStartToken,
          'executablePath': process.executablePath,
        },
        'observedOsIdentity': context.identity.toJson(),
        'observedRoots': context.runtimeRoots(),
        'reasons': <Object?>[],
      });
      final serverId = (fault == 'server' ? 'e' : 'a') * 64;
      final id = fault == 'request' ? 'foreign' : profile.requestId;
      final intent = fault == 'intent' ? 'foreign' : request.intentId;
      final role = fault == 'role'
          ? LaunchSessionRole.host
          : LaunchSessionRole.join;
      write(
        'launch-outcome',
        LaunchOutcome(
          kind: 'launch',
          requestId: id,
          sequence: 1,
          status: 'succeeded',
          phase: 'idle',
          command: role.command,
          multiplayer: LaunchOutcomeMultiplayer(
            role: role,
            intentId: intent,
            serverId: serverId,
          ),
        ).toJson(),
      );
      write(
        'launch-progress',
        LaunchProgress(
          requestId: id,
          sequence: 2,
          phase: 'idle',
          session: LaunchProgressSession(
            role: role,
            intentId: intent,
            serverId: serverId,
            state: role == LaunchSessionRole.host
                ? LaunchSessionState.hosting
                : LaunchSessionState.inSession,
          ),
        ).toJson(),
      );
      var stopped = 0;
      var alive = fault != 'dead';
      final session = OwnedAcceptanceSession(
        context: context,
        process: process,
        expectedSession: request,
        acknowledgement: () =>
            store.readAcceptanceAcknowledgement(binding, process),
        outcome: () => store.readOutcome(profile.requestId, session: false),
        progress: () => store.readProgress(profile.requestId),
        processLiveness: (original) async {
          expect(identical(original, process), isTrue);
          return alive;
        },
        stopAndConfirmExit: (original) async {
          expect(identical(original, process), isTrue);
          stopped++;
        },
        dispose: () async {},
        clock: () => now,
        delay: (duration) async {
          now = now.add(duration);
        },
      );
      try {
        if (fault == 'none') {
          await session.confirm(const Duration(seconds: 1));
          alive = false;
          await expectLater(session.verifyAlive(), throwsStateError);
        } else {
          await expectLater(
            session.confirm(const Duration(seconds: 1)),
            throwsA(anyOf(isA<StateError>(), isA<FormatException>())),
          );
        }
      } finally {
        await session.close();
        expect(stopped, 1);
      }
    });
  }
}
