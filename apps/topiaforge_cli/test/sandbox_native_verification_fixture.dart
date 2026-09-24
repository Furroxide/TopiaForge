import 'dart:io';
import 'package:topiaforge/src/sandbox_acceptance/native_annex.dart';
import 'package:topiaforge/src/sandbox_acceptance/native_annex_verifier.dart';
import 'package:topiaforge/src/sandbox_acceptance/sandbox_specification.dart';
import 'sandbox_native_fixture.dart';

/// Fabricated binding/oracle unit-test inputs. No actual native run occurred.
final class NativeVerificationFixture {
  NativeVerificationFixture() {
    value['specSha256'] = spec.sha256Digest;
    value['sourceWorkspaceSha256'] = nativeTestDigest(source);
    value['driverManifestSha256'] = nativeTestDigest(driver);
    value['deviceProfileSha256'] = nativeTestDigest(device);
    value['failures'] = [];
    nativeMap(value['cleanup']).updateAll((_, _) => true);
    isolation['processExitConfirmed'] = true;
    for (final row in nativeList(value['scenarioResults'])) {
      nativeMap(row)['status'] = 'passed';
      nativeMap(row)['reason'] = '';
    }
    record = {
      'schemaVersion': 1,
      'kind': 'windows-user',
      'sourceGameRoot': r'D:\source-game',
      'gameRoot': roots['gameRoot'],
      'launcherRoot': r'D:\QA\launcher',
      'outputRoot': r'D:\QA\output',
      'persistentDataRoot': roots['persistentDataRoot'],
      'userSid': identity['userSid'],
      'userProfile': identity['userProfile'],
      'localAppDataLow': identity['localAppDataLow'],
      'normalUserSid': 'S-1-5-21-123-1000',
      'normalUserProfile': r'C:\Users\test-normal',
      'reviewerEvidence': 'Test-only provisioning reference.',
    };
    request = {
      'schemaVersion': 1,
      'requestId': acknowledgement['requestId'],
      'challenge': value['challenge'],
      'profileRequestSha256': nativeTestDigest(profile),
      'isolationEvidenceSha256': nativeTestDigest(record),
      'issuedAtUtc': '2026-09-09T10:00:01.000Z',
      'expiresAtUtc': '2026-09-09T10:10:01.000Z',
      'expectedOsIdentity': identity,
      'expectedRoots': roots,
    };
    rebindIsolation();
    nativeMap(value['transcript'])['sha256'] = nativeTestDigest(transcript);
  }
  final value = nativeAnnexFixture();
  final spec = SandboxSpecification.parse(
    File('../../tests/sandbox-workbench-acceptance-v1.json').readAsBytesSync(),
  );
  final source = <String, Object?>{'test': 'source-workspace'};
  final driver = <String, Object?>{'test': 'driver'};
  final device = <String, Object?>{'test': 'device'};
  final profile = <String, Object?>{'test': 'profile'};
  final transcript = <String, Object?>{
    'test': 'transcript-evaluator-is-separate',
  };
  late final Map<String, Object?> record, request;
  Map<String, Object?> get isolation => nativeMap(value['isolation']);
  Map<String, Object?> get acknowledgement =>
      nativeMap(isolation['acknowledgement']);
  Map<String, Object?> get identity =>
      nativeMap(acknowledgement['observedOsIdentity']);
  Map<String, Object?> get roots => nativeMap(acknowledgement['observedRoots']);
  void rebindIsolation() {
    request['isolationEvidenceSha256'] = nativeTestDigest(record);
    isolation['provisioningRecordSha256'] = nativeTestDigest(record);
    acknowledgement['requestSha256'] = nativeTestDigest(request);
    isolation['acknowledgementSha256'] = nativeTestDigest(acknowledgement);
  }

  final overrides = <String, Object?>{};
  SandboxNativeBindings get bindings => SandboxNativeBindings(
    sourceRevision:
        overrides['sourceRevision'] as String? ??
        value['sourceRevision']! as String,
    challenge:
        overrides['challenge'] as String? ?? value['challenge']! as String,
    sourceWorkspaceBytes:
        overrides['source'] as List<int>? ?? nativeTestBytes(source),
    driverManifestBytes:
        overrides['driver'] as List<int>? ?? nativeTestBytes(driver),
    deviceProfileBytes:
        overrides['device'] as List<int>? ?? nativeTestBytes(device),
    provisioningRecordBytes:
        overrides['record'] as List<int>? ?? nativeTestBytes(record),
    acknowledgementBytes:
        overrides['ack'] as List<int>? ?? nativeTestBytes(acknowledgement),
    requestBytes:
        overrides['request'] as List<int>? ?? nativeTestBytes(request),
    profileRequestBytes:
        overrides['profile'] as List<int>? ?? nativeTestBytes(profile),
    transcriptBytes:
        overrides['transcript'] as List<int>? ?? nativeTestBytes(transcript),
    brokerBinarySha256:
        overrides['brokerDigest'] as String? ??
        nativeMap(value['broker'])['binarySha256']! as String,
    brokerProcessId:
        overrides['brokerPid'] as int? ??
        nativeMap(value['broker'])['processId']! as int,
    packages:
        overrides['packages'] as List<Map<String, Object?>>? ??
        nativeList(value['packages']).map(nativeMap).toList(),
    artifactDigests:
        overrides['artifacts'] as Map<String, SandboxNativeArtifactFact>? ??
        {
          for (final row in nativeList(value['artifacts']))
            nativeMap(row)['path']! as String: SandboxNativeArtifactFact(
              sha256Digest: nativeMap(row)['sha256']! as String,
              length: nativeMap(row)['length']! as int,
            ),
        },
    originalProcessIdentity:
        overrides['process'] as Map<String, Object?>? ??
        nativeMap(acknowledgement['process']),
  );
  List<SandboxNativeOracleResult> get oracles => [
    for (final id in sandboxScenarioIds)
      SandboxNativeOracleResult(
        scenarioId: id,
        status: SandboxNativeStatus.passed,
        reason: '',
        completedSamples: id == 'ten-cycles' ? 10 : 1,
      ),
  ];
  SandboxNativeVerification verify({
    List<SandboxNativeOracleResult>? results,
  }) => SandboxNativeAnnexVerifier().verify(
    SandboxNativeAnnex.parse(nativeTestBytes(value)),
    spec,
    bindings,
    results ?? oracles,
  );
}
