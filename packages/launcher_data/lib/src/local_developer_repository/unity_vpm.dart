part of '../local_developer_repository.dart';

/// Unity-side VPM: the launcher-driven resolver + listing/repo management. Reads a project's
/// `Packages/vpm-manifest.json`, resolves it against the subscribed listings (reusing the Unity-free
/// [UnityVpmResolver]), and downloads + extracts the resolved packages into `Packages/`. Mirrors the existing
/// `.robotopiamod` source model: the built-in listing is derived from `dist/vpm/index.json`, drift-proof.
extension LocalDeveloperUnityVpm on LocalDeveloperRepository {
  File get _vpmSourcesFile => File(p.join(_dataRoot.path, 'vpm_sources.json'));

  PackageSource _defaultVpmSource() => PackageSource(
    id: 'robotopia.vpm.local',
    name: 'QuantumWorks (local)',
    url: p.join(_repositoryRoot.path, 'dist', 'vpm', 'index.json'),
    builtIn: true,
  );

  Future<List<PackageSource>> _loadVpmSources() async {
    final defaultSource = _defaultVpmSource();
    final sources = <PackageSource>[];
    _recoverDeveloperAtomicBackupIfMissing(_vpmSourcesFile);
    if (_vpmSourcesFile.existsSync()) {
      try {
        final decoded = jsonDecode(
          utf8.decode(
            await _readDeveloperFileBounded(
              _vpmSourcesFile,
              maxBytes: _maxDeveloperCatalogBytes,
              label: 'VPM sources',
            ),
          ),
        );
        final list = decoded is Map ? decoded['sources'] : null;
        if (list is List) {
          sources.addAll(
            list.whereType<Map>().map(
              (item) => PackageSource.fromJson(item.cast<String, Object?>()),
            ),
          );
        }
      } on Object {
        // ignore a malformed file
      }
    }

    // Ensure the built-in listing is present and its url tracks the current repo (so a stale persisted path
    // can't pin a removed location).
    final builtInIndex = sources.indexWhere((s) => s.id == defaultSource.id);
    if (builtInIndex < 0) {
      sources.insert(0, defaultSource);
    } else {
      sources[builtInIndex] = sources[builtInIndex].copyWith(
        url: defaultSource.url,
      );
    }
    return sources;
  }

  Future<void> _saveVpmSources(List<PackageSource> sources) async {
    if (!_dataRoot.existsSync()) {
      _dataRoot.createSync(recursive: true);
    }
    final json = _prettyJson({
      'sources': sources.map((source) => source.toJson()).toList(),
    });
    _writeDeveloperTextAtomic(_vpmSourcesFile, json);
  }

  Future<List<PackageSource>> _addVpmSource(String url, String name) async {
    final trimmed = url.trim();
    if (trimmed.isEmpty) {
      throw StateError('A VPM repository url is required.');
    }
    final uri = Uri.tryParse(trimmed);
    if (!_isWindowsPathLike(trimmed) &&
        uri != null &&
        uri.hasScheme &&
        uri.scheme != 'https' &&
        uri.scheme != 'file') {
      throw StateError(
        'Unsupported VPM repository scheme: ${uri.scheme}. '
        'Use HTTPS or a local path.',
      );
    }
    if (uri?.scheme == 'https' &&
        (uri!.host.isEmpty || uri.userInfo.isNotEmpty)) {
      throw StateError(
        'VPM repository URLs must be absolute and contain no credentials.',
      );
    }
    final sources = await _loadVpmSources();
    // Content-derived id (sha256) avoids the collisions a 32-bit hashCode could produce — otherwise two distinct
    // urls could share an id and be mass-removed together.
    final id =
        'vpm.${sha256.convert(utf8.encode(trimmed)).toString().substring(0, 16)}';
    sources.removeWhere((s) => s.url == trimmed && !s.builtIn);
    sources.add(
      PackageSource(id: id, name: name.isEmpty ? trimmed : name, url: trimmed),
    );
    await _saveVpmSources(sources);
    return sources;
  }

  Future<List<PackageSource>> _removeVpmSource(String id) async {
    final sources = await _loadVpmSources();
    sources.removeWhere((s) => s.id == id && !s.builtIn);
    await _saveVpmSources(sources);
    return sources;
  }

  // Merges every enabled listing into one catalog (later sources can add versions, never override the built-in's).
  Future<VpmListing> _loadVpmListings() async {
    final merged = <String, Map<String, VpmPackageInfo>>{};
    for (final source in (await _loadVpmSources()).where((s) => s.enabled)) {
      try {
        final text = await _fetchVpmText(source.url);
        final decoded = jsonDecode(text) as Map<String, Object?>;
        _resolveVpmListingUrls(decoded, source.url);
        final listing = VpmListing.fromJson(decoded);
        listing.packages.forEach((id, versions) {
          final into = merged.putIfAbsent(id, () => <String, VpmPackageInfo>{});
          versions.forEach((ver, info) => into.putIfAbsent(ver, () => info));
        });
      } on Object {
        // A missing/unreadable listing (e.g. dist/vpm not built yet) simply contributes nothing.
      }
    }
    return VpmListing(name: 'merged', id: 'merged', packages: merged);
  }

  String _requireUnityProjectRoot(String projectPath) {
    final root = p.normalize(p.absolute(projectPath));
    if (!Directory(p.join(root, 'Packages')).existsSync()) {
      throw StateError('Not a Unity project (no Packages/ folder): $root');
    }
    return root;
  }

  VpmManifest _readVpmManifest(String root) {
    final file = File(p.join(root, 'Packages', 'vpm-manifest.json'));
    _recoverDeveloperAtomicBackupIfMissing(file);
    if (FileSystemEntity.typeSync(file.path, followLinks: false) ==
        FileSystemEntityType.notFound) {
      return const VpmManifest();
    }
    try {
      final bytes = _readDeveloperFileBoundedSync(
        file,
        maxBytes: _maxDeveloperManifestBytes,
        label: 'Packages/vpm-manifest.json',
      );
      final decoded = jsonDecode(utf8.decode(bytes, allowMalformed: false));
      if (decoded is! Map<String, Object?>) {
        throw const FormatException('root must be a JSON object');
      }
      _validateVpmManifestShape(decoded);
      return VpmManifest.fromJson(decoded);
    } on FormatException catch (error) {
      throw StateError('Invalid Packages/vpm-manifest.json: ${error.message}.');
    }
  }

  void _validateVpmManifestShape(Map<String, Object?> json) {
    _validateVpmStringMap(json['dependencies'], 'dependencies');
    final locked = json['locked'];
    if (locked == null) {
      return;
    }
    if (locked is! Map) {
      throw const FormatException('"locked" must be a JSON object');
    }
    for (final entry in locked.entries) {
      if (entry.key is! String || entry.value is! Map) {
        throw const FormatException(
          'each "locked" entry must be a JSON object',
        );
      }
      final packageId = entry.key as String;
      final diagnosticId = packageId.length <= 80
          ? packageId
          : '${packageId.substring(0, 80)}…';
      final value = entry.value as Map;
      final version = value['version'];
      if (version is! String || version.trim().isEmpty) {
        throw FormatException(
          'locked package "$diagnosticId" must have an exact version',
        );
      }
      _validateVpmStringMap(
        value['dependencies'],
        'locked package "$diagnosticId" dependencies',
      );
    }
  }

  void _validateVpmStringMap(Object? value, String label) {
    if (value == null) {
      return;
    }
    if (value is! Map) {
      throw FormatException('"$label" must be a JSON object');
    }
    for (final entry in value.entries) {
      if (entry.key is! String ||
          entry.value is! String ||
          (entry.value as String).trim().isEmpty) {
        throw FormatException('"$label" values must be non-empty strings');
      }
    }
  }

  void _writeVpmManifest(String root, VpmManifest manifest) {
    final dir = Directory(p.join(root, 'Packages'))
      ..createSync(recursive: true);
    _writeDeveloperTextAtomic(
      File(p.join(dir.path, 'vpm-manifest.json')),
      _prettyJson(manifest.toJson()),
    );
  }

  String _installedVpmVersion(String root, String id) {
    final file = File(p.join(root, 'Packages', id, 'package.json'));
    try {
      final bytes = _readDeveloperFileBoundedSync(
        file,
        maxBytes: _maxDeveloperManifestBytes,
        label: 'VPM package $id package.json',
      );
      final decoded = jsonDecode(utf8.decode(bytes, allowMalformed: false));
      if (decoded is Map &&
          decoded['name'] == id &&
          decoded['version'] is String &&
          (decoded['version'] as String).trim().isNotEmpty) {
        return decoded['version'] as String;
      }
    } on Object {
      // ignore
    }
    return '';
  }

  Future<List<VpmResolvedPackage>> _resolveUnityProject(
    String projectPath, {
    required bool restore,
  }) async {
    final root = _requireUnityProjectRoot(projectPath);
    final manifest = _readVpmManifest(root);
    final catalog = await _loadVpmListings();
    final resolution = const UnityVpmResolver().resolve(
      manifest: manifest,
      catalog: catalog,
    );
    final blocking = resolution.issues
        .where((issue) => issue.isBlocking)
        .toList();
    if (blocking.isNotEmpty) {
      throw StateError(blocking.map((issue) => issue.message).join(' '));
    }

    final resolvedVersions = {
      for (final package in resolution.packages) package.id: package.version,
    };
    final locked = {
      for (final package in resolution.packages)
        package.id: VpmLocked(
          version: package.version,
          dependencies: {
            for (final dep in package.dependencies)
              if (resolvedVersions.containsKey(dep))
                dep: resolvedVersions[dep]!,
          },
        ),
    };
    final updatedManifest = manifest.copyWith(locked: locked);
    if (restore) {
      await _restoreVpmPackages(
        root,
        manifest,
        resolution.packages,
        updatedManifest,
      );
    } else {
      _writeVpmManifest(root, updatedManifest);
    }
    return resolution.packages;
  }

  Future<List<VpmResolvedPackage>> _addUnityPackage(
    String projectPath,
    String id,
    String range,
  ) async {
    final root = _requireUnityProjectRoot(projectPath);
    final manifestFile = File(p.join(root, 'Packages', 'vpm-manifest.json'));
    final before = _DeveloperFileSnapshot.capture(manifestFile);
    final manifest = _readVpmManifest(root);
    final dependencies = {
      ...manifest.dependencies,
      id: range.trim().isEmpty ? '*' : range.trim(),
    };
    try {
      _writeVpmManifest(root, manifest.copyWith(dependencies: dependencies));
      return await _resolveUnityProject(root, restore: true);
    } on Object {
      before.restore();
      rethrow;
    }
  }

  Future<List<VpmResolvedPackage>> _removeUnityPackage(
    String projectPath,
    String id,
  ) async {
    final root = _requireUnityProjectRoot(projectPath);
    final manifestFile = File(p.join(root, 'Packages', 'vpm-manifest.json'));
    final before = _DeveloperFileSnapshot.capture(manifestFile);
    final manifest = _readVpmManifest(root);
    final dependencies = {...manifest.dependencies}..remove(id);
    try {
      _writeVpmManifest(root, manifest.copyWith(dependencies: dependencies));
      return await _resolveUnityProject(root, restore: true);
    } on Object {
      before.restore();
      rethrow;
    }
  }

  Future<List<VpmPackageInfo>> _listAvailableUnityPackages() async {
    final catalog = await _loadVpmListings();
    final result = <VpmPackageInfo>[];
    catalog.packages.forEach((id, versions) {
      VpmPackageInfo? latest;
      for (final info in versions.values) {
        if (latest == null ||
            _vpmVersionGreater(info.version, latest.version)) {
          latest = info;
        }
      }
      if (latest != null) {
        result.add(latest);
      }
    });
    result.sort((a, b) => a.name.compareTo(b.name));
    return result;
  }

  bool _vpmVersionGreater(String a, String b) {
    final va = SemanticVersion.tryParse(a);
    final vb = SemanticVersion.tryParse(b);
    if (va == null) return false;
    if (vb == null) return true;
    return va.compareTo(vb) > 0;
  }

  Future<String> _fetchVpmText(String url) async => utf8.decode(
    await _fetchVpmBytes(url, maxBytes: _maxDeveloperCatalogBytes),
  );

  void _resolveVpmListingUrls(
    Map<String, Object?> listingJson,
    String sourceUrl,
  ) {
    final packages = listingJson['packages'];
    if (packages is! Map) {
      return;
    }
    for (final packageValue in packages.values) {
      if (packageValue is! Map) {
        continue;
      }
      final versions = packageValue['versions'];
      if (versions is! Map) {
        continue;
      }
      for (final versionValue in versions.values) {
        if (versionValue is! Map) {
          continue;
        }
        final rawUrl = versionValue['url'];
        if (rawUrl is String) {
          versionValue['url'] = _resolveVpmPackageUrl(rawUrl, sourceUrl);
        }
      }
    }
  }
}
