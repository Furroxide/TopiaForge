part of '../models.dart';

void _validateManifestWorldGamemodes(
  ModManifest manifest,
  List<LauncherIssue> issues,
) {
  final seen = <String>{};
  for (final gamemode in manifest.worldGamemodes) {
    if (!ModManifest.isValidId(gamemode.id)) {
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          subjectId: gamemode.id,
          message:
              'worldGamemodes id ${gamemode.id} must use the safe TopiaForge id format.',
        ),
      );
    } else if (!seen.add(gamemode.id.toLowerCase())) {
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          subjectId: gamemode.id,
          message: 'worldGamemodes contains duplicate id ${gamemode.id}.',
        ),
      );
    }
    if (gamemode.name.trim().isEmpty) {
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          subjectId: gamemode.id,
          message: 'worldGamemodes name is required.',
        ),
      );
    }
  }
}
