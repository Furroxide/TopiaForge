import 'dart:convert';
import 'dart:io';
import 'package:crypto/crypto.dart';
import 'package:launcher_data/launcher_data.dart';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/live_acceptance_isolation_verifier.dart';
import 'live_acceptance_test_fixture.dart';

void main() {
  late AcceptanceFixture f;
  late File evidence;
  late File ackFile;
  late String record;
  setUp(() async {
    f = AcceptanceFixture();
    await f
        .runner(
          commandRunner: (args) async {
            if (args.first == 'launch') f.writePassingRun();
            return 0;
          },
        )
        .run(f.options());
    final context = f.admitIsolation(f.options());
    record = context.recordPath;
    evidence = File(p.join(f.output.path, 'acceptance-result.json'));
    final raw = jsonDecode(evidence.readAsStringSync()) as Map<String, Object?>;
    final profile = ProfileLaunchConfigurationV4(
      profileId: 'qa',
      profileRevision: 1,
      requestId: 'owned-verifier',
      command: 'main-menu',
      safeMode: false,
      inheritManagerModState: false,
      enabledMods: const [],
      selectedVersions: const {},
      packages: const [],
    );
    final store = LaunchStagingStore(context.gameRoot);
    await store.writeRequest(profile);
    final request = await store.writeAcceptanceRequest(
      context: context,
      profile: profile,
      challenge: raw['acceptanceChallenge']! as String,
      now: DateTime.now().toUtc(),
    );
    final ack = <String, Object?>{
      'schemaVersion': 1,
      'requestId': profile.requestId,
      'challenge': raw['acceptanceChallenge'],
      'requestSha256': request.sha256Digest,
      'status': 'admitted',
      'process': {
        'pid': 4242,
        'nativeStartToken': 'windows:1234',
        'executablePath': p.join(context.gameRoot, 'Game.exe'),
      },
      'observedOsIdentity': context.identity.toJson(),
      'observedRoots': context.runtimeRoots(),
      'reasons': <Object?>[],
    };
    ackFile = File(
      p.join(
        context.managerRoot,
        'staging',
        'acceptance-isolation-ack-${LaunchStorageKeys.request(profile.requestId)}.json',
      ),
    );
    ackFile.writeAsStringSync(jsonEncode(ack));
    final issued = DateTime.parse(request.document['issuedAtUtc']! as String);
    raw['startedAtUtc'] = issued
        .subtract(const Duration(seconds: 1))
        .toIso8601String();
    raw['completedAtUtc'] = issued
        .add(const Duration(seconds: 1))
        .toIso8601String();
    raw['isolation'] = {
      'kind': context.kind,
      'provisioningRecordSha256': context.recordSha256,
      'acknowledgementSha256': sha256
          .convert(ackFile.readAsBytesSync())
          .toString(),
      'acknowledgement': ack,
      'processExitConfirmed': true,
    };
    evidence.writeAsStringSync(jsonEncode(raw));
    await store.deleteRequest(profile);
  });
  tearDown(() => f.dispose());
  Future<Map<String, Object?>> verify() => verifyLiveAcceptanceIsolation(
    evidencePath: evidence.path,
    isolationRecordPath: record,
  );
  test(
    'replays exact private record request ACK chain after V4 consumption without native launch',
    () async {
      final result = await verify();
      expect(result.keys.toSet(), {
        'schemaVersion',
        'status',
        'gameDirectory',
        'managerRoot',
        'provisioningRecordSha256',
        'acknowledgementSha256',
      });
      expect(result['schemaVersion'], 1);
      expect(result['status'], 'admitted');
      expect(result['gameDirectory'], f.game.path);
      expect(
        result['acknowledgementSha256'],
        sha256.convert(ackFile.readAsBytesSync()).toString(),
      );
    },
  );
  for (final mutation in <String, void Function(Map<String, Object?>)>{
    'disjoint earlier interval': (v) {
      v['startedAtUtc'] = '2020-01-01T00:00:00Z';
      v['completedAtUtc'] = '2020-01-01T00:01:00Z';
    },
    'unknown critical receipt field': (v) =>
        (((v['acceptancePackageReceipt']! as Map)['criticalFiles']! as List)
                    .first
                as Map)['trusted'] =
            true,
    'old schema': (v) => v['schemaVersion'] = 2,
    'unknown top-level field': (v) => v['trusted'] = true,
    'unconfirmed exit': (v) =>
        (v['isolation']! as Map)['processExitConfirmed'] = false,
    'mismatched record hash': (v) =>
        (v['isolation']! as Map)['provisioningRecordSha256'] = 'f' * 64,
    'mismatched ACK hash': (v) =>
        (v['isolation']! as Map)['acknowledgementSha256'] = 'f' * 64,
    'mismatched challenge': (v) => v['acceptanceChallenge'] = 'f' * 64,
    'normal game root': (v) => v['gameDirectory'] = f.sourceGame.path,
    'changed embedded ACK': (v) =>
        ((v['isolation']! as Map)['acknowledgement']! as Map)['status'] =
            'rejected',
    'scalar success': (v) => v['succeeded'] = 'true',
  }.entries) {
    test('refuses ${mutation.key}', () async {
      final raw =
          jsonDecode(evidence.readAsStringSync()) as Map<String, Object?>;
      mutation.value(raw);
      evidence.writeAsStringSync(jsonEncode(raw));
      await expectLater(verify(), throwsA(isA<FormatException>()));
    });
  }
  test(
    'actual ACK bytes cannot be replaced with semantically equivalent JSON',
    () async {
      ackFile.writeAsStringSync(' \n${ackFile.readAsStringSync()}');
      await expectLater(verify(), throwsA(isA<FormatException>()));
    },
  );
}
