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
    this.loadBefore = const [],
    this.gameVersionRange = const VersionRange.any(),
    this.loaderVersionRange = const VersionRange.any(),
    this.sdkVersionRange = const VersionRange.any(),
    this.gameVersionRangeIsPresent = true,
    this.loaderVersionRangeIsPresent = true,
    this.sdkVersionRangeIsPresent = true,
    this.category = '',
    this.tags = const [],
    this.icon = '',
    this.screenshots = const [],
    this.homepage = '',
    this.source = '',
    this.license = '',
    this.licenseFiles = const [],
    this.hashes = const {},
    this.capabilities = const [],
    this.platforms = const [],
    this.architectures = const [],
    this.contentTargets = const [],
    this.builtWith,
    this.worldGamemodes = const [],
    this.apiAssemblies = const [],
    this.extraFields = const {},
    List<String> structuralIssues = const [],
  }) : _structuralIssues = structuralIssues;

  /// Canonical URL for the manifest JSON schema, used by editors for
  /// autocomplete and validation of `topiaforge.mod.json`.
  static const canonicalSchemaUrl =
      'https://raw.githubusercontent.com/furroxide/TopiaForge/main/schemas/topiaforge.mod.schema.json';

  static bool isValidId(String id) {
    if (!_modIdPattern.hasMatch(id)) {
      return false;
    }
    final normalized = id.toLowerCase();
    return !_retiredEcosystemIdPrefixes.any(normalized.startsWith);
  }

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
  final List<String> loadBefore;
  final VersionRange gameVersionRange;
  final VersionRange loaderVersionRange;
  final VersionRange sdkVersionRange;
  final bool gameVersionRangeIsPresent;
  final bool loaderVersionRangeIsPresent;
  final bool sdkVersionRangeIsPresent;
  final String category;
  final List<String> tags;
  final String icon;
  final List<String> screenshots;
  final String homepage;
  final String source;
  final String license;
  final List<String> licenseFiles;
  final Map<String, String> hashes;
  final List<String> capabilities;
  final List<String> platforms;
  final List<String> architectures;
  final List<String> contentTargets;
  final ModBuildMetadata? builtWith;
  final List<GamemodeDefinition> worldGamemodes;
  final List<String> apiAssemblies;

  /// Namespaced `x-*` extension fields survive a read/edit/write cycle.
  /// Retired aliases and invalid unknown fields remain visible so validation
  /// can reject them explicitly.
  final Map<String, Object?> extraFields;
  final List<String> _structuralIssues;

  List<ModDependency> get allDependencies => [
    ...dependencies,
    ...optionalDependencies,
  ];

  factory ModManifest.fromJson(Map<String, Object?> json) {
    return ModManifest(
      schemaVersion: (json['schemaVersion'] as num?)?.toInt() ?? 0,
      schemaUrl: (json[r'$schema'] as String?) ?? '',
      id: (json['name'] as String?) ?? '',
      name: (json['displayName'] as String?) ?? '',
      version: (json['version'] as String?) ?? '',
      author: ModAuthor.fromJson(json['author']),
      authorIsObject: json['author'] is Map,
      description: (json['description'] as String?) ?? '',
      entryAssembly: (json['entryAssembly'] as String?) ?? '',
      entryType: (json['entryType'] as String?) ?? '',
      dependencies: _dependencyMapList(json['dependencies']),
      optionalDependencies: _dependencyMapList(
        json['optionalDependencies'],
        optional: true,
      ),
      conflicts: _conflictList(json['conflicts']),
      loadAfter: _stringList(json['loadAfter']),
      loadBefore: _stringList(json['loadBefore']),
      gameVersionRange: VersionRange.parse(
        json['supportedGameVersionRange'] as String?,
      ),
      loaderVersionRange: VersionRange.parse(
        json['supportedLoaderVersionRange'] as String?,
      ),
      sdkVersionRange: VersionRange.parse(
        json['supportedSdkVersionRange'] as String?,
      ),
      gameVersionRangeIsPresent: json.containsKey('supportedGameVersionRange'),
      loaderVersionRangeIsPresent: json.containsKey(
        'supportedLoaderVersionRange',
      ),
      sdkVersionRangeIsPresent: json.containsKey('supportedSdkVersionRange'),
      category: (json['category'] as String?) ?? '',
      tags: _stringList(json['tags']),
      icon: (json['icon'] as String?) ?? '',
      screenshots: _stringList(json['screenshots']),
      homepage: (json['homepage'] as String?) ?? '',
      source: (json['source'] as String?) ?? '',
      license: (json['license'] as String?) ?? '',
      licenseFiles: _stringList(json['licenseFiles']),
      hashes: _stringMap(json['hashes']),
      capabilities: _stringList(json['capabilities']),
      platforms: _stringList(json['platforms']),
      architectures: _stringList(json['architectures']),
      contentTargets: _stringList(json['contentTargets']),
      builtWith: json['builtWith'] == null
          ? null
          : ModBuildMetadata.fromJson(json['builtWith']),
      worldGamemodes: _gamemodeList(json['worldGamemodes']),
      apiAssemblies: _stringList(json['apiAssemblies']),
      structuralIssues: _manifestStructuralIssues(json),
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
      'dependencies': {
        for (final item in dependencies) item.id: item.versionRange.toString(),
      },
    if (optionalDependencies.isNotEmpty)
      'optionalDependencies': {
        for (final item in optionalDependencies)
          item.id: item.versionRange.toString(),
      },
    if (conflicts.isNotEmpty)
      'conflicts': conflicts.map((item) => item.toJson()).toList(),
    if (loadAfter.isNotEmpty) 'loadAfter': loadAfter,
    if (loadBefore.isNotEmpty) 'loadBefore': loadBefore,
    'supportedGameVersionRange': gameVersionRange.toString(),
    'supportedLoaderVersionRange': loaderVersionRange.toString(),
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
    if (capabilities.isNotEmpty) 'capabilities': capabilities,
    if (platforms.isNotEmpty) 'platforms': platforms,
    if (architectures.isNotEmpty) 'architectures': architectures,
    if (contentTargets.isNotEmpty) 'contentTargets': contentTargets,
    if (builtWith != null) 'builtWith': builtWith!.toJson(),
    if (worldGamemodes.isNotEmpty)
      'worldGamemodes': worldGamemodes.map((item) => item.toJson()).toList(),
    if (apiAssemblies.isNotEmpty) 'apiAssemblies': apiAssemblies,
  };

  List<LauncherIssue> validate() {
    final issues = <LauncherIssue>[];
    _validateRequiredFields(issues);
    _validateDependencies(issues);
    _validateConflicts(issues);
    _validateOrderHints(issues);
    _validateApiAssemblies(issues);
    _validateManifestWorldGamemodes(this, issues);
    _validateUnsupportedAliases(issues);
    _validateUnknownFields(issues);
    for (final message in _structuralIssues) {
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          subjectId: id,
          message: message,
        ),
      );
    }
    _validateManifestV4Constraints(this, issues);
    _validateLicense(issues);
    _validateManifestLicenseFiles(this, issues);
    _validateScaffoldPlaceholders(this, issues);
    return issues;
  }

  void _validateRequiredFields(List<LauncherIssue> issues) {
    if (schemaVersion != 4) {
      issues.add(
        const LauncherIssue(
          severity: IssueSeverity.error,
          message: 'schemaVersion must be 4.',
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
    } else if (_portableManifestPathCollisionKey(entryAssembly) == null ||
        !entryAssembly.toLowerCase().endsWith('.dll')) {
      issues.add(
        const LauncherIssue(
          severity: IssueSeverity.error,
          message:
              'entryAssembly must be a relative file path in the package and name a safe portable .dll.',
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
    for (final requirement in <(bool, String)>[
      (gameVersionRangeIsPresent, 'supportedGameVersionRange'),
      (loaderVersionRangeIsPresent, 'supportedLoaderVersionRange'),
      (sdkVersionRangeIsPresent, 'supportedSdkVersionRange'),
    ]) {
      if (!requirement.$1) {
        issues.add(
          LauncherIssue(
            severity: IssueSeverity.error,
            message: '${requirement.$2} is required for publishable manifests.',
          ),
        );
      }
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

  void _validateOrderHints(List<LauncherIssue> issues) {
    for (final hint in <(String, List<String>)>[
      ('loadAfter', loadAfter),
      ('loadBefore', loadBefore),
    ]) {
      for (final dependencyId in hint.$2) {
        if (!ModManifest.isValidId(dependencyId)) {
          issues.add(
            LauncherIssue(
              severity: IssueSeverity.error,
              subjectId: dependencyId,
              message:
                  '${hint.$1} id $dependencyId must use the safe mod id format.',
            ),
          );
        }
      }
    }
  }

  void _validateApiAssemblies(List<LauncherIssue> issues) {
    final seen = <String>{};
    for (final assembly in apiAssemblies) {
      if (_portableManifestPathCollisionKey(assembly) == null ||
          !assembly.toLowerCase().endsWith('.dll')) {
        issues.add(
          const LauncherIssue(
            severity: IssueSeverity.error,
            message: 'apiAssemblies entries must be safe portable .dll paths.',
          ),
        );
      } else if (!seen.add(assembly.toLowerCase())) {
        issues.add(
          LauncherIssue(
            severity: IssueSeverity.error,
            message: 'apiAssemblies contains duplicate path $assembly.',
          ),
        );
      }
    }
  }

  void _validateUnsupportedAliases(List<LauncherIssue> issues) {
    for (final field in _unsupportedManifestFields) {
      if (extraFields.containsKey(field)) {
        issues.add(
          LauncherIssue(
            severity: IssueSeverity.error,
            subjectId: id,
            message:
                '$field is not supported by the TopiaForge manifest contract.',
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

  void _validateUnknownFields(List<LauncherIssue> issues) {
    for (final field in extraFields.keys) {
      if (!_unsupportedManifestFields.contains(field) &&
          !RegExp(r'^x-[A-Za-z0-9_.-]{1,64}$').hasMatch(field)) {
        issues.add(
          LauncherIssue(
            severity: IssueSeverity.error,
            subjectId: id,
            message: 'Unknown manifest field $field; extensions must use x-*.',
          ),
        );
      }
    }
  }
}
