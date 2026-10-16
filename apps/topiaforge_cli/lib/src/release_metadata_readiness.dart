import 'dart:io';

import 'release_candidate_qualification.dart';

const releaseCandidateMetadataFileNames = [
  'release-candidate-readiness-v1.json',
  'release-candidate-acceptance-v1.json',
];

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
    String? assetsDirectory,
  }) async {
    try {
      if (assetsDirectory == null) {
        throw StateError('Detached candidate qualification requires --assets.');
      }
      final decision = await ReleaseCandidateQualification.loadAtGitSha(
        repositoryRoot: repositoryRoot,
        targetSha: targetSha,
        expectedReleaseVersion: version,
        assetsDirectory: assetsDirectory,
      );
      if (!decision.isReady) {
        throw StateError('Detached candidate qualification is not ready.');
      }
      return ReleaseMetadataReadiness._(
        status: decision.status,
        blobSha256: decision.decisionSha256,
        summary: decision.toPublicSummary(),
        blockingReasons: const [],
      );
    } on StateError {
      if (!allowUnresolved) rethrow;
    } on ProcessException {
      if (!allowUnresolved) rethrow;
    } on FileSystemException {
      if (!allowUnresolved) rethrow;
    } on FormatException {
      if (!allowUnresolved) rethrow;
    }
    return const ReleaseMetadataReadiness._(
      status: 'unavailable',
      blobSha256: null,
      summary: null,
      blockingReasons: [
        'Detached candidate qualification is unavailable or invalid for the '
            'exact target SHA and asset bytes.',
      ],
    );
  }

  Map<String, Object?> toBomJson() => {
    'binding': 'detached-candidate-at-target-sha',
    'path': releaseCandidateMetadataFileNames.first,
    'schemaPath':
        'schemas/topiaforge.release-candidate-readiness-v1.schema.json',
    'acceptancePath': releaseCandidateMetadataFileNames.last,
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
