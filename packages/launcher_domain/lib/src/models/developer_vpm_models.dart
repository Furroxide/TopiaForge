part of '../models.dart';

class VpmPackageInfo {
  const VpmPackageInfo({
    required this.name,
    required this.version,
    this.displayName = '',
    this.description = '',
    this.unity = '',
    this.url = '',
    this.zipSha256 = '',
    this.vpmDependencies = const {},
  });

  final String name;
  final String version;
  final String displayName;
  final String description;
  final String unity;
  final String url;
  final String zipSha256;
  final Map<String, String> vpmDependencies;

  String get label => displayName.isEmpty ? name : displayName;

  factory VpmPackageInfo.fromJson(Map<String, Object?> json) {
    final name = (json['name'] as String?) ?? '';
    if (!VpmPackageId.isValid(name)) {
      throw FormatException('Invalid VPM package name: $name');
    }
    return VpmPackageInfo(
      name: name,
      version: (json['version'] as String?) ?? '',
      displayName: (json['displayName'] as String?) ?? '',
      description: (json['description'] as String?) ?? '',
      unity: (json['unity'] as String?) ?? '',
      url: (json['url'] as String?) ?? '',
      zipSha256: (json['zipSHA256'] as String?) ?? '',
      vpmDependencies: _vpmDependencyMap(
        json['vpmDependencies'],
        'VPM package $name dependencies',
      ),
    );
  }
}

class VpmListing {
  const VpmListing({this.name = '', this.id = '', this.packages = const {}});

  final String name;
  final String id;
  final Map<String, Map<String, VpmPackageInfo>> packages;

  factory VpmListing.fromJson(Map<String, Object?> json) {
    final result = <String, Map<String, VpmPackageInfo>>{};
    _objectMap(json['packages']).forEach((id, value) {
      if (!VpmPackageId.isValid(id)) {
        throw FormatException('Invalid VPM listing package id: $id');
      }
      final versionsJson = _objectMap(value is Map ? value['versions'] : null);
      final versions = <String, VpmPackageInfo>{};
      versionsJson.forEach((ver, vJson) {
        final info = VpmPackageInfo.fromJson(_objectMap(vJson));
        if (info.name != id) {
          throw FormatException(
            'VPM listing package $id version $ver declares name ${info.name}.',
          );
        }
        versions[ver] = info;
      });
      if (versions.isNotEmpty) {
        result[id] = versions;
      }
    });
    return VpmListing(
      name: (json['name'] as String?) ?? '',
      id: (json['id'] as String?) ?? '',
      packages: result,
    );
  }
}

class VpmLocked {
  const VpmLocked({required this.version, this.dependencies = const {}});

  final String version;
  final Map<String, String> dependencies;

  factory VpmLocked.fromJson(Map<String, Object?> json) => VpmLocked(
    version: (json['version'] as String?) ?? '',
    dependencies: _vpmDependencyMap(
      json['dependencies'],
      'VPM locked dependencies',
    ),
  );

  Map<String, Object?> toJson() => {
    'version': version,
    if (dependencies.isNotEmpty) 'dependencies': dependencies,
  };
}

class VpmManifest {
  const VpmManifest({this.dependencies = const {}, this.locked = const {}});

  final Map<String, String> dependencies;
  final Map<String, VpmLocked> locked;

  factory VpmManifest.fromJson(Map<String, Object?> json) {
    final locked = <String, VpmLocked>{};
    _objectMap(json['locked']).forEach((id, value) {
      if (!VpmPackageId.isValid(id)) {
        throw FormatException('Invalid locked VPM package id: $id');
      }
      locked[id] = VpmLocked.fromJson(_objectMap(value));
    });
    return VpmManifest(
      dependencies: _vpmDependencyMap(
        json['dependencies'],
        'VPM manifest dependencies',
      ),
      locked: locked,
    );
  }

  Map<String, Object?> toJson() => {
    'dependencies': dependencies,
    'locked': {
      for (final entry in locked.entries) entry.key: entry.value.toJson(),
    },
  };

  VpmManifest copyWith({
    Map<String, String>? dependencies,
    Map<String, VpmLocked>? locked,
  }) => VpmManifest(
    dependencies: dependencies ?? this.dependencies,
    locked: locked ?? this.locked,
  );
}

class VpmResolvedPackage {
  const VpmResolvedPackage({
    required this.id,
    required this.version,
    this.url = '',
    this.zipSha256 = '',
    this.displayName = '',
    this.dependencies = const [],
  });

  final String id;
  final String version;
  final String url;
  final String zipSha256;
  final String displayName;
  final List<String> dependencies;
}

class UnityVpmResolution {
  const UnityVpmResolution({this.packages = const [], this.issues = const []});

  final List<VpmResolvedPackage> packages;
  final List<LauncherIssue> issues;

  bool get hasBlockingIssues => issues.any((issue) => issue.isBlocking);
}

Map<String, String> _vpmDependencyMap(Object? value, String label) {
  if (value == null) {
    return const {};
  }
  if (value is! Map) {
    throw FormatException('$label must be a JSON object.');
  }
  final result = <String, String>{};
  value.forEach((key, mapValue) {
    final id = key.toString();
    if (!VpmPackageId.isValid(id)) {
      throw FormatException('$label contains invalid package id: $id');
    }
    if (mapValue is! String || mapValue.trim().isEmpty) {
      throw FormatException('$label range for $id must be a non-empty string.');
    }
    result[id] = mapValue;
  });
  return result;
}
