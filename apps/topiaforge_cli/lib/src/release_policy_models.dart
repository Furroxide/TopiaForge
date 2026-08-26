part of 'release_policy.dart';

const releaseSupportedPlatforms = <String>[
  'linux-x64',
  'macos-universal',
  'windows-x64',
];

const releasePlatformArchives = <String, String>{
  'linux-x64': 'TopiaForge-linux-x64.zip',
  'macos-universal': 'TopiaForge-macos-universal.zip',
  'windows-x64': 'TopiaForge-windows-x64.zip',
};

const releasePlatformInstallLayouts = <String, String>{
  'linux-x64': 'portable-root',
  'macos-universal': 'app-bundle',
  'windows-x64': 'portable-root',
};

const releasePlatformGameArchives = <String, String>{
  'linux-x64': 'windows',
  'macos-universal': 'mac',
  'windows-x64': 'windows',
};

String releasePlatformForArchive(String archiveName) {
  for (final entry in releasePlatformArchives.entries) {
    if (entry.value == archiveName) return entry.key;
  }
  throw StateError('Unsupported release platform archive: $archiveName.');
}

String releaseArchiveForPlatform(String platform) =>
    releasePlatformArchives[platform] ??
    (throw StateError('Unsupported release platform: $platform.'));

class TopiaForgeReleasePolicy {
  TopiaForgeReleasePolicy._({
    required this.repositoryRoot,
    required this.toolchains,
    required this.gameBuildId,
    required this.gameBuildMetadataFile,
    required this.requireLatestGameBuild,
    required this.licenseExpression,
    required this.licenseFile,
    required this.licenseDecisionStatus,
    required this.provenanceFiles,
    required this.productVersion,
    required this.rollback,
    required this.platformArchives,
    required this.generatedMetadata,
    required this.windowsDistribution,
    required this.windowsCertificateSha256,
    required this.macosTeamId,
    required this.bepInExVersion,
    required this.bepInExProvenanceFile,
    required this.unityDoorstopVersion,
    required this.unityDoorstopCommit,
  });

  final String repositoryRoot;
  final Map<String, String> toolchains;
  final int gameBuildId;
  final String gameBuildMetadataFile;
  final bool requireLatestGameBuild;
  final String licenseExpression;
  final String? licenseFile;
  final String licenseDecisionStatus;
  final List<String> provenanceFiles;
  final String productVersion;
  final String rollback;
  final List<String> platformArchives;
  final List<String> generatedMetadata;

  /// `signed` or `unsigned`. Defaults to `signed` when the policy is silent, so
  /// an absent certificate can never be mistaken for a decision to ship without
  /// one — shipping unsigned has to be written down.
  final String windowsDistribution;

  final String windowsCertificateSha256;
  final String macosTeamId;
  final String bepInExVersion;
  final String bepInExProvenanceFile;
  final String unityDoorstopVersion;
  final String unityDoorstopCommit;

  static TopiaForgeReleasePolicy load(String repositoryRoot) {
    final file = File(p.join(repositoryRoot, 'release', 'release-policy.json'));
    final json = _readObject(file);
    if (json['schemaVersion'] != 2) {
      throw StateError('${file.path} must use schemaVersion 2.');
    }
    final toolchains = _stringMap(json['toolchains'], 'toolchains');
    final game = _object(json['gameBuild'], 'gameBuild');
    final license = _object(json['projectLicense'], 'projectLicense');
    final publication = _object(json['publication'], 'publication');
    const publicationFields = {
      'mode',
      'allowTagCreation',
      'allowAssetReplacement',
      'requireImmutableRelease',
      'publishAfterProtectedApproval',
    };
    if (publication.keys.length != publicationFields.length ||
        !publication.keys.toSet().containsAll(publicationFields)) {
      throw StateError(
        '${file.path} publication must contain only the protected immutable '
        'publication fields; code-signing exceptions are forbidden.',
      );
    }
    final versioning = _object(json['versioning'], 'versioning');
    final artifacts = _object(json['artifactPolicy'], 'artifactPolicy');
    final signingIdentities = _object(
      json['signingIdentities'],
      'signingIdentities',
    );
    final bepInEx = _object(json['bepInEx'], 'bepInEx');
    if (publication['mode'] != 'admin-staged-auto-publish' ||
        publication['allowTagCreation'] != false ||
        publication['allowAssetReplacement'] != false ||
        publication['requireImmutableRelease'] != true ||
        publication['publishAfterProtectedApproval'] != true) {
      throw StateError(
        '${file.path} must require an admin-staged, protected, immutable, '
        'non-clobbering automatic publication.',
      );
    }
    return TopiaForgeReleasePolicy._(
      repositoryRoot: repositoryRoot,
      toolchains: toolchains,
      gameBuildId: (game['id'] as num?)?.toInt() ?? 0,
      gameBuildMetadataFile: game['metadataFile'] as String? ?? '',
      requireLatestGameBuild: game['requireLatestAtRelease'] == true,
      licenseExpression: license['spdxExpression'] as String? ?? '',
      licenseFile: license['licenseFile'] as String?,
      licenseDecisionStatus: license['decisionStatus'] as String? ?? '',
      provenanceFiles: _stringList(
        json['thirdPartyProvenance'],
        'thirdPartyProvenance',
      ),
      productVersion: versioning['productVersion'] as String? ?? '',
      rollback: versioning['rollback'] as String? ?? '',
      platformArchives: _stringList(
        artifacts['platformArchives'],
        'platformArchives',
      ),
      generatedMetadata: _stringList(
        artifacts['generatedMetadata'],
        'generatedMetadata',
      ),
      windowsDistribution:
          signingIdentities['windowsDistribution'] as String? ?? 'signed',
      windowsCertificateSha256:
          signingIdentities['windowsCertificateSha256'] as String? ?? '',
      macosTeamId: signingIdentities['macosTeamId'] as String? ?? '',
      bepInExVersion: bepInEx['version'] as String? ?? '',
      bepInExProvenanceFile: bepInEx['provenanceFile'] as String? ?? '',
      unityDoorstopVersion: bepInEx['unityDoorstopVersion'] as String? ?? '',
      unityDoorstopCommit: bepInEx['unityDoorstopCommit'] as String? ?? '',
    );
  }

  bool get hasApprovedLicense =>
      licenseDecisionStatus == 'approved' &&
      SpdxExpressionValidator.validate(licenseExpression) == null &&
      licenseFile != null;

  List<String> get targetPlatforms {
    final selected = <String>{};
    for (final archive in platformArchives) {
      final platform = releasePlatformForArchive(archive);
      if (!selected.add(platform)) {
        throw StateError(
          'Release platform archives contain a duplicate target.',
        );
      }
    }
    return List.unmodifiable([
      for (final platform in releaseSupportedPlatforms)
        if (selected.contains(platform)) platform,
    ]);
  }

  bool get targetsWindows => targetPlatforms.contains('windows-x64');

  bool get targetsMacOS => targetPlatforms.contains('macos-universal');

  /// True when the policy explicitly records an unsigned Windows distribution.
  bool get distributesWindowsUnsigned => windowsDistribution == 'unsigned';

  bool get requiresWindowsSigningIdentity =>
      targetsWindows && !distributesWindowsUnsigned;

  bool get requiresMacOSSigningIdentity => targetsMacOS;

  bool get hasConfiguredWindowsSigningIdentity =>
      RegExp(r'^[0-9a-f]{64}$').hasMatch(windowsCertificateSha256) &&
      windowsCertificateSha256 !=
          '0000000000000000000000000000000000000000000000000000000000000000';

  bool get hasConfiguredMacOSSigningIdentity =>
      RegExp(r'^[A-Z0-9]{10}$').hasMatch(macosTeamId) &&
      macosTeamId != 'UNSETTEAM0';

  bool get hasConfiguredSigningIdentities =>
      (!requiresWindowsSigningIdentity ||
          hasConfiguredWindowsSigningIdentity) &&
      (!requiresMacOSSigningIdentity || hasConfiguredMacOSSigningIdentity);
}

class TopiaForgeReleaseCatalog {
  TopiaForgeReleaseCatalog._(this.releases);

  final List<TopiaForgeReleaseCatalogEntry> releases;

  static TopiaForgeReleaseCatalog load(String repositoryRoot) {
    final file = File(p.join(repositoryRoot, 'release', 'catalog.json'));
    final json = _readObject(file);
    if (json['schemaVersion'] != 3 || json['releases'] is! List) {
      throw StateError('${file.path} must contain a schemaVersion 3 catalog.');
    }
    final releases = <TopiaForgeReleaseCatalogEntry>[];
    final versions = <String>{};
    final tags = <String>{};
    for (final value in json['releases'] as List) {
      final entry = TopiaForgeReleaseCatalogEntry.fromJson(
        _object(value, 'release catalog entry'),
      );
      if (!versions.add(entry.version) || !tags.add(entry.tag)) {
        throw StateError('Release catalog versions and tags must be unique.');
      }
      releases.add(entry);
    }
    return TopiaForgeReleaseCatalog._(List.unmodifiable(releases));
  }

  TopiaForgeReleaseCatalogEntry release(String version) {
    return releases.firstWhere(
      (entry) => entry.version == version,
      orElse: () => throw StateError(
        'Release $version is not present in release/catalog.json.',
      ),
    );
  }
}

class TopiaForgeReleaseCatalogEntry {
  const TopiaForgeReleaseCatalogEntry({
    required this.version,
    required this.tag,
    required this.prerelease,
    required this.status,
    required this.notesFile,
    required this.components,
    required this.vpmPackages,
    required this.mods,
    required this.excludedDeveloperMods,
    required this.artifacts,
  });

  factory TopiaForgeReleaseCatalogEntry.fromJson(Map<String, Object?> json) {
    final prerelease = json['prerelease'];
    if (prerelease is! bool) {
      throw StateError('release catalog entry prerelease must be a boolean.');
    }
    return TopiaForgeReleaseCatalogEntry(
      version: json['version'] as String? ?? '',
      tag: json['tag'] as String? ?? '',
      prerelease: prerelease,
      status: json['status'] as String? ?? '',
      notesFile: json['notesFile'] as String? ?? '',
      components: _stringMap(json['components'], 'components'),
      vpmPackages: _stringMap(json['vpmPackages'], 'vpmPackages'),
      mods: _stringMap(json['mods'], 'mods'),
      excludedDeveloperMods: _stringMap(
        json['excludedDeveloperMods'],
        'excludedDeveloperMods',
      ),
      artifacts: _stringList(json['artifacts'], 'artifacts'),
    );
  }

  final String version;
  final String tag;
  final bool prerelease;
  final String status;
  final String notesFile;
  final Map<String, String> components;
  final Map<String, String> vpmPackages;
  final Map<String, String> mods;
  final Map<String, String> excludedDeveloperMods;
  final List<String> artifacts;
}
