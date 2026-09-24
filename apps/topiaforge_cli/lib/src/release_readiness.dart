import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:json_schema/json_schema.dart';

import 'release_strict_json.dart';
import 'release_git_contract.dart';

part 'release_readiness_gate_contracts.dart';

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
      [
        '--no-replace-objects',
        '-C',
        repositoryRoot,
        'rev-parse',
        '--verify',
        '$targetSha^{commit}',
      ],
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
    return readReleaseGitBlob(repositoryRoot, targetSha, path, maximumBytes);
  }

  static Map<String, Object?> _decodeObject(
    List<int> bytes, {
    required int maximumBytes,
    required String label,
  }) => decodeReleaseObject(bytes, maximumBytes: maximumBytes, label: label);

  static void _checkTargetSha(String targetSha) {
    if (!_shaPattern.hasMatch(targetSha)) {
      throw StateError(
        'Release readiness requires an exact lowercase 40-character SHA.',
      );
    }
  }
}
