import 'release_handoff_qa.dart';

class ReleaseHandoffFile {
  const ReleaseHandoffFile({
    required this.name,
    required this.sha256,
    required this.size,
  });

  factory ReleaseHandoffFile.fromJson(Object? value, {required String label}) {
    final json = _handoffObject(value, label);
    _requireHandoffKeys(json, const {'name', 'sha256', 'size'}, label);
    return ReleaseHandoffFile(
      name: _handoffString(json, 'name', label),
      sha256: _handoffString(json, 'sha256', label),
      size: _handoffPositiveInt(json, 'size', label),
    );
  }

  final String name;
  final String sha256;
  final int size;

  Map<String, Object?> toJson() => {
    'name': name,
    'sha256': sha256,
    'size': size,
  };
}

class ReleaseHandoffSigning {
  const ReleaseHandoffSigning({
    required this.scheme,
    required this.status,
    required this.notarization,
    required this.exceptionApplied,
  });

  factory ReleaseHandoffSigning.fromJson(Object? value) {
    final json = _handoffObject(value, 'signing');
    _requireHandoffKeys(json, const {
      'scheme',
      'status',
      'notarization',
      'exceptionApplied',
    }, 'signing');
    return ReleaseHandoffSigning(
      scheme: _handoffString(json, 'scheme', 'signing'),
      status: _handoffString(json, 'status', 'signing'),
      notarization: _handoffString(json, 'notarization', 'signing'),
      exceptionApplied: _handoffBool(json, 'exceptionApplied', 'signing'),
    );
  }

  final String scheme;
  final String status;
  final String notarization;
  final bool exceptionApplied;

  Map<String, Object?> toJson() => {
    'scheme': scheme,
    'status': status,
    'notarization': notarization,
    'exceptionApplied': exceptionApplied,
  };
}

class ReleaseHandoffValidation {
  const ReleaseHandoffValidation({
    required this.status,
    required this.evidenceSha256,
  });

  factory ReleaseHandoffValidation.fromJson(Object? value, String name) {
    final label = 'validations.$name';
    final json = _handoffObject(value, label);
    _requireHandoffKeys(json, const {'status', 'evidenceSha256'}, label);
    return ReleaseHandoffValidation(
      status: _handoffString(json, 'status', label),
      evidenceSha256: _handoffString(json, 'evidenceSha256', label),
    );
  }

  final String status;
  final String evidenceSha256;

  Map<String, Object?> toJson() => {
    'status': status,
    'evidenceSha256': evidenceSha256,
  };
}

class ReleasePlatformBundle {
  const ReleasePlatformBundle({
    required this.version,
    required this.targetSha,
    required this.platform,
    required this.builderProfile,
    required this.archive,
    required this.canonicalEcosystemSha256,
    required this.toolchains,
    required this.platformToolchains,
    required this.signing,
    required this.validations,
    required this.qa,
  });

  factory ReleasePlatformBundle.fromJson(Map<String, Object?> json) {
    _requireHandoffKeys(json, const {
      'schema',
      'version',
      'targetSha',
      'platform',
      'builderProfile',
      'archive',
      'canonicalEcosystemSha256',
      'toolchains',
      'platformToolchains',
      'signing',
      'validations',
      'qa',
    }, 'release platform bundle');
    if (json['schema'] != releasePlatformBundleSchema) {
      throw StateError(
        'Release platform bundle schema must be '
        '$releasePlatformBundleSchema.',
      );
    }
    final toolchainJson = _handoffObject(json['toolchains'], 'toolchains');
    final platformToolchainJson = _handoffObject(
      json['platformToolchains'],
      'platformToolchains',
    );
    final validationJson = _handoffObject(json['validations'], 'validations');
    final platform = _handoffString(
      json,
      'platform',
      'release platform bundle',
    );
    return ReleasePlatformBundle(
      version: _handoffString(json, 'version', 'release platform bundle'),
      targetSha: _handoffString(json, 'targetSha', 'release platform bundle'),
      platform: platform,
      builderProfile: _handoffString(
        json,
        'builderProfile',
        'release platform bundle',
      ),
      archive: ReleaseHandoffFile.fromJson(json['archive'], label: 'archive'),
      canonicalEcosystemSha256: _handoffString(
        json,
        'canonicalEcosystemSha256',
        'release platform bundle',
      ),
      toolchains: {
        for (final entry in toolchainJson.entries)
          entry.key: _handoffStringValue(
            entry.value,
            'toolchains.${entry.key}',
          ),
      },
      platformToolchains: {
        for (final entry in platformToolchainJson.entries)
          entry.key: _handoffStringValue(
            entry.value,
            'platformToolchains.${entry.key}',
          ),
      },
      signing: ReleaseHandoffSigning.fromJson(json['signing']),
      validations: {
        for (final entry in validationJson.entries)
          entry.key: ReleaseHandoffValidation.fromJson(entry.value, entry.key),
      },
      qa: parseReleasePlatformQa(json['qa'], platform: platform),
    );
  }

  final String version;
  final String targetSha;
  final String platform;
  final String builderProfile;
  final ReleaseHandoffFile archive;
  final String canonicalEcosystemSha256;
  final Map<String, String> toolchains;
  final Map<String, String> platformToolchains;
  final ReleaseHandoffSigning signing;
  final Map<String, ReleaseHandoffValidation> validations;
  final Map<String, Object?> qa;

  Map<String, Object?> toJson() => {
    'schema': releasePlatformBundleSchema,
    'version': version,
    'targetSha': targetSha,
    'platform': platform,
    'builderProfile': builderProfile,
    'archive': archive.toJson(),
    'canonicalEcosystemSha256': canonicalEcosystemSha256,
    'toolchains': _sortedHandoffStringMap(toolchains),
    'platformToolchains': _sortedHandoffStringMap(platformToolchains),
    'signing': signing.toJson(),
    'validations': {
      for (final key in validations.keys.toList()..sort())
        key: validations[key]!.toJson(),
    },
    'qa': qa,
  };
}

class ReleaseHandoffPlatformReference {
  const ReleaseHandoffPlatformReference({
    required this.platform,
    required this.builderProfile,
    required this.manifest,
    required this.archive,
    required this.signing,
    required this.validations,
    required this.qaSha256,
  });

  factory ReleaseHandoffPlatformReference.fromJson(Object? value) {
    final json = _handoffObject(value, 'platform bundle reference');
    _requireHandoffKeys(json, const {
      'platform',
      'builderProfile',
      'manifest',
      'archive',
      'signing',
      'validations',
      'qaSha256',
    }, 'platform bundle reference');
    final validationJson = _handoffObject(
      json['validations'],
      'platform bundle reference validations',
    );
    return ReleaseHandoffPlatformReference(
      platform: _handoffString(json, 'platform', 'platform bundle reference'),
      builderProfile: _handoffString(
        json,
        'builderProfile',
        'platform bundle reference',
      ),
      manifest: ReleaseHandoffFile.fromJson(
        json['manifest'],
        label: 'platform bundle manifest',
      ),
      archive: ReleaseHandoffFile.fromJson(
        json['archive'],
        label: 'platform bundle archive',
      ),
      signing: ReleaseHandoffSigning.fromJson(json['signing']),
      validations: {
        for (final entry in validationJson.entries)
          entry.key: ReleaseHandoffValidation.fromJson(entry.value, entry.key),
      },
      qaSha256: _handoffString(json, 'qaSha256', 'platform bundle reference'),
    );
  }

  final String platform;
  final String builderProfile;
  final ReleaseHandoffFile manifest;
  final ReleaseHandoffFile archive;
  final ReleaseHandoffSigning signing;
  final Map<String, ReleaseHandoffValidation> validations;
  final String qaSha256;

  Map<String, Object?> toJson() => {
    'platform': platform,
    'builderProfile': builderProfile,
    'manifest': manifest.toJson(),
    'archive': archive.toJson(),
    'signing': signing.toJson(),
    'validations': {
      for (final key in validations.keys.toList()..sort())
        key: validations[key]!.toJson(),
    },
    'qaSha256': qaSha256,
  };
}

class ReleaseHandoffManifest {
  const ReleaseHandoffManifest({
    required this.version,
    required this.targetSha,
    required this.canonicalEcosystemSha256,
    required this.toolchains,
    required this.platformBundles,
  });

  factory ReleaseHandoffManifest.fromJson(Map<String, Object?> json) {
    _requireHandoffKeys(json, const {
      'schema',
      'version',
      'targetSha',
      'canonicalEcosystemSha256',
      'toolchains',
      'platformBundles',
    }, 'release handoff');
    if (json['schema'] != releaseHandoffSchema) {
      throw StateError('Release handoff schema must be $releaseHandoffSchema.');
    }
    final toolchainJson = _handoffObject(json['toolchains'], 'toolchains');
    final bundles = json['platformBundles'];
    if (bundles is! List<Object?>) {
      throw StateError('release handoff platformBundles must be a list.');
    }
    return ReleaseHandoffManifest(
      version: _handoffString(json, 'version', 'release handoff'),
      targetSha: _handoffString(json, 'targetSha', 'release handoff'),
      canonicalEcosystemSha256: _handoffString(
        json,
        'canonicalEcosystemSha256',
        'release handoff',
      ),
      toolchains: {
        for (final entry in toolchainJson.entries)
          entry.key: _handoffStringValue(
            entry.value,
            'toolchains.${entry.key}',
          ),
      },
      platformBundles: [
        for (final value in bundles)
          ReleaseHandoffPlatformReference.fromJson(value),
      ],
    );
  }

  final String version;
  final String targetSha;
  final String canonicalEcosystemSha256;
  final Map<String, String> toolchains;
  final List<ReleaseHandoffPlatformReference> platformBundles;

  Map<String, Object?> toJson() => {
    'schema': releaseHandoffSchema,
    'version': version,
    'targetSha': targetSha,
    'canonicalEcosystemSha256': canonicalEcosystemSha256,
    'toolchains': _sortedHandoffStringMap(toolchains),
    'platformBundles': [for (final bundle in platformBundles) bundle.toJson()],
  };
}

const releasePlatformBundleSchema = 'release-platform-bundle-v1';
const releaseHandoffSchema = 'release-handoff-v1';
const releaseHandoffFileName = 'release-handoff-v1.json';

String releasePlatformBundleFileName(String platform) =>
    'release-platform-bundle-v1-$platform.json';

Map<String, Object?> _handoffObject(Object? value, String label) {
  if (value is! Map) {
    throw StateError('$label must be a JSON object.');
  }
  return value.map((key, value) => MapEntry(key.toString(), value));
}

void _requireHandoffKeys(
  Map<String, Object?> json,
  Set<String> expected,
  String label,
) {
  final actual = json.keys.toSet();
  if (actual.length != expected.length || !actual.containsAll(expected)) {
    final missing = expected.difference(actual).toList()..sort();
    final extra = actual.difference(expected).toList()..sort();
    throw StateError(
      '$label has forbidden or missing fields. '
      'Missing: ${missing.join(', ')}. Extra: ${extra.join(', ')}.',
    );
  }
}

String _handoffString(Map<String, Object?> json, String key, String label) =>
    _handoffStringValue(json[key], '$label.$key');

String _handoffStringValue(Object? value, String label) {
  if (value is! String || value.isEmpty) {
    throw StateError('$label must be a non-empty string.');
  }
  return value;
}

int _handoffPositiveInt(Map<String, Object?> json, String key, String label) {
  final value = json[key];
  if (value is! int || value <= 0) {
    throw StateError('$label.$key must be a positive integer.');
  }
  return value;
}

bool _handoffBool(Map<String, Object?> json, String key, String label) {
  final value = json[key];
  if (value is! bool) {
    throw StateError('$label.$key must be a boolean.');
  }
  return value;
}

Map<String, String> _sortedHandoffStringMap(Map<String, String> source) => {
  for (final key in source.keys.toList()..sort()) key: source[key]!,
};
