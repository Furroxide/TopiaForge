part of '../models.dart';

class RobotopiaRuntimeVersions {
  static const loaderVersion = '0.2.0';
  static const sdkVersion = '0.1.0';
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

class ModAuthor {
  const ModAuthor({this.name = '', this.email = '', this.url = ''});

  final String name;
  final String email;
  final String url;

  bool get isEmpty =>
      name.trim().isEmpty && email.trim().isEmpty && url.trim().isEmpty;

  factory ModAuthor.fromJson(Object? value) {
    if (value is String) {
      return ModAuthor(name: value);
    }
    final json = _objectMap(value);
    return ModAuthor(
      name: (json['name'] as String?) ?? '',
      email: (json['email'] as String?) ?? '',
      url: (json['url'] as String?) ?? '',
    );
  }

  Map<String, Object?> toJson() => {
    'name': name,
    if (email.isNotEmpty) 'email': email,
    if (url.isNotEmpty) 'url': url,
  };
}

class ModConflict {
  const ModConflict({
    required this.id,
    this.versionRange = const VersionRange.any(),
    this.reason = '',
  });

  final String id;
  final VersionRange versionRange;
  final String reason;

  factory ModConflict.fromJson(Map<String, Object?> json) {
    return ModConflict(
      id: (json['id'] as String?) ?? '',
      versionRange: VersionRange.parse(
        (json['versionRange'] as String?) ?? (json['version'] as String?),
      ),
      reason: (json['reason'] as String?) ?? '',
    );
  }

  Map<String, Object?> toJson() => {
    'id': id,
    'versionRange': versionRange.toString(),
    if (reason.isNotEmpty) 'reason': reason,
  };
}

class ModManifest {
  const ModManifest({
    required this.schemaVersion,
    required this.id,
    required this.name,
    required this.version,
    this.schemaUrl = '',
    this.author = const ModAuthor(),
    this.description = '',
    this.entryAssembly = '',
    this.entryType = '',
    this.dependencies = const [],
    this.optionalDependencies = const [],
    this.conflicts = const [],
    this.loadAfter = const [],
    this.gameVersionRange = const VersionRange.any(),
    this.loaderVersionRange = const VersionRange.any(),
    this.sdkVersionRange = const VersionRange.any(),
    this.category = '',
    this.tags = const [],
    this.icon = '',
    this.screenshots = const [],
    this.homepage = '',
    this.source = '',
    this.license = '',
    this.hashes = const {},
    this.permissions = const [],
    this.worldGamemodes = const [],
    this.apiAssemblies = const [],
    this.legacyFolders = const {},
    this.legacyFiles = const {},
    this.legacyPackages = const [],
  });

  /// Canonical URL for the manifest JSON schema, used by editors for
  /// autocomplete and validation of `robotopia.mod.json`.
  static const canonicalSchemaUrl =
      'https://raw.githubusercontent.com/furroxide/quantum-works/main/schemas/robotopia.mod.schema.json';

  final int schemaVersion;
  final String schemaUrl;
  final String id;
  final String name;
  final String version;
  final ModAuthor author;
  final String description;
  final String entryAssembly;
  final String entryType;
  final List<ModDependency> dependencies;
  final List<ModDependency> optionalDependencies;
  final List<ModConflict> conflicts;
  final List<String> loadAfter;
  final VersionRange gameVersionRange;
  final VersionRange loaderVersionRange;
  final VersionRange sdkVersionRange;
  final String category;
  final List<String> tags;
  final String icon;
  final List<String> screenshots;
  final String homepage;
  final String source;
  final String license;
  final Map<String, String> hashes;
  final List<String> permissions;
  final List<GamemodeDefinition> worldGamemodes;
  final List<String> apiAssemblies;
  final Map<String, String> legacyFolders;
  final Map<String, String> legacyFiles;
  final List<String> legacyPackages;

  List<ModDependency> get allDependencies => [
    ...dependencies,
    ...optionalDependencies,
  ];

  factory ModManifest.fromJson(Map<String, Object?> json) {
    final parsedDependencies = [
      ..._vpmDependencyList(json['vpmDependencies']),
      ..._dependencyList(json['dependencies']),
    ];
    final requiredDependencies = parsedDependencies
        .where((dependency) => !dependency.optional)
        .toList(growable: false);
    final optionalDependencies = [
      ...parsedDependencies.where((dependency) => dependency.optional),
      ..._dependencyList(json['optionalDependencies']),
    ];

    return ModManifest(
      schemaVersion: (json['schemaVersion'] as num?)?.toInt() ?? 0,
      schemaUrl: (json[r'$schema'] as String?) ?? '',
      id: (json['name'] as String?) ?? (json['id'] as String?) ?? '',
      name:
          (json['displayName'] as String?) ??
          (json['title'] as String?) ??
          (json['id'] == null ? '' : (json['name'] as String?) ?? ''),
      version: (json['version'] as String?) ?? '',
      author: ModAuthor.fromJson(json['author']),
      description: (json['description'] as String?) ?? '',
      entryAssembly: (json['entryAssembly'] as String?) ?? '',
      entryType: (json['entryType'] as String?) ?? '',
      dependencies: requiredDependencies,
      optionalDependencies: optionalDependencies,
      conflicts: _conflictList(json['conflicts']),
      loadAfter: _stringList(json['loadAfter']),
      gameVersionRange: VersionRange.parse(
        (json['supportedGameVersionRange'] as String?) ??
            (json['gameVersionRange'] as String?) ??
            (json['gameVersion'] as String?),
      ),
      loaderVersionRange: VersionRange.parse(
        (json['supportedLoaderVersionRange'] as String?) ??
            (json['loaderVersionRange'] as String?),
      ),
      sdkVersionRange: VersionRange.parse(
        (json['supportedSdkVersionRange'] as String?) ??
            (json['sdkVersionRange'] as String?),
      ),
      category: (json['category'] as String?) ?? '',
      tags: _stringList(json['tags']),
      icon: (json['icon'] as String?) ?? '',
      screenshots: _stringList(json['screenshots']),
      homepage: (json['homepage'] as String?) ?? '',
      source: (json['source'] as String?) ?? '',
      license: (json['license'] as String?) ?? '',
      hashes: _stringMap(json['hashes'] ?? json['packageHashes']),
      permissions: _stringList(json['permissions']),
      worldGamemodes: _gamemodeList(
        json['worldGamemodes'] ?? json['gamemodes'],
      ),
      apiAssemblies: _stringList(json['apiAssemblies']),
      legacyFolders: _stringMap(json['legacyFolders']),
      legacyFiles: _stringMap(json['legacyFiles']),
      legacyPackages: _stringList(json['legacyPackages']),
    );
  }

  Map<String, Object?> toJson() => {
    if (schemaUrl.isNotEmpty) r'$schema': schemaUrl,
    'schemaVersion': schemaVersion,
    'name': id,
    'displayName': name,
    'version': version,
    if (!author.isEmpty) 'author': author.toJson(),
    if (description.isNotEmpty) 'description': description,
    if (entryAssembly.isNotEmpty) 'entryAssembly': entryAssembly,
    if (entryType.isNotEmpty) 'entryType': entryType,
    if (dependencies.isNotEmpty)
      'vpmDependencies': {
        for (final item in dependencies) item.id: item.versionRange.toString(),
      },
    if (optionalDependencies.isNotEmpty)
      'optionalDependencies': optionalDependencies
          .map((item) => item.toJson())
          .toList(),
    if (conflicts.isNotEmpty)
      'conflicts': conflicts.map((item) => item.toJson()).toList(),
    if (loadAfter.isNotEmpty) 'loadAfter': loadAfter,
    if (!gameVersionRange.isAny)
      'supportedGameVersionRange': gameVersionRange.toString(),
    if (!loaderVersionRange.isAny)
      'supportedLoaderVersionRange': loaderVersionRange.toString(),
    if (!sdkVersionRange.isAny)
      'supportedSdkVersionRange': sdkVersionRange.toString(),
    if (category.isNotEmpty) 'category': category,
    if (tags.isNotEmpty) 'tags': tags,
    if (icon.isNotEmpty) 'icon': icon,
    if (screenshots.isNotEmpty) 'screenshots': screenshots,
    if (homepage.isNotEmpty) 'homepage': homepage,
    if (source.isNotEmpty) 'source': source,
    if (license.isNotEmpty) 'license': license,
    if (hashes.isNotEmpty) 'hashes': hashes,
    if (permissions.isNotEmpty) 'permissions': permissions,
    if (worldGamemodes.isNotEmpty)
      'worldGamemodes': worldGamemodes.map((item) => item.toJson()).toList(),
    if (apiAssemblies.isNotEmpty) 'apiAssemblies': apiAssemblies,
    if (legacyFolders.isNotEmpty) 'legacyFolders': legacyFolders,
    if (legacyFiles.isNotEmpty) 'legacyFiles': legacyFiles,
    if (legacyPackages.isNotEmpty) 'legacyPackages': legacyPackages,
  };

  List<LauncherIssue> validate() {
    final issues = <LauncherIssue>[];
    final idPattern = RegExp(r'^[A-Za-z0-9][A-Za-z0-9_.-]{1,63}$');
    _validateRequiredFields(issues, idPattern);
    _validateDependencies(issues);
    _validateConflicts(issues);
    _validateApiAssemblies(issues);
    _validateMigrationHints(issues);
    _validatePermissions(issues);
    _validateLicense(issues);
    return issues;
  }

  void _validateRequiredFields(List<LauncherIssue> issues, RegExp idPattern) {
    if (schemaVersion != 2) {
      issues.add(
        const LauncherIssue(
          severity: IssueSeverity.error,
          message: 'schemaVersion must be 2.',
        ),
      );
    }
    if (!idPattern.hasMatch(id)) {
      issues.add(
        const LauncherIssue(
          severity: IssueSeverity.error,
          message:
              'name must be 2-64 characters and use letters, numbers, underscore, dot, or dash.',
        ),
      );
    }
    if (name.trim().isEmpty) {
      issues.add(
        const LauncherIssue(
          severity: IssueSeverity.error,
          message: 'displayName is required.',
        ),
      );
    }
    if (author.name.trim().isEmpty) {
      issues.add(
        const LauncherIssue(
          severity: IssueSeverity.error,
          message: 'author.name is required.',
        ),
      );
    }
    if (SemanticVersion.tryParse(version) == null) {
      issues.add(
        const LauncherIssue(
          severity: IssueSeverity.error,
          message: 'version must be parseable as a semantic version.',
        ),
      );
    }
    if (entryAssembly.trim().isEmpty) {
      issues.add(
        const LauncherIssue(
          severity: IssueSeverity.error,
          message: 'entryAssembly is required for C# mods.',
        ),
      );
    } else if (_isUnsafeRelativePath(entryAssembly)) {
      issues.add(
        const LauncherIssue(
          severity: IssueSeverity.error,
          message: 'entryAssembly must be a relative file path in the package.',
        ),
      );
    }
    if (entryType.trim().isEmpty) {
      issues.add(
        const LauncherIssue(
          severity: IssueSeverity.error,
          message: 'entryType is required for C# mods.',
        ),
      );
    }
  }

  void _validateDependencies(List<LauncherIssue> issues) {
    final seenDependencies = <String>{};
    for (final dependency in dependencies) {
      if (dependency.id.trim().isEmpty) {
        issues.add(
          const LauncherIssue(
            severity: IssueSeverity.error,
            message: 'dependencies entries must include id.',
          ),
        );
      } else if (!seenDependencies.add(dependency.id.toLowerCase())) {
        issues.add(
          LauncherIssue(
            severity: IssueSeverity.error,
            subjectId: dependency.id,
            message: 'dependencies contains duplicate id ${dependency.id}.',
          ),
        );
      }
    }
  }

  void _validateConflicts(List<LauncherIssue> issues) {
    for (final conflict in conflicts) {
      if (conflict.id.trim().isEmpty) {
        issues.add(
          const LauncherIssue(
            severity: IssueSeverity.error,
            message: 'conflicts entries must include id.',
          ),
        );
      }
    }
  }

  void _validateApiAssemblies(List<LauncherIssue> issues) {
    final seen = <String>{};
    for (final assembly in apiAssemblies) {
      if (assembly.trim().isEmpty || _isUnsafeRelativePath(assembly)) {
        issues.add(
          const LauncherIssue(
            severity: IssueSeverity.error,
            message:
                'apiAssemblies entries must be safe relative package paths.',
          ),
        );
      } else if (!seen.add(assembly.toLowerCase())) {
        issues.add(
          LauncherIssue(
            severity: IssueSeverity.warning,
            message: 'apiAssemblies contains duplicate path $assembly.',
          ),
        );
      }
    }
  }

  void _validateMigrationHints(List<LauncherIssue> issues) {
    for (final path in [...legacyFolders.keys, ...legacyFiles.keys]) {
      if (path.trim().isEmpty || _isUnsafeRelativePath(path)) {
        issues.add(
          const LauncherIssue(
            severity: IssueSeverity.warning,
            message: 'legacy migration hints should use relative paths.',
          ),
        );
      }
    }
  }

  void _validateLicense(List<LauncherIssue> issues) {
    if (license.trim().isEmpty) {
      return;
    }
    final spdxLike = RegExp(
      r'^[A-Za-z0-9][A-Za-z0-9-.+]*(\s+(AND|OR|WITH)\s+[A-Za-z0-9][A-Za-z0-9-.+]*)*$',
    );
    if (!spdxLike.hasMatch(license.trim())) {
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.warning,
          subjectId: id,
          message: 'license should use an SPDX-style identifier when possible.',
        ),
      );
    }
  }

  void _validatePermissions(List<LauncherIssue> issues) {
    for (final permission in permissions) {
      if (!_knownPermissions.contains(permission)) {
        issues.add(
          LauncherIssue(
            severity: IssueSeverity.warning,
            subjectId: id,
            message: 'permissions contains unknown value $permission.',
          ),
        );
      }
    }
  }
}

const _knownPermissions = {
  'ai',
  'asset-bundles',
  'filesystem',
  'filesystem-watch',
  'harmony-patch',
  'hud',
  'input',
  'navigation',
  'network',
  'particles',
  'physics',
  'physics-settings',
  'player-control',
  'prompt-overrides',
  'quality-settings',
  'render-settings',
  'robot-spawning',
  'scene-management',
  'time',
  'ugc-livesync',
  'world-service',
};

List<ModDependency> _vpmDependencyList(Object? value) {
  if (value is! Map) {
    return const [];
  }

  return value.entries
      .map(
        (entry) => ModDependency(
          id: entry.key.toString(),
          versionRange: VersionRange.parse(entry.value?.toString()),
        ),
      )
      .toList(growable: false);
}

List<GamemodeDefinition> _gamemodeList(Object? value) {
  if (value is! List) {
    return const [];
  }

  return value
      .whereType<Map>()
      .map((item) => GamemodeDefinition.fromJson(_objectMap(item)))
      .where((item) => item.id.trim().isNotEmpty && item.name.trim().isNotEmpty)
      .toList(growable: false);
}
