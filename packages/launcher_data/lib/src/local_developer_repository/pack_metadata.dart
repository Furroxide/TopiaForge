part of '../local_developer_repository.dart';

extension _PackedManifestMetadata on LocalDeveloperRepository {
  ModManifest _manifestWithBuildMetadata(
    Directory root,
    Map<String, Object?> source, {
    Map<String, String> generatedHashes = const {},
    List<String>? synchronizedFiles,
  }) {
    final lock = _readSdkLock(root.path);
    final toolVersion = lock?.toolVersion.isNotEmpty == true
        ? lock!.toolVersion
        : _repositoryToolVersion();
    final gameVersion =
        lock?.gameVersion ?? TopiaForgeRuntimeVersions.gameVersion;
    final packed = <String, Object?>{
      ...source,
      'builtWith': {
        'sdkVersion': lock?.sdkVersion ?? TopiaForgeRuntimeVersions.sdkVersion,
        'loaderVersion': TopiaForgeRuntimeVersions.loaderVersion,
        'gameVersion': gameVersion,
        if (toolVersion.isNotEmpty) 'toolVersion': toolVersion,
      },
    };
    if (synchronizedFiles != null) {
      final sourceMultiplayer = source['multiplayer'];
      if (sourceMultiplayer is! Map) {
        throw StateError(
          'A session package must contain multiplayer manifest metadata.',
        );
      }
      packed['multiplayer'] = <String, Object?>{
        for (final entry in sourceMultiplayer.entries)
          entry.key.toString(): entry.value,
        'synchronizedFiles': List<String>.unmodifiable(synchronizedFiles),
      };
    }
    if (generatedHashes.isNotEmpty) {
      final hashes = <String, Object?>{};
      final sourceHashes = source['hashes'];
      if (sourceHashes is Map) {
        for (final entry in sourceHashes.entries) {
          hashes[entry.key.toString()] = entry.value;
        }
      }
      hashes.addAll(generatedHashes);
      packed['hashes'] = hashes;
    }
    return ModManifest.fromJson(packed);
  }
}
