part of '../local_launcher_repository.dart';

/// Registry document parsing: turns a decoded TopiaForge index (`mods`) or
/// VPM-style listing (`packages`) into registry mods with resolved package
/// URLs. (Split from registry_source_helpers.dart for the 500-line file cap.)
extension _RegistrySourceParsing on LocalLauncherRepository {
  /// The index's current entries, and the history rows that carry a
  /// manifest. History never becomes a catalog tile: it only lets an exact
  /// older version be found and installed.
  _RegistrySourceMods _flatRegistryMods(
    Map<String, Object?> decoded,
    PackageSource source,
    Uri baseUri,
  ) {
    if (!decoded.containsKey('mods')) {
      return const _RegistrySourceMods(current: <RegistryMod>[]);
    }
    if (decoded['formatVersion'] != ModRegistryFormat.indexFormatVersion) {
      throw FormatException(
        'TopiaForge registry indexes must use formatVersion '
        '${ModRegistryFormat.indexFormatVersion}.',
      );
    }
    final entries = decoded['mods'];
    if (entries is! List) {
      throw const FormatException('Registry index mods must be a JSON array.');
    }
    final current = <RegistryMod>[];
    final history = <RegistryMod>[];
    for (final item in entries.whereType<Map>()) {
      final json = item.map((key, value) => MapEntry(key.toString(), value));
      final parsed = RegistryMod.fromJson(json);
      final localPath = json['localPath'] as String?;
      final packageBaseUri =
          localPath != null &&
              source.id == 'io.github.furroxide.topiaforge.local'
          ? Uri.file(_repositoryRoot.path)
          : baseUri;
      final downloadUrl = _resolvePackageUrl(
        parsed.downloadUrl.isNotEmpty ? parsed.downloadUrl : localPath ?? '',
        packageBaseUri,
      );
      current.add(
        RegistryMod(
          manifest: parsed.manifest,
          downloadUrl: downloadUrl,
          packageSha256: parsed.packageSha256,
          changelog: parsed.changelog,
          sourceId: source.id,
          sourceName: source.name,
        ),
      );
      history.addAll(_historyMods(json, parsed.manifest, source, baseUri));
    }
    return _RegistrySourceMods(current: current, history: history);
  }

  /// The history rows of one index entry that inline a manifest for this
  /// mod at the row's own version. A row that is malformed, names another
  /// mod or version, or has no safe package URL is left out instead of
  /// failing the source, as a launcher that ignores history would.
  List<RegistryMod> _historyMods(
    Map<String, Object?> entry,
    ModManifest latest,
    PackageSource source,
    Uri baseUri,
  ) {
    final rows = entry['history'];
    if (rows is! List) return const [];
    final mods = <RegistryMod>[];
    for (final row in rows.whereType<Map>()) {
      try {
        final ref = RegistryVersionRef.fromJson(
          row.map((key, value) => MapEntry(key.toString(), value)),
        );
        final manifest = ref.manifest;
        if (manifest == null ||
            manifest.id.toLowerCase() != latest.id.toLowerCase() ||
            manifest.version != ref.version ||
            ref.downloadUrl.trim().isEmpty) {
          continue;
        }
        final mod = RegistryMod(
          manifest: manifest,
          downloadUrl: _resolvePackageUrl(ref.downloadUrl, baseUri),
          packageSha256: ref.packageSha256,
          changelog: ref.changelog,
          sourceId: source.id,
          sourceName: source.name,
          packageBytes: ref.packageBytes,
        );
        if (_registryModTrustProblem(source, mod) == null) mods.add(mod);
      } on Object {
        continue;
      }
    }
    return mods;
  }

  List<RegistryMod> _vpmRegistryMods(
    Map<String, Object?> decoded,
    PackageSource source,
    Uri baseUri,
  ) {
    final packages = _objectMap(decoded['packages']);
    final mods = <RegistryMod>[];
    for (final packageEntry in packages.entries) {
      final packageId = packageEntry.key;
      final packageJson = _objectMap(packageEntry.value);
      final versions = _objectMap(packageJson['versions']);
      for (final versionEntry in versions.entries) {
        final versionJson = _objectMap(versionEntry.value);
        final manifestJson = _objectMap(versionJson['manifest']);
        final manifestSource = manifestJson.isEmpty
            ? _manifestFromRegistryVersion(
                packageId,
                packageJson,
                versionEntry.key,
                versionJson,
              )
            : manifestJson;
        final rawUrl =
            (versionJson['downloadUrl'] as String?) ??
            (versionJson['url'] as String?) ??
            (versionJson['zipUrl'] as String?) ??
            '';
        final sha =
            (versionJson['packageSha256'] as String?) ??
            (versionJson['sha256'] as String?) ??
            (versionJson['zipSHA256'] as String?) ??
            '';
        mods.add(
          RegistryMod(
            manifest: ModManifest.fromJson(manifestSource),
            downloadUrl: _resolvePackageUrl(rawUrl, baseUri),
            packageSha256: sha,
            changelog:
                (versionJson['changelog'] as String?) ??
                (versionJson['changelogUrl'] as String?) ??
                '',
            sourceId: source.id,
            sourceName: source.name,
          ),
        );
      }
    }
    return mods;
  }

  Map<String, Object?> _manifestFromRegistryVersion(
    String packageId,
    Map<String, Object?> packageJson,
    String version,
    Map<String, Object?> versionJson,
  ) {
    final manifest = <String, Object?>{
      ...versionJson,
      'schemaVersion':
          versionJson['schemaVersion'] ?? ModManifest.currentSchemaVersion,
      'name': versionJson['name'] ?? packageId,
      'displayName':
          versionJson['displayName'] ?? packageJson['displayName'] ?? packageId,
      'version': versionJson['version'] ?? version,
    };
    for (final field in const <String>{
      'manifest',
      'downloadUrl',
      'url',
      'zipUrl',
      'packageSha256',
      'sha256',
      'zipSHA256',
      'changelog',
      'changelogUrl',
    }) {
      manifest.remove(field);
    }
    return manifest;
  }

  String _resolvePackageUrl(String rawUrl, Uri baseUri) {
    if (rawUrl.trim().isEmpty) {
      return '';
    }
    final uri = Uri.tryParse(rawUrl);
    if (uri != null && uri.hasScheme) {
      if (isPublicHttpsUri(uri)) {
        return uri.toString();
      }
      if (uri.scheme == 'file' && baseUri.scheme == 'file') {
        return uri.toString();
      }
      throw StateError('Unsupported or unsafe package URL: $rawUrl');
    }
    if (baseUri.scheme == 'file') {
      return Uri.file(
        p.normalize(
          p.join(baseUri.toFilePath(windows: Platform.isWindows), rawUrl),
        ),
      ).toString();
    }
    final resolved = baseUri.resolve(rawUrl);
    if (!isPublicHttpsUri(resolved)) {
      throw StateError('Remote package URLs must resolve to HTTPS.');
    }
    return resolved.toString();
  }
}
