part of '../models.dart';

class RobotopiaRuntimeVersions {
  static const loaderVersion = '0.2.0';
  static const sdkVersion = '0.1.3';
}

class ModDependency {
  const ModDependency({
    required this.id,
    this.versionRange = const VersionRange.any(),
    this.optional = false,
  });

  final String id;
  final VersionRange versionRange;
  final bool optional;

  factory ModDependency.fromJson(Map<String, Object?> json) {
    return ModDependency(
      id: (json['id'] as String?) ?? '',
      versionRange: VersionRange.parse(
        (json['versionRange'] as String?) ?? (json['version'] as String?),
      ),
      optional: (json['optional'] as bool?) ?? false,
    );
  }

  Map<String, Object?> toJson() => {
    'id': id,
    'versionRange': versionRange.toString(),
    if (optional) 'optional': true,
  };
}
