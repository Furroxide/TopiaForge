part of '../models.dart';

/// Shared constants for the published mod-registry index and the in-repo
/// community entry files (`registry/<id>.json`).
class ModRegistryFormat {
  /// Format of the published `registry/index.json`. Within a format version
  /// changes are additive-only; breaking changes move to a new path
  /// (`registry/v2/index.json`) so older launchers keep a readable index.
  static const indexFormatVersion = 1;

  /// Format of a community entry file under `registry/`.
  static const entryFormatVersion = 1;

  static const canonicalIndexSchemaUrl =
      'https://raw.githubusercontent.com/furroxide/quantum-works/main/schemas/robotopia.registry-index.schema.json';

  static const canonicalEntrySchemaUrl =
      'https://raw.githubusercontent.com/furroxide/quantum-works/main/schemas/robotopia.registry-entry.schema.json';

  /// The official QuantumWorks registry index published by CI to GitHub
  /// Pages. The launcher and developer tooling register this as a built-in
  /// package source.
  static const officialRegistryUrl =
      'https://furroxide.github.io/quantum-works/registry/index.json';

  static const officialSourceId = 'robotopia.official';
  static const officialSourceName = 'QuantumWorks Mod Registry';

  /// Launcher-enforced package size cap; registry validation mirrors it so
  /// oversized submissions are rejected before players ever see them.
  static const maxPackageBytes = 512 * 1024 * 1024;

  /// Community entries may not claim these first-party namespaces.
  static const reservedIdPrefixes = ['robotopia.', 'sample.', 'quantumworks.'];
}

/// One prior version of a mod in the published index (`history` array) or a
/// non-latest version of a community entry. Ignored by launchers that only
/// understand the flat `mods` list — the field is purely additive.
class RegistryVersionRef {
  RegistryVersionRef({
    required this.version,
    required this.downloadUrl,
    required this.packageSha256,
    this.changelog = '',
    this.publishedAt = '',
    Map<String, Object?> extraFields = const {},
  }) : extraFields = _withoutKnownRegistryFields(
         extraFields,
         _registryVersionRefKeys,
       );

  final String version;
  final String downloadUrl;
  final String packageSha256;
  final String changelog;
  final String publishedAt;
  final Map<String, Object?> extraFields;

  factory RegistryVersionRef.fromJson(Map<String, Object?> json) {
    return RegistryVersionRef(
      version: (json['version'] as String?) ?? '',
      downloadUrl: (json['downloadUrl'] as String?) ?? '',
      packageSha256: _lowercaseSha(json['packageSha256']),
      changelog: (json['changelog'] as String?) ?? '',
      publishedAt: (json['publishedAt'] as String?) ?? '',
      extraFields: _unknownRegistryFields(json, _registryVersionRefKeys),
    );
  }

  Map<String, Object?> toJson() => {
    ...extraFields,
    'version': version,
    'downloadUrl': downloadUrl,
    'packageSha256': packageSha256.toLowerCase(),
    if (changelog.isNotEmpty) 'changelog': changelog,
    if (publishedAt.isNotEmpty) 'publishedAt': publishedAt,
  };
}

/// One mod in the published registry index. `toJson()` emits exactly the
/// object shape `RegistryMod.fromJson` consumes (manifest, downloadUrl,
/// packageSha256, changelog), plus additive metadata (`origin`, `history`)
/// that current launchers ignore.
class RegistryIndexEntry {
  RegistryIndexEntry({
    required this.manifest,
    required this.downloadUrl,
    required this.packageSha256,
    this.changelog = '',
    this.origin = '',
    this.publishedAt = '',
    this.history = const [],
    Map<String, Object?> extraFields = const {},
  }) : extraFields = _withoutKnownRegistryFields(
         extraFields,
         _registryIndexEntryKeys,
       );

  final ModManifest manifest;
  final String downloadUrl;
  final String packageSha256;
  final String changelog;

  /// `first-party` or `community`.
  final String origin;
  final String publishedAt;
  final List<RegistryVersionRef> history;
  final Map<String, Object?> extraFields;

  factory RegistryIndexEntry.fromJson(Map<String, Object?> json) {
    return RegistryIndexEntry(
      manifest: ModManifest.fromJson(_objectMap(json['manifest'])),
      downloadUrl: (json['downloadUrl'] as String?) ?? '',
      packageSha256: _lowercaseSha(json['packageSha256']),
      changelog: (json['changelog'] as String?) ?? '',
      origin: (json['origin'] as String?) ?? '',
      publishedAt: (json['publishedAt'] as String?) ?? '',
      history: _versionRefList(json['history']),
      extraFields: _unknownRegistryFields(json, _registryIndexEntryKeys),
    );
  }

  Map<String, Object?> toJson() => {
    ...extraFields,
    'manifest': manifest.toJson(),
    'downloadUrl': downloadUrl,
    'packageSha256': packageSha256.toLowerCase(),
    if (changelog.isNotEmpty) 'changelog': changelog,
    if (origin.isNotEmpty) 'origin': origin,
    if (publishedAt.isNotEmpty) 'publishedAt': publishedAt,
    if (history.isNotEmpty)
      'history': history.map((item) => item.toJson()).toList(),
  };
}

/// One version inside a community entry file. The full manifest is required
/// inline so the published index can be built without downloading community
/// packages (deploys stay hermetic; only PR validation fetches the bytes).
class RegistryEntryVersion {
  RegistryEntryVersion({
    required this.version,
    required this.downloadUrl,
    required this.packageSha256,
    this.changelog = '',
    this.manifest,
    Map<String, Object?> extraFields = const {},
  }) : extraFields = _withoutKnownRegistryFields(
         extraFields,
         _registryEntryVersionKeys,
       );

  final String version;
  final String downloadUrl;
  final String packageSha256;
  final String changelog;
  final ModManifest? manifest;
  final Map<String, Object?> extraFields;

  factory RegistryEntryVersion.fromJson(Map<String, Object?> json) {
    final manifestJson = _objectMap(json['manifest']);
    return RegistryEntryVersion(
      version: (json['version'] as String?) ?? '',
      downloadUrl: (json['downloadUrl'] as String?) ?? '',
      packageSha256: _lowercaseSha(json['packageSha256']),
      changelog: (json['changelog'] as String?) ?? '',
      manifest: manifestJson.isEmpty
          ? null
          : ModManifest.fromJson(manifestJson),
      extraFields: _unknownRegistryFields(json, _registryEntryVersionKeys),
    );
  }

  Map<String, Object?> toJson() => {
    ...extraFields,
    'version': version,
    'downloadUrl': downloadUrl,
    'packageSha256': packageSha256.toLowerCase(),
    if (changelog.isNotEmpty) 'changelog': changelog,
    if (manifest != null) 'manifest': manifest!.toJson(),
  };
}

/// A community submission file at `registry/<id>.json` (filename must equal
/// the lowercase mod id). Versions are kept newest-first by convention; the
/// index builder re-sorts by semantic version regardless.
class RegistryEntryFile {
  RegistryEntryFile({
    required this.id,
    this.formatVersion = ModRegistryFormat.entryFormatVersion,
    this.homepage = '',
    this.versions = const [],
    Map<String, Object?> extraFields = const {},
  }) : extraFields = _withoutKnownRegistryFields(
         extraFields,
         _registryEntryFileKeys,
       );

  final int formatVersion;
  final String id;
  final String homepage;
  final List<RegistryEntryVersion> versions;
  final Map<String, Object?> extraFields;

  factory RegistryEntryFile.fromJson(Map<String, Object?> json) {
    return RegistryEntryFile(
      formatVersion: (json['formatVersion'] as num?)?.toInt() ?? 0,
      id: (json['id'] as String?) ?? '',
      homepage: (json['homepage'] as String?) ?? '',
      versions: _entryVersionList(json['versions']),
      extraFields: _unknownRegistryFields(json, _registryEntryFileKeys),
    );
  }

  Map<String, Object?> toJson() => {
    ...extraFields,
    r'$schema': ModRegistryFormat.canonicalEntrySchemaUrl,
    'formatVersion': formatVersion,
    'id': id,
    if (homepage.isNotEmpty) 'homepage': homepage,
    'versions': versions.map((item) => item.toJson()).toList(),
  };

  /// Versions ordered newest-first by semantic version (unparseable last).
  List<RegistryEntryVersion> get sortedVersions {
    final sorted = [...versions];
    sorted.sort((a, b) {
      final left = SemanticVersion.tryParse(a.version);
      final right = SemanticVersion.tryParse(b.version);
      if (left == null && right == null) {
        return a.version.compareTo(b.version);
      }
      if (left == null) {
        return 1;
      }
      if (right == null) {
        return -1;
      }
      return right.compareTo(left);
    });
    return sorted;
  }

  /// Structural validation (no network). Download-time checks (sha of the
  /// hosted bytes, packaged manifest equality) live in registry validation
  /// tooling, not here.
  List<LauncherIssue> validate() {
    final issues = <LauncherIssue>[];
    if (formatVersion != ModRegistryFormat.entryFormatVersion) {
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          subjectId: id,
          message:
              'formatVersion must be ${ModRegistryFormat.entryFormatVersion}.',
        ),
      );
    }
    final idPattern = RegExp(r'^[A-Za-z0-9][A-Za-z0-9_.-]{1,63}$');
    if (!idPattern.hasMatch(id)) {
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          subjectId: id,
          message:
              'id must be 2-64 characters and use letters, numbers, underscore, dot, or dash.',
        ),
      );
    }
    if (versions.isEmpty) {
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          subjectId: id,
          message: 'versions must contain at least one entry.',
        ),
      );
    }
    if (homepage.isNotEmpty && !_isTrustedPublicHttpsUrl(homepage.trim())) {
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          subjectId: id,
          message:
              'homepage must be an absolute HTTPS URL without credentials, query, or fragment.',
        ),
      );
    }
    final seenVersions = <String>{};
    for (final version in versions) {
      final label = '$id@${version.version}';
      if (SemanticVersion.tryParse(version.version) == null) {
        issues.add(
          LauncherIssue(
            severity: IssueSeverity.error,
            subjectId: label,
            message: 'version must be a valid SemVer 2.0.0 string.',
          ),
        );
      } else if (!seenVersions.add(version.version.trim())) {
        issues.add(
          LauncherIssue(
            severity: IssueSeverity.error,
            subjectId: label,
            message: 'versions contains duplicate version ${version.version}.',
          ),
        );
      }
      if (!_isAllowedPackageUrl(version.downloadUrl)) {
        issues.add(
          LauncherIssue(
            severity: IssueSeverity.error,
            subjectId: label,
            message:
                'downloadUrl must be an absolute https URL (got "${version.downloadUrl}").',
          ),
        );
      }
      if (!_isSha256Hex(version.packageSha256)) {
        issues.add(
          LauncherIssue(
            severity: IssueSeverity.error,
            subjectId: label,
            message: 'packageSha256 must be 64 hex characters.',
          ),
        );
      }
      final manifest = version.manifest;
      if (manifest == null) {
        issues.add(
          LauncherIssue(
            severity: IssueSeverity.error,
            subjectId: label,
            message:
                'manifest is required inline (generate the entry with `robotopia registry add-entry`).',
          ),
        );
        continue;
      }
      if (manifest.id.toLowerCase() != id.toLowerCase()) {
        issues.add(
          LauncherIssue(
            severity: IssueSeverity.error,
            subjectId: label,
            message:
                'manifest name "${manifest.id}" does not match the entry id "$id".',
          ),
        );
      }
      if (manifest.version.trim() != version.version.trim()) {
        issues.add(
          LauncherIssue(
            severity: IssueSeverity.error,
            subjectId: label,
            message:
                'manifest version "${manifest.version}" does not match the entry version "${version.version}".',
          ),
        );
      }
    }
    return issues;
  }
}

String _lowercaseSha(Object? value) {
  return (value is String ? value : '').trim().toLowerCase();
}

bool _isSha256Hex(String value) {
  return RegExp(r'^[0-9a-f]{64}$').hasMatch(value);
}

bool _isAllowedPackageUrl(String value) {
  return _isTrustedPublicHttpsUrl(value.trim());
}

List<RegistryVersionRef> _versionRefList(Object? value) {
  if (value is! List) {
    return const [];
  }
  return value
      .whereType<Map>()
      .map((item) => RegistryVersionRef.fromJson(_objectMap(item)))
      .toList(growable: false);
}

List<RegistryEntryVersion> _entryVersionList(Object? value) {
  if (value is! List) {
    return const [];
  }
  return value
      .whereType<Map>()
      .map((item) => RegistryEntryVersion.fromJson(_objectMap(item)))
      .toList(growable: false);
}

Map<String, Object?> _unknownRegistryFields(
  Map<String, Object?> json,
  Set<String> knownKeys,
) => _immutableRegistryFields(
  Map<String, Object?>.of(json)
    ..removeWhere((key, _) => knownKeys.contains(key)),
);

Map<String, Object?> _withoutKnownRegistryFields(
  Map<String, Object?> fields,
  Set<String> knownKeys,
) => _immutableRegistryFields(
  Map<String, Object?>.of(fields)
    ..removeWhere((key, _) => knownKeys.contains(key)),
);

Map<String, Object?> _immutableRegistryFields(Map<String, Object?> fields) =>
    Map<String, Object?>.unmodifiable({
      for (final entry in fields.entries)
        entry.key: _immutableRegistryValue(entry.value),
    });

Object? _immutableRegistryValue(Object? value) {
  if (value is Map) {
    return Map<String, Object?>.unmodifiable({
      for (final entry in value.entries)
        entry.key.toString(): _immutableRegistryValue(entry.value),
    });
  }
  if (value is List) {
    return List<Object?>.unmodifiable(value.map(_immutableRegistryValue));
  }
  return value;
}

const _registryVersionRefKeys = {
  'version',
  'downloadUrl',
  'packageSha256',
  'changelog',
  'publishedAt',
};
const _registryIndexEntryKeys = {
  'manifest',
  'downloadUrl',
  'packageSha256',
  'changelog',
  'origin',
  'publishedAt',
  'history',
};
const _registryEntryVersionKeys = {
  'version',
  'downloadUrl',
  'packageSha256',
  'changelog',
  'manifest',
};
const _registryEntryFileKeys = {
  r'$schema',
  'formatVersion',
  'id',
  'homepage',
  'versions',
};
