part of 'release_policy.dart';

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
    required this.bepInExVersion,
    required this.bepInExProvenanceFile,
    required this.unityDoorstopVersion,
    required this.unityDoorstopCommit,
    required this.codeSigningException,
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
  final String bepInExVersion;
  final String bepInExProvenanceFile;
  final String unityDoorstopVersion;
  final String unityDoorstopCommit;
  final ReleaseCodeSigningException? codeSigningException;

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
    final exceptionValue = publication['codeSigningException'];
    final codeSigningException = exceptionValue == null
        ? null
        : ReleaseCodeSigningException.fromJson(
            _object(exceptionValue, 'publication.codeSigningException'),
          );
    final versioning = _object(json['versioning'], 'versioning');
    final artifacts = _object(json['artifactPolicy'], 'artifactPolicy');
    final bepInEx = _object(json['bepInEx'], 'bepInEx');
    if (publication['mode'] != 'draft-only' ||
        publication['allowTagCreation'] != false ||
        publication['allowAssetReplacement'] != false ||
        publication['requireImmutableReleasesBeforeManualPublish'] != true) {
      throw StateError(
        '${file.path} must remain draft-only, immutable, and non-clobbering.',
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
      bepInExVersion: bepInEx['version'] as String? ?? '',
      bepInExProvenanceFile: bepInEx['provenanceFile'] as String? ?? '',
      unityDoorstopVersion: bepInEx['unityDoorstopVersion'] as String? ?? '',
      unityDoorstopCommit: bepInEx['unityDoorstopCommit'] as String? ?? '',
      codeSigningException: codeSigningException,
    );
  }

  bool get hasApprovedLicense =>
      licenseDecisionStatus == 'approved' &&
      SpdxExpressionValidator.validate(licenseExpression) == null &&
      licenseFile != null;

  bool get allowsUnsignedWindows =>
      codeSigningException?.allowsUnsignedWindowsFor(productVersion) ?? false;

  bool get allowsAdHocMacOS =>
      codeSigningException?.allowsAdHocMacOSFor(productVersion) ?? false;
}

class ReleaseCodeSigningException {
  const ReleaseCodeSigningException({
    required this.version,
    required this.allowUnsignedWindows,
    required this.allowAdHocMacOS,
  });

  factory ReleaseCodeSigningException.fromJson(Map<String, Object?> json) =>
      ReleaseCodeSigningException(
        version: json['version'] as String? ?? '',
        allowUnsignedWindows: json['allowUnsignedWindows'] == true,
        allowAdHocMacOS: json['allowAdHocMacOS'] == true,
      );

  final String version;
  final bool allowUnsignedWindows;
  final bool allowAdHocMacOS;

  bool allowsUnsignedWindowsFor(String productVersion) =>
      version == '1.0.0-rc.1' &&
      productVersion == version &&
      allowUnsignedWindows;

  bool allowsAdHocMacOSFor(String productVersion) =>
      version == '1.0.0-rc.1' && productVersion == version && allowAdHocMacOS;
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
