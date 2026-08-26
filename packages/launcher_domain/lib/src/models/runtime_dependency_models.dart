part of '../models.dart';

class TopiaForgeRuntimeVersions {
  static const loaderVersion = '0.1.0-rc.1';
  static const sdkVersion = '0.1.0-rc.1';
  static const gameVersion = '0.0.2409';
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
    if (json.containsKey('version')) {
      throw const FormatException(
        'Dependency version aliases are not supported; use versionRange.',
      );
    }
    return ModDependency(
      id: (json['id'] as String?) ?? '',
      versionRange: VersionRange.parse(json['versionRange'] as String?),
      optional: (json['optional'] as bool?) ?? false,
    );
  }

  Map<String, Object?> toJson() => {
    'id': id,
    'versionRange': versionRange.toString(),
    if (optional) 'optional': true,
  };
}
