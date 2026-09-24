import 'package:test/test.dart';
import 'package:topiaforge/src/sandbox_acceptance/native_annex_verifier.dart';
import 'package:topiaforge/src/sandbox_acceptance/sandbox_specification.dart';
import 'sandbox_native_fixture.dart';
import 'sandbox_native_verification_fixture.dart';

void main() {
  test('recomputed fixture passes supplementary checks but never release', () {
    final result = NativeVerificationFixture().verify();
    expect(result.allNativeChecksPassed, true);
    expect(result.qualifiesRelease, false);
    expect(result.toJson()['qualifiesRelease'], false);
    expect(result.toJson()['remainingRequirements'], isNotEmpty);
  });
  final mutations = <String, void Function(NativeVerificationFixture)>{
    'source workspace': (f) => f.overrides['source'] = [1, 2, 3],
    'source': (f) => f.overrides['sourceRevision'] = 'a' * 40,
    'challenge': (f) => f.overrides['challenge'] = 'b' * 64,
    'spec digest': (f) => f.value['specSha256'] = 'a' * 64,
    'driver bytes': (f) => f.overrides['driver'] = [1, 2, 3],
    'device bytes': (f) => f.overrides['device'] = [1, 2, 3],
    'record bytes': (f) => f.overrides['record'] = [1, 2, 3],
    'ack bytes': (f) => f.overrides['ack'] = [1, 2, 3],
    'request bytes': (f) => f.overrides['request'] = [1, 2, 3],
    'profile bytes': (f) => f.overrides['profile'] = [1, 2, 3],
    'transcript bytes': (f) => f.overrides['transcript'] = [1, 2, 3],
    'empty transcript': (f) => f.overrides['transcript'] = <int>[],
    'oversized transcript': (f) =>
        f.overrides['transcript'] = List<int>.filled(128 * 1024 * 1024 + 1, 32),
    'broker binary': (f) => f.overrides['brokerDigest'] = 'a' * 64,
    'broker PID': (f) => f.overrides['brokerPid'] = 42,
    'package bytes': (f) => f.overrides['packages'] = <Map<String, Object?>>[],
    'missing artifact': (f) =>
        f.overrides['artifacts'] = <String, SandboxNativeArtifactFact>{},
    'artifact length': (f) => f.overrides['artifacts'] = {
      'screens/before.png': SandboxNativeArtifactFact(
        sha256Digest: '7' * 64,
        length: 33,
      ),
    },
    'artifact digest': (f) => f.overrides['artifacts'] = {
      'screens/before.png': SandboxNativeArtifactFact(
        sha256Digest: '8' * 64,
        length: 32,
      ),
    },
    'PID reuse start': (f) => f.overrides['process'] = {
      ...nativeMap(f.acknowledgement['process']),
      'nativeStartToken': 'windows:2222',
    },
    'process executable': (f) => f.overrides['process'] = {
      ...nativeMap(f.acknowledgement['process']),
      'executablePath': r'D:\other\Robotopia.exe',
    },
    'request unknown field': (f) {
      f.request['extra'] = true;
      f.rebindIsolation();
    },
    'request challenge': (f) {
      f.request['challenge'] = '0' * 64;
      f.rebindIsolation();
    },
    'request ID': (f) {
      f.request['requestId'] = 'old-request';
      f.rebindIsolation();
    },
    'request before run': (f) {
      f.request['issuedAtUtc'] = '2026-09-09T09:59:59Z';
      f.rebindIsolation();
    },
    'request after run': (f) {
      f.request['issuedAtUtc'] = '2026-09-09T10:01:01Z';
      f.rebindIsolation();
    },
    'request overlong lifetime': (f) {
      f.request['expiresAtUtc'] = '2026-09-09T10:16:01Z';
      f.rebindIsolation();
    },
    'expired before issued': (f) {
      f.request['expiresAtUtc'] = f.request['issuedAtUtc'];
      f.rebindIsolation();
    },
    'normal user masquerade': (f) {
      f.record['normalUserSid'] = f.record['userSid'];
      f.rebindIsolation();
    },
    'wrong provisioned game': (f) {
      f.record['gameRoot'] = r'D:\other';
      f.rebindIsolation();
    },
    'wrong provisioned SID': (f) {
      f.record['userSid'] = 'S-1-5-21-123-9999';
      f.rebindIsolation();
    },
    'blank review': (f) {
      f.record['reviewerEvidence'] = '';
      f.rebindIsolation();
    },
  };
  for (final entry in mutations.entries) {
    test('binding rejects ${entry.key}', () {
      final fixture = NativeVerificationFixture();
      entry.value(fixture);
      expect(fixture.verify, throwsStateError);
    });
  }
  for (final key in [
    'inputReleased',
    'fixtureReleased',
    'originalProcessExitConfirmed',
  ]) {
    test('unconfirmed $key cannot pass', () {
      final f = NativeVerificationFixture();
      nativeMap(f.value['cleanup'])[key] = false;
      final result = f.verify();
      expect(result.allNativeChecksPassed, false);
      expect(result.status, 'failed');
    });
  }
  test('isolation exit cannot disagree', () {
    final f = NativeVerificationFixture();
    f.isolation['processExitConfirmed'] = false;
    expect(f.verify().status, 'failed');
  });
  test('raw failure survives driver green claims', () {
    final f = NativeVerificationFixture();
    f.value['failures'] = ['first native failure'];
    expect(f.verify().failures, contains('first native failure'));
    expect(f.verify().allNativeChecksPassed, false);
  });
  for (final id in sandboxScenarioIds) {
    test('claim cannot replace missing $id observations', () {
      final f = NativeVerificationFixture();
      final result = f.verify(
        results: f.oracles.where((r) => r.scenarioId != id).toList(),
      );
      expect(result.allNativeChecksPassed, false);
      expect(
        result.results.firstWhere((r) => r.scenarioId == id).status,
        SandboxNativeStatus.missing,
      );
    });
    test('claim cannot replace failed $id oracle', () {
      final f = NativeVerificationFixture();
      final results = f.oracles
          .map(
            (r) => r.scenarioId != id
                ? r
                : SandboxNativeOracleResult(
                    scenarioId: id,
                    status: SandboxNativeStatus.failed,
                    reason: 'Measured failure.',
                    completedSamples: r.completedSamples,
                  ),
          )
          .toList();
      expect(f.verify(results: results).status, 'failed');
    });
  }
  for (var count = 0; count < 10; count++) {
    test('ten cycles cannot pass with $count samples', () {
      final f = NativeVerificationFixture();
      final results = f.oracles..removeLast();
      results.add(
        SandboxNativeOracleResult(
          scenarioId: 'ten-cycles',
          status: SandboxNativeStatus.passed,
          reason: '',
          completedSamples: count,
        ),
      );
      expect(() => f.verify(results: results), throwsStateError);
    });
  }
  test('unsupported cases stay incomplete', () {
    final f = NativeVerificationFixture();
    for (final row in nativeList(f.value['scenarioResults'])) {
      nativeMap(row)['status'] = 'unavailable';
      nativeMap(row)['reason'] = 'Actual unsupported capability.';
    }
    final results = [
      for (final id in sandboxScenarioIds)
        SandboxNativeOracleResult(
          scenarioId: id,
          status: SandboxNativeStatus.unavailable,
          reason: 'Actual unsupported capability.',
          completedSamples: 0,
        ),
    ];
    final result = f.verify(results: results);
    expect(result.status, 'incomplete');
    expect(result.allNativeChecksPassed, false);
  });
  test('duplicate independent oracle refused', () {
    final f = NativeVerificationFixture();
    expect(
      () => f.verify(results: f.oracles..add(f.oracles.first)),
      throwsStateError,
    );
  });
  test('binding bytes and maps are immutable snapshots', () {
    final f = NativeVerificationFixture();
    final bindings = f.bindings;
    expect(() => bindings.driverManifestBytes[0] = 0, throwsUnsupportedError);
    expect(
      () => bindings.packages.first['id'] = 'changed',
      throwsUnsupportedError,
    );
    expect(
      () => bindings.originalProcessIdentity['pid'] = 0,
      throwsUnsupportedError,
    );
    expect(() => bindings.artifactDigests.clear(), throwsUnsupportedError);
  });
}
