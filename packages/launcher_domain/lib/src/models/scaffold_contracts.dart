part of '../models.dart';

/// Deliberately non-publishable defaults used when a scaffold caller has not
/// supplied the new mod author's identity and license decision.
abstract final class RobotopiaScaffoldDefaults {
  static const authorName = 'REPLACE_WITH_YOUR_NAME';
  static const license = 'NOASSERTION';
}

void _validateScaffoldPlaceholders(
  ModManifest manifest,
  List<LauncherIssue> issues,
) {
  if (manifest.author.name.trim() == RobotopiaScaffoldDefaults.authorName) {
    issues.add(
      LauncherIssue(
        severity: IssueSeverity.warning,
        subjectId: manifest.id,
        message: 'Replace the scaffold author placeholder before publishing.',
      ),
    );
  }
  if (manifest.license.trim() == RobotopiaScaffoldDefaults.license) {
    issues.add(
      LauncherIssue(
        severity: IssueSeverity.warning,
        subjectId: manifest.id,
        message: 'Choose a license and include its text before publishing.',
      ),
    );
  }
}
