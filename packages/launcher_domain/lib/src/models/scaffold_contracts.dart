part of '../models.dart';

/// Defaults used when a scaffold caller has not supplied the new mod author's
/// identity and license decision. The author name stays deliberately
/// non-publishable; the license defaults to the project's own terms.
abstract final class TopiaForgeScaffoldDefaults {
  static const authorName = 'REPLACE_WITH_YOUR_NAME';
  static const license = 'AGPL-3.0-or-later';

  /// SPDX sentinel meaning the author has not chosen terms yet. Distinct from
  /// [license]: a scaffold that still carries this value is not publishable.
  static const unresolvedLicense = 'NOASSERTION';
}

void _validateScaffoldPlaceholders(
  ModManifest manifest,
  List<LauncherIssue> issues,
) {
  if (manifest.author.name.trim() == TopiaForgeScaffoldDefaults.authorName) {
    issues.add(
      LauncherIssue(
        severity: IssueSeverity.warning,
        subjectId: manifest.id,
        message: 'Replace the scaffold author placeholder before publishing.',
      ),
    );
  }
  if (manifest.license.trim() == TopiaForgeScaffoldDefaults.unresolvedLicense) {
    issues.add(
      LauncherIssue(
        severity: IssueSeverity.warning,
        subjectId: manifest.id,
        message: 'Choose a license and include its text before publishing.',
      ),
    );
  }
}
