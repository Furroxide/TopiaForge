import 'dart:convert';

import 'package:crypto/crypto.dart';
import 'package:topiaforge/src/release_handoff.dart';

/// Synthetic test data only: these hashes are never real acceptance evidence.
Map<String, Object?> candidateAcceptanceFixture({
  required Map<String, Object?> decision,
  required Map<String, Object?> gameMetadata,
  required Map<String, Object?> redesignInventory,
  required ReleaseHandoffVerification handoff,
  required String contractSha256,
  required String handoffSha256,
  required List<Map<String, Object?>> payloads,
  String repository = 'Furroxide/TopiaForge',
}) {
  final game = (decision['gates'] as List).cast<Map>().singleWhere(
    (gate) => gate['id'] == 'P0-GAME-01',
  );
  final qa = handoff.platformBundles['windows-x64']!.qa;
  final ids = [
    for (final entry in redesignInventory['cases'] as List)
      (entry as Map)['id'] as String,
  ]..sort();
  return {
    'schema': 'release-candidate-acceptance-v1',
    'repository': repository,
    'releaseVersion': handoff.handoff.version,
    'targetSha': handoff.handoff.targetSha,
    'contractSha256': contractSha256,
    'handoffSha256': handoffSha256,
    'payloads': payloads,
    'gameBuildId': gameMetadata['buildId'],
    'result': 'passed',
    'gameCycles': redesignInventory['requiredLifecycleCycles'],
    'authoringCycles': redesignInventory['requiredAuthoringCycles'],
    'gameEvidenceSha256': (qa['robotopia'] as Map)['evidenceSha256'],
    'authoringEvidenceSha256': (qa['unity'] as Map)['evidenceSha256'],
    'isolation': {
      'kind': 'windows-user',
      'evidenceSha256': _digest('fixture:isolated-user'),
      'persistentDataIsolated': true,
      'normalUserDataAccessed': false,
    },
    'cases': [
      for (final id in ids)
        {
          'id': id,
          'result': 'passed',
          'evidenceSha256': _digest('fixture:$id'),
        },
    ],
    'reviewerEvidence': [
      for (final evidenceId in game['evidenceIds'] as List)
        for (final role in game['reviewerRoles'] as List)
          {
            'evidenceId': evidenceId,
            'role': role,
            'reference': 'review:fixture-${(role as String)}',
            'sha256': _digest('fixture:$evidenceId:$role'),
          },
    ],
  };
}

String _digest(String value) => sha256.convert(utf8.encode(value)).toString();
