part of '../local_developer_repository.dart';

final class WorldBundleAttestation {
  const WorldBundleAttestation({
    required this.bundlePath,
    required this.sha256,
    required this.sizeBytes,
  });

  final String bundlePath;
  final String sha256;
  final int sizeBytes;
}

extension LocalDeveloperWorldBundleAttestation on LocalDeveloperRepository {
  WorldBundleAttestation attestWorldBundleOutput({
    required String modPath,
    required String bundleName,
    required String worldPrefab,
  }) {
    final safeBundleName = _requireDeveloperPackageSegment(
      bundleName,
      label: 'World bundle name',
    );
    if (safeBundleName != bundleName) {
      throw StateError('World bundle name is not portable.');
    }
    final assetDirectory = Directory(p.join(modPath, 'AssetBundles'));
    if (FileSystemEntity.typeSync(assetDirectory.path, followLinks: false) !=
        FileSystemEntityType.directory) {
      throw StateError('World AssetBundles output is not a regular directory.');
    }
    final bundle = File(p.join(assetDirectory.path, '$safeBundleName.bundle'));
    final manifest = File(
      p.join(assetDirectory.path, '$safeBundleName.manifest.json'),
    );
    final bundleBytes = _readDeveloperFileBoundedSync(
      bundle,
      maxBytes: _maxDeveloperArchiveBytes,
      label: 'World bundle',
    );
    if (bundleBytes.isEmpty) throw StateError('World bundle is empty.');
    final manifestBytes = _readDeveloperFileBoundedSync(
      manifest,
      maxBytes: _maxDeveloperManifestBytes,
      label: 'World bundle manifest',
    );
    final decoded = jsonDecode(
      utf8.decode(manifestBytes, allowMalformed: false),
    );
    if (decoded is! Map) {
      throw StateError('World bundle manifest must be a JSON object.');
    }
    final json = decoded.map((key, value) => MapEntry(key.toString(), value));
    final expectedBundle = '$safeBundleName.bundle';
    if (json['bundle'] != expectedBundle ||
        json['worldPrefab'] != worldPrefab ||
        json['editorVersion'] !=
            RobotopiaUnityCompatibility.requiredEditorVersion) {
      throw StateError('World bundle provenance does not match build inputs.');
    }
    final assets = json['assets'];
    if (assets is! List ||
        assets.isEmpty ||
        assets.any((item) => item is! String) ||
        !assets.contains(worldPrefab)) {
      throw StateError('World bundle provenance has an invalid asset list.');
    }
    final assetPaths = assets.cast<String>();
    final sorted = [...assetPaths]..sort();
    if (assetPaths.toSet().length != assetPaths.length ||
        !_sameStringLists(assetPaths, sorted) ||
        assetPaths.any(
          (asset) =>
              !asset.startsWith('Assets/') ||
              _portableDeveloperArchivePath(
                    asset,
                    label: 'World bundle asset',
                  ) !=
                  asset,
        )) {
      throw StateError('World bundle provenance assets are inconsistent.');
    }
    final digest = sha256.convert(bundleBytes).toString();
    if (json['sha256'] != digest) {
      throw StateError('World bundle provenance SHA-256 does not match.');
    }
    return WorldBundleAttestation(
      bundlePath: bundle.absolute.path,
      sha256: digest,
      sizeBytes: bundleBytes.length,
    );
  }
}

bool _sameStringLists(List<String> left, List<String> right) {
  if (left.length != right.length) return false;
  for (var index = 0; index < left.length; index++) {
    if (left[index] != right[index]) return false;
  }
  return true;
}
