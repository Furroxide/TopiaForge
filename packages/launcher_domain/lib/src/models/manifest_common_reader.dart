part of '../models.dart';

/// The field mapping every supported schema shares.
///
/// Kept beside the model rather than inside it so that adding a reader for a
/// later schema does not push [ModManifest] past the repository's 500-line cap,
/// and so a version's own field mapping can be read on its own. A field that
/// drifted between two hand-written readers of the same manifest would be a bug
/// no fixture would think to look for, so version-specific differences live in
/// the version's reader and nowhere else.
ModManifest _readCommonManifestFields(
  Map<String, Object?> json, {
  required ModContributions? contributions,
}) {
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
    contributions: contributions,
    apiAssemblies: _stringList(json['apiAssemblies']),
    multiplayer: ModMultiplayerMetadata.tryFromJson(json['multiplayer']),
    multiplayerIsPresent: json.containsKey('multiplayer'),
    structuralIssues: _manifestStructuralIssues(json),
    extraFields: Map<String, Object?>.unmodifiable(
      Map<String, Object?>.of(json)
        ..removeWhere((key, _) => _knownManifestJsonKeys.contains(key)),
    ),
  );
}
