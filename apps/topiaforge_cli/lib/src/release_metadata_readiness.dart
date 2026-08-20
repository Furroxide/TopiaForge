import 'dart:io';

import 'release_readiness.dart';

final class ReleaseMetadataReadiness {
  const ReleaseMetadataReadiness._({
    required this.status,
    required this.blobSha256,
    required this.summary,
    required this.blockingReasons,
  });

  final String status;
  final String? blobSha256;
  final Map<String, Object?>? summary;
  final List<String> blockingReasons;

  static Future<ReleaseMetadataReadiness> load({
    required String repositoryRoot,
    required String version,
    required String targetSha,
    required bool allowUnresolved,
  }) async {
    ReleaseReadinessDecision decision;
    try {
      decision = await ReleaseReadinessDecision.loadAtGitSha(
        repositoryRoot: repositoryRoot,
        targetSha: targetSha,
        expectedReleaseVersion: version,
      );
    } on StateError {
      if (!allowUnresolved) rethrow;
      return const ReleaseMetadataReadiness._(
        status: 'unavailable',
        blobSha256: null,
        summary: null,
        blockingReasons: [
          'Release readiness is unavailable at the exact target SHA.',
        ],
      );
    } on ProcessException {
      if (!allowUnresolved) rethrow;
      return const ReleaseMetadataReadiness._(
        status: 'unavailable',
        blobSha256: null,
        summary: null,
        blockingReasons: [
          'Release readiness is unavailable at the exact target SHA.',
        ],
      );
    }

    final blockingReasons = [
      for (final gate in decision.gates)
        if (!gate.satisfiesRelease)
          'Release readiness gate ${gate.id} is ${gate.status}.',
    ];
    if (!decision.isReady && !allowUnresolved) {
      throw StateError(
        'Release readiness validation failed:\n'
        '- ${blockingReasons.join('\n- ')}',
      );
    }
    return ReleaseMetadataReadiness._(
      status: decision.status,
      blobSha256: decision.readinessBlobSha256,
      summary: decision.toPublicSummary(),
      blockingReasons: List.unmodifiable(blockingReasons),
    );
  }

  Map<String, Object?> toBomJson() => {
    'binding': 'git-blob-at-target-sha',
    'path': releaseReadinessPath,
    'schemaPath': releaseReadinessSchemaPath,
    'status': status,
    'blobSha256': blobSha256,
    'summary': summary,
  };
}

List<String> releaseMetadataBlockingReasons({
  required bool allowUnresolved,
  required bool licenseApproved,
  required String licenseStatus,
  required String licenseExpression,
  required String catalogStatus,
  required ReleaseMetadataReadiness readiness,
}) {
  return [
    if (allowUnresolved) 'Unresolved-policy mode is non-distributable.',
    if (!licenseApproved)
      'Project license is $licenseStatus: $licenseExpression.',
    if (catalogStatus != 'ready')
      'Release catalog status is $catalogStatus, not ready.',
    ...readiness.blockingReasons,
  ];
}
