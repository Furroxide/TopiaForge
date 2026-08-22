import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:json_schema/json_schema.dart';

const releaseReadinessPath = 'release/release-readiness.json';
const releaseReadinessSchemaPath =
    'schemas/topiaforge.release-readiness-v1.schema.json';

final class ReleaseReadinessGateDecision {
  ReleaseReadinessGateDecision({
    required this.id,
    required this.priority,
    required this.enforcement,
    required this.status,
    required this.reviewerRoles,
    required this.evidenceIds,
    this.reasonCode,
    this.acceptedRiskScope,
    this.acceptedRiskEvidenceId,
  });

  final String id;
  final String priority;
  final String enforcement;
  final String status;
  final List<String> reviewerRoles;
  final List<String> evidenceIds;
  final String? reasonCode;
  final String? acceptedRiskScope;
  final String? acceptedRiskEvidenceId;

  /// Whether the gate's own exit criteria are met.
  bool get isSatisfied =>
      status == 'approved' || (priority == 'P1' && status == 'accepted-risk');

  /// Whether an unmet gate stops the release.
  ///
  /// A `0.x` line keeps the whole register visible but only lets the gates
  /// declared `blocking` in [_gateContracts] hold a candidate. An `advisory`
  /// gate is still recorded, still reported, and still reaches `ready` only by
  /// approval — it just does not by itself make the computed status `blocked`.
  bool get blocksRelease => enforcement == 'blocking' && !isSatisfied;

  Map<String, Object?> toPublicSummary() => {
    'id': id,
    'priority': priority,
    'enforcement': enforcement,
    'status': status,
    if (reasonCode != null) 'reasonCode': reasonCode,
    'reviewerRoles': reviewerRoles,
    'evidenceIds': evidenceIds,
    if (acceptedRiskScope != null)
      'acceptedRisk': {
        'scope': acceptedRiskScope,
        'decisionEvidenceId': acceptedRiskEvidenceId,
      },
  };
}

final class ReleaseReadinessDecision {
  ReleaseReadinessDecision._({
    required this.targetSha,
    required this.releaseVersion,
    required this.status,
    required this.readinessBlobSha256,
    required this.gates,
  });

  static const _maximumReadinessBytes = 128 * 1024;
  static const _maximumSchemaBytes = 256 * 1024;
  static final _shaPattern = RegExp(r'^[0-9a-f]{40}$');

  final String targetSha;
  final String releaseVersion;
  final String status;
  final String readinessBlobSha256;
  final List<ReleaseReadinessGateDecision> gates;

  bool get isReady => status == 'ready';

  static Future<ReleaseReadinessDecision> loadAtGitSha({
    required String repositoryRoot,
    required String targetSha,
    required String expectedReleaseVersion,
  }) async {
    _checkTargetSha(targetSha);
    final resolved = await Process.run(
      'git',
      ['-C', repositoryRoot, 'rev-parse', '--verify', '$targetSha^{commit}'],
      stdoutEncoding: utf8,
      stderrEncoding: utf8,
    );
    if (resolved.exitCode != 0 ||
        (resolved.stdout as String).trim() != targetSha) {
      throw StateError('The release target is not the exact requested commit.');
    }
    final readinessBytes = await _readGitBlob(
      repositoryRoot,
      targetSha,
      releaseReadinessPath,
      _maximumReadinessBytes,
    );
    final schemaBytes = await _readGitBlob(
      repositoryRoot,
      targetSha,
      releaseReadinessSchemaPath,
      _maximumSchemaBytes,
    );
    return fromCandidateBlobs(
      readinessBytes: readinessBytes,
      schemaBytes: schemaBytes,
      targetSha: targetSha,
      expectedReleaseVersion: expectedReleaseVersion,
    );
  }

  static ReleaseReadinessDecision fromCandidateBlobs({
    required List<int> readinessBytes,
    required List<int> schemaBytes,
    required String targetSha,
    required String expectedReleaseVersion,
  }) {
    _checkTargetSha(targetSha);
    final readiness = _decodeObject(
      readinessBytes,
      maximumBytes: _maximumReadinessBytes,
      label: 'Release readiness',
    );
    final schemaJson = _decodeObject(
      schemaBytes,
      maximumBytes: _maximumSchemaBytes,
      label: 'Release readiness schema',
    );
    final schemaResult = JsonSchema.create(schemaJson).validate(readiness);
    if (!schemaResult.isValid) {
      throw StateError(
        'Release readiness is schema-invalid:\n'
        '${schemaResult.errors.join('\n')}',
      );
    }
    if (readiness['releaseVersion'] != expectedReleaseVersion) {
      throw StateError('Release readiness is not for $expectedReleaseVersion.');
    }

    final rawGates = readiness['gates']! as List;
    if (rawGates.length != _gateContracts.length) {
      // The schema pins the gate count and this list pins each gate's identity.
      // If they ever disagree, positional lookup below would either crash with a
      // RangeError or silently drop a contract, so refuse the decision instead.
      throw StateError(
        'Release readiness declares ${rawGates.length} gates but the release '
        'contract defines ${_gateContracts.length}.',
      );
    }
    final gates = <ReleaseReadinessGateDecision>[];
    final evidenceIds = <String>{};
    for (var index = 0; index < rawGates.length; index++) {
      final raw = Map<String, Object?>.from(rawGates[index]! as Map);
      final contract = _gateContracts[index];
      final id = raw['id']! as String;
      final priority = raw['priority']! as String;
      final enforcement = raw['enforcement']! as String;
      final status = raw['status']! as String;
      final roles = List<String>.from(raw['reviewerRoles']! as List);
      final gateEvidence = List<String>.from(raw['evidenceIds']! as List);
      if (id != contract.id ||
          priority != contract.priority ||
          enforcement != contract.enforcement ||
          !_sameList(roles, contract.reviewerRoles)) {
        throw StateError(
          'Release readiness gate ${contract.id} has the wrong identity '
          'or reviewer-role contract.',
        );
      }
      if (!_isStrictlySorted(gateEvidence) ||
          gateEvidence.any((value) => !value.startsWith('EVID-$id-')) ||
          gateEvidence.any((value) => !evidenceIds.add(value))) {
        throw StateError(
          'Release readiness gate $id has invalid or duplicate evidence IDs.',
        );
      }

      final reasonCode = raw['reasonCode'] as String?;
      final acceptedRisk = raw['acceptedRisk'] == null
          ? null
          : Map<String, Object?>.from(raw['acceptedRisk']! as Map);
      final riskScope = acceptedRisk?['scope'] as String?;
      final riskEvidenceId = acceptedRisk?['decisionEvidenceId'] as String?;
      if (status == 'blocked' && reasonCode != contract.blockedReasonCode) {
        throw StateError(
          'Release readiness gate $id has the wrong blocked reason.',
        );
      }
      if (status == 'accepted-risk' &&
          (priority != 'P1' ||
              riskScope != contract.acceptedRiskScope ||
              !gateEvidence.contains(riskEvidenceId))) {
        throw StateError(
          'Release readiness gate $id has an invalid accepted-risk scope.',
        );
      }
      gates.add(
        ReleaseReadinessGateDecision(
          id: id,
          priority: priority,
          enforcement: enforcement,
          status: status,
          reasonCode: reasonCode,
          reviewerRoles: List.unmodifiable(roles),
          evidenceIds: List.unmodifiable(gateEvidence),
          acceptedRiskScope: riskScope,
          acceptedRiskEvidenceId: riskEvidenceId,
        ),
      );
    }

    final computedStatus = gates.any((gate) => gate.blocksRelease)
        ? 'blocked'
        : 'ready';
    if (readiness['status'] != computedStatus) {
      throw StateError(
        'Release readiness status does not match its exact gate decisions.',
      );
    }
    return ReleaseReadinessDecision._(
      targetSha: targetSha,
      releaseVersion: expectedReleaseVersion,
      status: computedStatus,
      readinessBlobSha256: sha256.convert(readinessBytes).toString(),
      gates: List.unmodifiable(gates),
    );
  }

  Map<String, Object?> toPublicSummary() => {
    'schema': 'topiaforge-release-readiness-summary-v1',
    'releaseVersion': releaseVersion,
    'targetSha': targetSha,
    'readinessBlobSha256': readinessBlobSha256,
    'status': status,
    'gates': gates.map((gate) => gate.toPublicSummary()).toList(),
  };

  static Future<List<int>> _readGitBlob(
    String repositoryRoot,
    String targetSha,
    String path,
    int maximumBytes,
  ) async {
    final result = await Process.run(
      'git',
      ['-C', repositoryRoot, 'cat-file', 'blob', '$targetSha:$path'],
      stdoutEncoding: null,
      stderrEncoding: utf8,
    );
    if (result.exitCode != 0 || result.stdout is! List<int>) {
      throw StateError(
        'Required release decision is not tracked at the target commit: $path.',
      );
    }
    final bytes = result.stdout as List<int>;
    if (bytes.isEmpty || bytes.length > maximumBytes) {
      throw StateError('Tracked release decision has an invalid size: $path.');
    }
    return bytes;
  }

  static Map<String, Object?> _decodeObject(
    List<int> bytes, {
    required int maximumBytes,
    required String label,
  }) {
    if (bytes.isEmpty || bytes.length > maximumBytes) {
      throw StateError('$label has an invalid size.');
    }
    Object? value;
    try {
      value = jsonDecode(utf8.decode(bytes, allowMalformed: false));
    } on FormatException catch (error) {
      throw StateError('$label is not strict UTF-8 JSON: $error');
    }
    if (value is! Map) {
      throw StateError('$label must contain one JSON object.');
    }
    return Map<String, Object?>.from(value);
  }

  static void _checkTargetSha(String targetSha) {
    if (!_shaPattern.hasMatch(targetSha)) {
      throw StateError(
        'Release readiness requires an exact lowercase 40-character SHA.',
      );
    }
  }
}

final class _GateContract {
  const _GateContract({
    required this.id,
    required this.priority,
    required this.enforcement,
    required this.blockedReasonCode,
    required this.reviewerRoles,
    this.acceptedRiskScope,
  });

  final String id;
  final String priority;
  final String enforcement;
  final String blockedReasonCode;
  final List<String> reviewerRoles;
  final String? acceptedRiskScope;
}

// The `0.x` release register keeps every gate visible but only lets a
// `blocking` gate hold a candidate. TopiaForge has never shipped, and eight of
// the original twelve gates wait on organizational evidence — counsel sign-off,
// a paid code-signing certificate, GitHub org administration, external QA
// participants — that an alpha line cannot obtain. Blocking is therefore
// reserved for the four gates that are release-fatal at any version plus the
// one that proves the product runs at all:
//
//   P0-IP-01     rights to integrate with Robotopia
//   P0-OSS-01    third-party redistribution
//   P0-PRIV-01   the unapproved RoboAPI backend dependency
//   P0-CRED-01   rotation of exposed credentials
//   P0-GAME-01   the product loads and runs on the pinned game build
//
// The remaining seven are advisory: recorded, reported, and approvable, but not
// by themselves a stop. Restoring the `1.0` posture is a matter of moving each
// `enforcement` back to `blocking` here and in `release/release-readiness.json`;
// the enforcement value is pinned per gate so the decision file cannot relax
// itself. See docs/LaunchBlockers.md.
const _gateContracts = [
  _GateContract(
    id: 'P0-IP-01',
    priority: 'P0',
    enforcement: 'blocking',
    blockedReasonCode: 'approval-evidence-missing',
    reviewerRoles: ['ip-trademark-counsel', 'project-owner', 'robotopia-owner'],
  ),
  // Re-opened 2026-08-06. The legal inventory is a fixed allowlist that proves
  // the licence texts it names exist, not that every redistributed asset has a
  // licence, so the readiness decision must carry the gate rather than infer it
  // from the inventory passing. See P0-OSS-01 in docs/LaunchBlockers.md.
  _GateContract(
    id: 'P0-OSS-01',
    priority: 'P0',
    enforcement: 'blocking',
    blockedReasonCode: 'approval-evidence-missing',
    reviewerRoles: ['ip-trademark-counsel', 'release-owner'],
  ),
  _GateContract(
    id: 'P0-PRIV-01',
    priority: 'P0',
    enforcement: 'blocking',
    blockedReasonCode: 'approval-evidence-missing',
    reviewerRoles: [
      'backend-owner',
      'privacy-legal',
      'product-owner',
      'robotopia-owner',
      'security-owner',
    ],
  ),
  _GateContract(
    id: 'P0-TRUST-01',
    priority: 'P0',
    enforcement: 'advisory',
    blockedReasonCode: 'approval-evidence-missing',
    reviewerRoles: [
      'product-owner',
      'registry-owner',
      'release-owner',
      'security-owner',
    ],
  ),
  _GateContract(
    id: 'P0-CRED-01',
    priority: 'P0',
    enforcement: 'blocking',
    blockedReasonCode: 'rotation-evidence-missing',
    reviewerRoles: ['credential-owner', 'security-owner'],
  ),
  _GateContract(
    id: 'P0-WIN-01',
    priority: 'P0',
    enforcement: 'advisory',
    blockedReasonCode: 'platform-evidence-missing',
    reviewerRoles: ['release-owner', 'windows-release-qa'],
  ),
  // P0-LINUX-01 is intentionally absent: Linux is descoped from 0.1.0-rc.1 and
  // returns in rc.2. Restore this entry, the schema's gate count and id enum,
  // and the policy platform archives together when it does.
  _GateContract(
    id: 'P0-GAME-01',
    priority: 'P0',
    enforcement: 'blocking',
    blockedReasonCode: 'acceptance-evidence-missing',
    reviewerRoles: ['robotopia-owner', 'runtime-mod-qa'],
  ),
  _GateContract(
    id: 'P0-HOST-01',
    priority: 'P0',
    enforcement: 'advisory',
    blockedReasonCode: 'host-evidence-missing',
    reviewerRoles: [
      'credential-owner',
      'github-administrator',
      'security-owner',
    ],
  ),
  _GateContract(
    id: 'P0-CAND-01',
    priority: 'P0',
    enforcement: 'advisory',
    blockedReasonCode: 'candidate-evidence-missing',
    reviewerRoles: ['project-owner', 'release-manager'],
  ),
  _GateContract(
    id: 'P1-UX-01',
    priority: 'P1',
    enforcement: 'advisory',
    blockedReasonCode: 'acceptance-evidence-missing',
    reviewerRoles: ['accessibility-reviewer', 'native-qa', 'product-owner'],
    acceptedRiskScope: 'rc1-native-ux-accessibility',
  ),
  _GateContract(
    id: 'P1-E2E-01',
    priority: 'P1',
    enforcement: 'advisory',
    blockedReasonCode: 'independent-evidence-missing',
    reviewerRoles: [
      'external-author-reviewer',
      'external-player-reviewer',
      'release-owner',
    ],
    acceptedRiskScope: 'rc1-independent-player-author-e2e',
  ),
  _GateContract(
    id: 'P1-SUPPORT-01',
    priority: 'P1',
    enforcement: 'advisory',
    blockedReasonCode: 'ownership-evidence-missing',
    reviewerRoles: [
      'incident-owner',
      'release-owner',
      'security-intake-owner',
      'support-owner',
    ],
    acceptedRiskScope: 'rc1-support-incident-ownership',
  ),
];

bool _sameList(List<String> left, List<String> right) {
  if (left.length != right.length) return false;
  for (var index = 0; index < left.length; index++) {
    if (left[index] != right[index]) return false;
  }
  return true;
}

bool _isStrictlySorted(List<String> values) {
  for (var index = 1; index < values.length; index++) {
    if (values[index - 1].compareTo(values[index]) >= 0) return false;
  }
  return true;
}
