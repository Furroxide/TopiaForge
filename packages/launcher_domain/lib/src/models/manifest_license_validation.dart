part of '../models.dart';

void _validateManifestLicenseFiles(
  ModManifest manifest,
  List<LauncherIssue> issues,
) {
  if (manifest.licenseFiles.length > 32) {
    issues.add(
      const LauncherIssue(
        severity: IssueSeverity.error,
        message: 'licenseFiles may contain at most 32 paths.',
      ),
    );
  }
  final seen = <String>{};
  for (final path in manifest.licenseFiles) {
    final collisionKey = _portableManifestPathCollisionKey(path);
    if (collisionKey == null) {
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          message:
              'licenseFiles entry "$path" must be a safe portable relative path.',
        ),
      );
    } else if (!seen.add(collisionKey)) {
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          message:
              'licenseFiles contains duplicate or portable-collision path "$path".',
        ),
      );
    }
  }
}
