import 'package:crypto/crypto.dart';
import 'native_annex.dart';
import 'sandbox_json.dart';
import 'sandbox_specification.dart';

enum SandboxNativeStatus { passed, failed, unavailable, missing }

/// Independently recomputed facts supplied by the transcript evaluator.
/// The CLI must construct these from verified bytes, never scenarioResults.
final class SandboxNativeOracleResult {
  const SandboxNativeOracleResult({
    required this.scenarioId,
    required this.status,
    required this.reason,
    required this.completedSamples,
  });
  final String scenarioId, reason;
  final SandboxNativeStatus status;
  final int completedSamples;
}

final class SandboxNativeArtifactFact {
  const SandboxNativeArtifactFact({
    required this.sha256Digest,
    required this.length,
  });
  final String sha256Digest;
  final int length;
}

/// Actual private inputs read by the physical-path/owned-process verifier.
/// Parsing this object does not admit a live host or authenticate a human.
final class SandboxNativeBindings {
  SandboxNativeBindings({
    required this.sourceRevision,
    required this.challenge,
    required List<int> sourceWorkspaceBytes,
    required List<int> driverManifestBytes,
    required List<int> deviceProfileBytes,
    required List<int> provisioningRecordBytes,
    required List<int> acknowledgementBytes,
    required List<int> requestBytes,
    required List<int> profileRequestBytes,
    required List<int> transcriptBytes,
    required this.brokerBinarySha256,
    required this.brokerProcessId,
    required List<Map<String, Object?>> packages,
    required Map<String, SandboxNativeArtifactFact> artifactDigests,
    required Map<String, Object?> originalProcessIdentity,
  }) : sourceWorkspaceBytes = List.unmodifiable(sourceWorkspaceBytes),
       driverManifestBytes = List.unmodifiable(driverManifestBytes),
       deviceProfileBytes = List.unmodifiable(deviceProfileBytes),
       provisioningRecordBytes = List.unmodifiable(provisioningRecordBytes),
       acknowledgementBytes = List.unmodifiable(acknowledgementBytes),
       requestBytes = List.unmodifiable(requestBytes),
       profileRequestBytes = List.unmodifiable(profileRequestBytes),
       transcriptBytes = List.unmodifiable(transcriptBytes),
       packages = (nativeFreeze(packages)! as List)
           .cast<Map<String, Object?>>(),
       artifactDigests = Map.unmodifiable(artifactDigests),
       originalProcessIdentity =
           nativeFreeze(originalProcessIdentity)! as Map<String, Object?>;
  final String sourceRevision, challenge, brokerBinarySha256;
  final List<int> sourceWorkspaceBytes;
  final int brokerProcessId;
  final List<int> driverManifestBytes,
      deviceProfileBytes,
      provisioningRecordBytes;
  final List<int> acknowledgementBytes,
      requestBytes,
      profileRequestBytes,
      transcriptBytes;
  final List<Map<String, Object?>> packages;
  final Map<String, SandboxNativeArtifactFact> artifactDigests;
  final Map<String, Object?> originalProcessIdentity;
}

final class SandboxNativeVerification {
  SandboxNativeVerification._(
    this.runId,
    Iterable<SandboxNativeOracleResult> results,
    Iterable<String> failures,
  ) : results = List.unmodifiable(results),
      failures = List.unmodifiable(failures);
  final String runId;
  final List<SandboxNativeOracleResult> results;
  final List<String> failures;
  bool get qualifiesRelease => false;
  bool get allNativeChecksPassed =>
      failures.isEmpty &&
      results.every((r) => r.status == SandboxNativeStatus.passed);
  String get status =>
      failures.isNotEmpty ||
          results.any((r) => r.status == SandboxNativeStatus.failed)
      ? 'failed'
      : allNativeChecksPassed
      ? 'passed'
      : 'incomplete';
  Map<String, Object?> toJson() => {
    'scope': 'supplementary-native-development',
    'runId': runId,
    'status': status,
    'allNativeChecksPassed': allNativeChecksPassed,
    'qualifiesRelease': false,
    'results': [
      for (final result in results)
        {
          'scenarioId': result.scenarioId,
          'status': result.status.name,
          'reason': result.reason,
          'completedSamples': result.completedSamples,
        },
    ],
    'failures': failures,
    'remainingRequirements': [
      'Human review of visual/accessibility baselines and physical audio quality.',
      'Successful global persistence mutation is not implemented.',
      'Native vehicle and genuine remote-session branches require separate support and evidence.',
      'Existing SDK, game, gamemode and Unity authoring release cases remain mandatory.',
    ],
  };
}

final class SandboxNativeAnnexVerifier {
  SandboxNativeVerification verify(
    SandboxNativeAnnex annex,
    SandboxSpecification spec,
    SandboxNativeBindings bindings,
    Iterable<SandboxNativeOracleResult> oracles,
  ) {
    final value = annex.document;
    nativeHex(bindings.sourceRevision, 40);
    nativeHex(bindings.challenge, 64);
    if (annex.sourceRevision != bindings.sourceRevision ||
        annex.challenge != bindings.challenge ||
        value['sourceWorkspaceSha256'] !=
            _digest(bindings.sourceWorkspaceBytes) ||
        value['specSha256'] != spec.sha256Digest ||
        value['driverManifestSha256'] !=
            _digest(bindings.driverManifestBytes) ||
        value['deviceProfileSha256'] != _digest(bindings.deviceProfileBytes) ||
        !nativeSame(annex.packages, bindings.packages)) {
      throw StateError(
        'Native source/spec/driver/device/package byte binding failed.',
      );
    }
    final broker = sandboxObject(value['broker'], 'broker');
    if (broker['binarySha256'] != bindings.brokerBinarySha256 ||
        broker['processId'] != bindings.brokerProcessId) {
      throw StateError('Native broker identity differs.');
    }
    final transcript = sandboxObject(value['transcript'], 'transcript');
    if (bindings.transcriptBytes.isEmpty ||
        bindings.transcriptBytes.length > 128 * 1024 * 1024 ||
        transcript['sha256'] != _digest(bindings.transcriptBytes)) {
      throw StateError('Native transcript bytes differ or exceed bounds.');
    }
    if (bindings.artifactDigests.length != annex.artifacts.length) {
      throw StateError('Native artifact inventory differs.');
    }
    for (final artifact in annex.artifacts) {
      final actual = bindings.artifactDigests[artifact['path']];
      if (actual == null ||
          actual.sha256Digest != artifact['sha256'] ||
          actual.length != artifact['length']) {
        throw StateError('Native artifact bytes differ.');
      }
    }
    _verifyIsolation(annex, bindings);
    final actualById = <String, SandboxNativeOracleResult>{};
    for (final result in oracles) {
      if (!sandboxScenarioIds.contains(result.scenarioId) ||
          actualById.containsKey(result.scenarioId)) {
        throw StateError(
          'Independent native oracle IDs are unknown or repeated.',
        );
      }
      final count = result.scenarioId == 'ten-cycles' ? 10 : 1;
      if (result.completedSamples < 0 ||
          result.completedSamples > count ||
          (result.status == SandboxNativeStatus.passed &&
              result.completedSamples != count)) {
        throw StateError(
          'Native oracle did not observe every required sample.',
        );
      }
      nativeReason(
        result.reason,
        empty: result.status == SandboxNativeStatus.passed,
      );
      actualById[result.scenarioId] = result;
    }
    final claimed = {
      for (final row in annex.scenarioResults) row['scenarioId']: row,
    };
    final failures = <String>[
      ...nativeReasons(value['failures']),
      if (annex.isolation['processExitConfirmed'] != true ||
          annex.cleanup.values.any((v) => v != true))
        'Input release, fixture cleanup or original owned-process exit is unconfirmed.',
    ];
    final results = <SandboxNativeOracleResult>[];
    for (final id in sandboxScenarioIds) {
      final actual =
          actualById[id] ??
          SandboxNativeOracleResult(
            scenarioId: id,
            status: SandboxNativeStatus.missing,
            reason: 'No independent native observations supplied.',
            completedSamples: 0,
          );
      results.add(actual);
      final claim = claimed[id];
      if (claim != null && claim['status'] != actual.status.name) {
        failures.add(
          'Native driver claim differs from independent oracle for $id.',
        );
      }
      if (claim == null && actual.status == SandboxNativeStatus.passed) {
        failures.add(
          'Native annex omits an independently observed scenario: $id.',
        );
      }
    }
    return SandboxNativeVerification._(annex.runId, results, failures);
  }
}

String _digest(List<int> bytes) => sha256.convert(bytes).toString();

void _verifyIsolation(
  SandboxNativeAnnex annex,
  SandboxNativeBindings bindings,
) {
  final isolation = annex.isolation;
  if (isolation['provisioningRecordSha256'] !=
          _digest(bindings.provisioningRecordBytes) ||
      isolation['acknowledgementSha256'] !=
          _digest(bindings.acknowledgementBytes)) {
    throw StateError('Native private isolation/acknowledgement bytes differ.');
  }
  final ack = sandboxDocument(
    bindings.acknowledgementBytes,
    'actual acknowledgement',
  );
  if (!nativeSame(ack, isolation['acknowledgement']) ||
      !nativeSame(ack['process'], bindings.originalProcessIdentity)) {
    throw StateError(
      'Native embedded acknowledgement or original process differs.',
    );
  }
  final request = sandboxDocument(
    bindings.requestBytes,
    'actual isolation request',
  );
  sandboxFields(request, {
    'schemaVersion',
    'requestId',
    'challenge',
    'profileRequestSha256',
    'isolationEvidenceSha256',
    'issuedAtUtc',
    'expiresAtUtc',
    'expectedOsIdentity',
    'expectedRoots',
  }, 'actual isolation request');
  if (request['schemaVersion'] is! int ||
      request['schemaVersion'] != 1 ||
      request['requestId'] != ack['requestId'] ||
      request['challenge'] != bindings.challenge ||
      request['isolationEvidenceSha256'] !=
          isolation['provisioningRecordSha256'] ||
      request['profileRequestSha256'] !=
          _digest(bindings.profileRequestBytes) ||
      ack['requestSha256'] != _digest(bindings.requestBytes) ||
      !nativeSame(request['expectedOsIdentity'], ack['observedOsIdentity']) ||
      !nativeSame(request['expectedRoots'], ack['observedRoots'])) {
    throw StateError(
      'Native request/profile/identity/root correlation differs.',
    );
  }
  final issued = nativeUtc(request['issuedAtUtc']);
  final expires = nativeUtc(request['expiresAtUtc']);
  if (issued.isBefore(nativeUtc(annex.document['startedAtUtc'])) ||
      issued.isAfter(nativeUtc(annex.document['completedAtUtc'])) ||
      !expires.isAfter(issued) ||
      expires.difference(issued) > const Duration(minutes: 15)) {
    throw StateError('Native acknowledgement request lifetime differs.');
  }
  final record = sandboxDocument(
    bindings.provisioningRecordBytes,
    'actual provisioning',
  );
  sandboxFields(record, {
    'schemaVersion',
    'kind',
    'sourceGameRoot',
    'gameRoot',
    'launcherRoot',
    'outputRoot',
    'persistentDataRoot',
    'userSid',
    'userProfile',
    'localAppDataLow',
    'normalUserSid',
    'normalUserProfile',
    'reviewerEvidence',
  }, 'actual provisioning');
  final identity = sandboxObject(
    ack['observedOsIdentity'],
    'actual OS identity',
  );
  final roots = sandboxObject(ack['observedRoots'], 'actual roots');
  if (record['schemaVersion'] is! int ||
      record['schemaVersion'] != 1 ||
      record['kind'] != isolation['kind'] ||
      record['userSid'] != identity['userSid'] ||
      record['userSid'] == record['normalUserSid'] ||
      record['userProfile'] != identity['userProfile'] ||
      record['localAppDataLow'] != identity['localAppDataLow'] ||
      record['gameRoot'] != roots['gameRoot'] ||
      record['persistentDataRoot'] != roots['persistentDataRoot']) {
    throw StateError(
      'Native recorded provisioning differs from acknowledgement.',
    );
  }
  sandboxText(record['reviewerEvidence'], 'private provisioning review');
}
