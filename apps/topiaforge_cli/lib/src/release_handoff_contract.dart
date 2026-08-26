part of 'release_handoff.dart';

class _ReleaseHandoffContext {
  const _ReleaseHandoffContext({
    required this.policy,
    required this.release,
    required this.targetSha,
    required this.platformToolchains,
  });

  final TopiaForgeReleasePolicy policy;
  final TopiaForgeReleaseCatalogEntry release;
  final String targetSha;
  final Map<String, Map<String, String>> platformToolchains;

  List<String> get targetPlatforms => policy.targetPlatforms;
}

_ReleaseHandoffContext _loadHandoffContext(
  String repositoryRoot,
  String version,
  String targetSha,
) {
  if (!RegExp(r'^[0-9a-f]{40}$').hasMatch(targetSha)) {
    throw StateError('targetSha must be an exact lowercase 40-character SHA.');
  }
  final policy = TopiaForgeReleasePolicy.load(repositoryRoot);
  final release = TopiaForgeReleaseCatalog.load(
    repositoryRoot,
  ).release(version);
  if (policy.productVersion != version ||
      release.tag != 'v$version' ||
      release.version != version) {
    throw StateError(
      'Handoff version must exactly match release policy and catalog.',
    );
  }
  if (!_sameStringMap(policy.toolchains, _requiredToolchains)) {
    throw StateError('Release policy does not contain the pinned toolchains.');
  }
  final targetPlatforms = policy.targetPlatforms;
  final expectedArchives = policy.platformArchives.toSet();
  if (!release.artifacts.toSet().containsAll(expectedArchives)) {
    throw StateError('Release catalog omits a required platform archive.');
  }
  // Linux is descoped; see the note in release_policy.dart and P0-LINUX-01 in
  // docs/LaunchBlockers.md. This is a property of the product, not of one version.
  if (!_sameSet(targetPlatforms.toSet(), {'windows-x64'})) {
    throw StateError(
      'Release $version handoff requires signed Windows x64 only.',
    );
  }
  final platformToolchains = _loadPlatformToolchains(repositoryRoot);
  return _ReleaseHandoffContext(
    policy: policy,
    release: release,
    targetSha: targetSha,
    platformToolchains: platformToolchains,
  );
}

Map<String, Map<String, String>> _loadPlatformToolchains(
  String repositoryRoot,
) {
  final file = File(
    p.join(repositoryRoot, 'release', 'platform-toolchains.json'),
  );
  final json = readBoundedJsonObjectSync(
    file,
    maxBytes: CliFileLimits.metadata,
  );
  _requirePlatformPolicyKeys(json, const {
    'schemaVersion',
    'windows',
    'linux',
    'macos',
  }, 'platform toolchain policy');
  if (json['schemaVersion'] != 1) {
    throw StateError('Platform toolchain policy schemaVersion must be 1.');
  }
  const sectionFields = <String, Set<String>>{
    'windows': {'msvc', 'windowsSdk'},
    'linux': {
      'clang',
      'cmake',
      'ninja',
      'gtk',
      'proton',
      'executionEnvironment',
      'protonSteamAppId',
      'protonSteamDepotId',
      'protonSteamManifestId',
      'protonSteamBuildId',
      'protonSourceCommit',
    },
    'macos': {'xcode', 'macosSdk', 'appleClang'},
  };
  final sections = <String, Map<String, String>>{};
  for (final entry in sectionFields.entries) {
    final section = _platformPolicyObject(
      json[entry.key],
      'platform toolchain policy.${entry.key}',
    );
    _requirePlatformPolicyKeys(
      section,
      entry.value,
      'platform toolchain policy.${entry.key}',
    );
    sections[entry.key] = {
      for (final field in entry.value)
        field: _platformPolicyString(
          section[field],
          'platform toolchain policy.${entry.key}.$field',
        ),
    };
  }
  return {
    for (final entry in _platformToolchainSections.entries)
      entry.key: Map.unmodifiable(sections[entry.value]!),
  };
}

Map<String, Object?> _platformPolicyObject(Object? value, String label) {
  if (value is! Map) {
    throw StateError('$label must be a JSON object.');
  }
  return value.map((key, value) => MapEntry(key.toString(), value));
}

void _requirePlatformPolicyKeys(
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

String _platformPolicyString(Object? value, String label) {
  if (value is! String || value.isEmpty) {
    throw StateError('$label must be a non-empty string.');
  }
  return value;
}

void _validatePlatformBundle(
  ReleasePlatformBundle bundle,
  _ReleaseHandoffContext context, {
  String? expectedPlatform,
}) {
  _requirePlatform(bundle.platform, context.targetPlatforms);
  if (expectedPlatform != null && bundle.platform != expectedPlatform) {
    throw StateError('Platform bundle is for the wrong platform.');
  }
  if (bundle.version != context.release.version ||
      bundle.targetSha != context.targetSha) {
    throw StateError('Platform bundle version or target SHA does not match.');
  }
  _requireSha256(bundle.canonicalEcosystemSha256, 'canonicalEcosystemSha256');
  if (bundle.builderProfile != _builderProfiles[bundle.platform]) {
    throw StateError(
      '${bundle.platform} does not use its approved builder profile.',
    );
  }
  if (!_sameStringMap(bundle.toolchains, context.policy.toolchains)) {
    throw StateError(
      '${bundle.platform} toolchains do not exactly match the pins.',
    );
  }
  if (!_sameStringMap(
    bundle.platformToolchains,
    context.platformToolchains[bundle.platform]!,
  )) {
    throw StateError(
      '${bundle.platform} platformToolchains do not exactly match the '
      'platform pins.',
    );
  }
  final expectedArchive = releaseArchiveForPlatform(bundle.platform);
  if (bundle.archive.name != expectedArchive ||
      !context.release.artifacts.contains(expectedArchive)) {
    throw StateError('${bundle.platform} archive name is invalid.');
  }
  _requireSha256(bundle.archive.sha256, '${bundle.platform} archive sha256');
  final expectedSigning = _signingState(bundle.platform, context.policy);
  if (bundle.signing.scheme != expectedSigning.scheme ||
      bundle.signing.status != expectedSigning.status ||
      bundle.signing.notarization != expectedSigning.notarization ||
      bundle.signing.exceptionApplied != expectedSigning.exceptionApplied) {
    throw StateError(
      '${bundle.platform} signing state does not match release policy.',
    );
  }
  final requiredEvidence = _requiredEvidenceFor(
    bundle.platform,
    context.policy,
  );
  if (!_sameSet(bundle.validations.keys.toSet(), requiredEvidence)) {
    throw StateError(
      '${bundle.platform} validations must be exactly: '
      '${(requiredEvidence.toList()..sort()).join(', ')}.',
    );
  }
  for (final entry in bundle.validations.entries) {
    if (entry.value.status != 'passed') {
      throw StateError('${entry.key} validation did not pass.');
    }
    _requireSha256(entry.value.evidenceSha256, '${entry.key} evidenceSha256');
  }
  final ecosystemEvidence =
      bundle.validations['ecosystem-reproducibility']!.evidenceSha256;
  if (ecosystemEvidence != bundle.canonicalEcosystemSha256) {
    throw StateError(
      '${bundle.platform} ecosystem-reproducibility evidence must equal '
      'canonicalEcosystemSha256.',
    );
  }
  _validatePlatformQa(bundle, context);
}

void _validateHandoff(
  ReleaseHandoffManifest handoff,
  _ReleaseHandoffContext context,
) {
  if (handoff.version != context.release.version ||
      handoff.targetSha != context.targetSha) {
    throw StateError('Release handoff version or target SHA does not match.');
  }
  _requireSha256(handoff.canonicalEcosystemSha256, 'canonicalEcosystemSha256');
  if (!_sameStringMap(handoff.toolchains, context.policy.toolchains)) {
    throw StateError('Release handoff toolchains do not match the pins.');
  }
  final platforms = handoff.platformBundles
      .map((bundle) => bundle.platform)
      .toList();
  if (platforms.length != context.targetPlatforms.length ||
      !_sameSet(platforms.toSet(), context.targetPlatforms.toSet()) ||
      platforms.length != platforms.toSet().length) {
    throw StateError(
      'Release handoff must contain exactly the policy target platforms.',
    );
  }
  for (final reference in handoff.platformBundles) {
    _requireSha256(
      reference.qaSha256,
      '${reference.platform} handoff qaSha256',
    );
    if (reference.builderProfile != _builderProfiles[reference.platform]) {
      throw StateError(
        '${reference.platform} handoff builder profile is invalid.',
      );
    }
    final signing = _signingState(reference.platform, context.policy);
    if (reference.signing.scheme != signing.scheme ||
        reference.signing.status != signing.status ||
        reference.signing.notarization != signing.notarization ||
        reference.signing.exceptionApplied != signing.exceptionApplied) {
      throw StateError(
        '${reference.platform} handoff signing state is invalid.',
      );
    }
    final evidence = _requiredEvidenceFor(reference.platform, context.policy);
    if (!_sameSet(reference.validations.keys.toSet(), evidence)) {
      throw StateError(
        '${reference.platform} handoff validation set is invalid.',
      );
    }
    for (final validation in reference.validations.values) {
      if (validation.status != 'passed') {
        throw StateError(
          '${reference.platform} handoff contains a failed validation.',
        );
      }
      _requireSha256(
        validation.evidenceSha256,
        '${reference.platform} handoff evidenceSha256',
      );
    }
  }
  final reproducibilityDigests = handoff.platformBundles
      .map(
        (reference) =>
            reference.validations['ecosystem-reproducibility']!.evidenceSha256,
      )
      .toSet();
  if (reproducibilityDigests.length != 1 ||
      reproducibilityDigests.single != handoff.canonicalEcosystemSha256) {
    throw StateError(
      'Release handoff ecosystem reproducibility evidence must equal the '
      'canonical ecosystem digest.',
    );
  }
}

const _requiredToolchains = <String, String>{
  'dart': '3.12.2',
  'dotnetRuntime': '10.0.9',
  'dotnetSdk': '10.0.301',
  'flutter': '3.44.6',
  'node': '24.18.0',
  'unity': '6000.0.23f1',
};

const _builderProfiles = <String, String>{
  'windows-x64': 'admin-windows',
  'linux-x64': 'wsl2-ubuntu-24.04',
  'macos-universal': 'apple-silicon-macos-15-rosetta',
};

const _platformToolchainSections = <String, String>{
  'windows-x64': 'windows',
  'linux-x64': 'linux',
  'macos-universal': 'macos',
};

const _requiredEvidence = <String, Set<String>>{
  'windows-x64': {
    'authenticode',
    'ecosystem-reproducibility',
    'package',
    'robotopia',
    'toolchains',
    'unity',
  },
  'linux-x64': {'ecosystem-reproducibility', 'package', 'proton', 'toolchains'},
  'macos-universal': {
    'developer-id',
    'ecosystem-reproducibility',
    'notarization',
    'package',
    'toolchains',
    'universal',
  },
};

/// The signing state a platform bundle must declare.
///
/// Windows depends on the recorded distribution mode: an unsigned build has to
/// say so. Deriving this from the platform alone meant an unsigned release
/// would still have asserted `authenticode` / `verified` in its handoff, which
/// is worse than an absent field — it is an affirmative false provenance claim
/// in the document GitHub verifies before publishing.
ReleaseHandoffSigning _signingState(
  String platform,
  TopiaForgeReleasePolicy policy,
) => switch (platform) {
  'windows-x64' =>
    policy.distributesWindowsUnsigned
        ? const ReleaseHandoffSigning(
            scheme: 'not-applicable',
            status: 'unsigned',
            notarization: 'not-applicable',
            exceptionApplied: false,
          )
        : const ReleaseHandoffSigning(
            scheme: 'authenticode',
            status: 'verified',
            notarization: 'not-applicable',
            exceptionApplied: false,
          ),
  'linux-x64' => const ReleaseHandoffSigning(
    scheme: 'not-applicable',
    status: 'not-applicable',
    notarization: 'not-applicable',
    exceptionApplied: false,
  ),
  'macos-universal' => const ReleaseHandoffSigning(
    scheme: 'developer-id-application',
    status: 'verified',
    notarization: 'stapled-and-validated',
    exceptionApplied: false,
  ),
  _ => throw StateError('Unsupported release handoff platform: $platform.'),
};

/// The evidence keys a platform bundle must carry.
///
/// An unsigned Windows build produces no Authenticode evidence, so requiring
/// the key would have made the bundle unsatisfiable; leaving it required *and*
/// satisfied would have meant fabricating it.
Set<String> _requiredEvidenceFor(
  String platform,
  TopiaForgeReleasePolicy policy,
) {
  final evidence = _requiredEvidence[platform];
  if (evidence == null) {
    throw StateError('Unsupported release handoff platform: $platform.');
  }
  if (platform == 'windows-x64' && policy.distributesWindowsUnsigned) {
    return evidence.where((key) => key != 'authenticode').toSet();
  }
  return evidence;
}
