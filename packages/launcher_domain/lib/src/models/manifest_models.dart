part of '../models.dart';

class ModManifest {
  const ModManifest({
    required this.schemaVersion,
    required this.id,
    required this.name,
    required this.version,
    this.schemaUrl = '',
    this.author = const ModAuthor(),
    this.authorIsObject = true,
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
    this.licenseFiles = const [],
    this.hashes = const {},
    this.permissions = const [],
    this.worldGamemodes = const [],
    this.apiAssemblies = const [],
    this.legacyFolders = const {},
    this.legacyFiles = const {},
    this.legacyPackages = const [],
    this.extraFields = const {},
  });

  /// Canonical URL for the manifest JSON schema, used by editors for
  /// autocomplete and validation of `robotopia.mod.json`.
  static const canonicalSchemaUrl =
      'https://raw.githubusercontent.com/furroxide/quantum-works/main/schemas/robotopia.mod.schema.json';

  static bool isValidId(String id) => _modIdPattern.hasMatch(id);

  final int schemaVersion;
  final String schemaUrl;
  final String id;
  final String name;
  final String version;
  final ModAuthor author;
  final bool authorIsObject;
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
  final List<String> licenseFiles;
  final Map<String, String> hashes;
  final List<String> permissions;
  final List<GamemodeDefinition> worldGamemodes;
  final List<String> apiAssemblies;
  final Map<String, String> legacyFolders;
  final Map<String, String> legacyFiles;
  final List<String> legacyPackages;

  /// Additive fields from a newer schema revision. Known aliases are
  /// canonicalized, while fields this version does not understand survive a
  /// read/edit/write cycle unchanged.
  final Map<String, Object?> extraFields;

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
      authorIsObject: json['author'] is Map,
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
      licenseFiles: _stringList(json['licenseFiles']),
      hashes: _stringMap(json['hashes'] ?? json['packageHashes']),
      permissions: _stringList(json['permissions']),
      worldGamemodes: _gamemodeList(
        json['worldGamemodes'] ?? json['gamemodes'],
      ),
      apiAssemblies: _stringList(json['apiAssemblies']),
      legacyFolders: _stringMap(json['legacyFolders']),
      legacyFiles: _stringMap(json['legacyFiles']),
      legacyPackages: _stringList(json['legacyPackages']),
      extraFields: Map<String, Object?>.unmodifiable(
        Map<String, Object?>.of(json)
          ..removeWhere((key, _) => _knownManifestJsonKeys.contains(key)),
      ),
    );
  }

  Map<String, Object?> toJson() => {
    ...extraFields,
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
    if (licenseFiles.isNotEmpty) 'licenseFiles': licenseFiles,
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
    _validateRequiredFields(issues);
    _validateDependencies(issues);
    _validateConflicts(issues);
    _validateLoadAfter(issues);
    _validateApiAssemblies(issues);
    _validateMigrationHints(issues);
    _validatePermissions(issues);
    _validateLicense(issues);
    _validateManifestLicenseFiles(this, issues);
    _validateScaffoldPlaceholders(this, issues);
    return issues;
  }

  void _validateRequiredFields(List<LauncherIssue> issues) {
    if (schemaVersion != 2) {
      issues.add(
        const LauncherIssue(
          severity: IssueSeverity.error,
          message: 'schemaVersion must be 2.',
        ),
      );
    }
    if (!ModManifest.isValidId(id)) {
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
    if (!authorIsObject) {
      issues.add(
        const LauncherIssue(
          severity: IssueSeverity.error,
          message: 'author must be an object with a name field.',
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
          message: 'version must be a valid SemVer 2.0.0 string.',
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
    for (final dependency in allDependencies) {
      if (!ModManifest.isValidId(dependency.id)) {
        issues.add(
          LauncherIssue(
            severity: IssueSeverity.error,
            subjectId: dependency.id,
            message:
                'dependencies id ${dependency.id} must use the safe mod id format.',
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
      if (!ModManifest.isValidId(conflict.id)) {
        issues.add(
          LauncherIssue(
            severity: IssueSeverity.error,
            subjectId: conflict.id,
            message:
                'conflicts id ${conflict.id} must use the safe mod id format.',
          ),
        );
      }
    }
  }

  void _validateLoadAfter(List<LauncherIssue> issues) {
    for (final dependencyId in loadAfter) {
      if (!ModManifest.isValidId(dependencyId)) {
        issues.add(
          LauncherIssue(
            severity: IssueSeverity.error,
            subjectId: dependencyId,
            message:
                'loadAfter id $dependencyId must use the safe mod id format.',
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

const _knownManifestJsonKeys = <String>{
  r'$schema',
  'schemaVersion',
  'name',
  'id',
  'displayName',
  'title',
  'version',
  'author',
  'description',
  'entryAssembly',
  'entryType',
  'vpmDependencies',
  'dependencies',
  'optionalDependencies',
  'conflicts',
  'loadAfter',
  'supportedGameVersionRange',
  'gameVersionRange',
  'gameVersion',
  'supportedLoaderVersionRange',
  'loaderVersionRange',
  'supportedSdkVersionRange',
  'sdkVersionRange',
  'category',
  'tags',
  'icon',
  'screenshots',
  'homepage',
  'source',
  'license',
  'licenseFiles',
  'hashes',
  'packageHashes',
  'permissions',
  'worldGamemodes',
  'gamemodes',
  'apiAssemblies',
  'legacyFolders',
  'legacyFiles',
  'legacyPackages',
};

final _modIdPattern = RegExp(r'^[A-Za-z0-9][A-Za-z0-9_.-]{1,63}$');

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
  'microphone',
  'particles',
  'physics',
  'physics-settings',
  'player-control',
  'player-token',
  'prompt-overrides',
  'quality-settings',
  'remote-ai',
  'render-settings',
  'robot-spawning',
  'scene-management',
  'speech-to-text',
  'time',
  'ugc-livesync',
  'world-service',
};
