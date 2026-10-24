import '../launcher_domain.dart';

/// A manifest semantic and the first loader release that understands it.
class ManifestFeatureRequirement {
  const ManifestFeatureRequirement({
    required this.name,
    required this.minimumLoaderVersion,
    required this.isUsed,
  });

  final String name;
  final String minimumLoaderVersion;
  final bool Function(ModManifest manifest) isUsed;
}

/// Shared by packing and checking packages. No post-0.1 features ship yet.
class ManifestFeatureRequirements {
  const ManifestFeatureRequirements([this.features = const []]);

  static const current = ManifestFeatureRequirements();
  final List<ManifestFeatureRequirement> features;

  List<LauncherIssue> validate(ModManifest manifest) {
    final issues = <LauncherIssue>[];
    for (final feature in features) {
      if (!feature.isUsed(manifest)) continue;
      final required = SemanticVersion.parse(feature.minimumLoaderVersion);
      final minimum = manifest.loaderVersionRange.min;
      if (minimum != null && minimum.compareTo(required) >= 0) continue;
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          subjectId: manifest.id,
          message:
              'TFPKG180: ${feature.name} needs TopiaForge $required or newer, '
              'but topiaforge.mod.json admits ${manifest.loaderVersionRange}. '
              'Raise supportedLoaderVersionRange.',
        ),
      );
    }
    return issues;
  }
}
