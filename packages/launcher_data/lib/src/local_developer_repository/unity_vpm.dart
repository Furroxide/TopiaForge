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
    if (_vpmSourcesFile.existsSync()) {
      try {
        final decoded = jsonDecode(await _vpmSourcesFile.readAsString());
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
    final temp = File('${_vpmSourcesFile.path}.tmp');
    await temp.writeAsString(json);
    if (_vpmSourcesFile.existsSync()) {
      await _vpmSourcesFile.delete();
    }
    await temp.rename(_vpmSourcesFile.path);
  }

  Future<List<PackageSource>> _addVpmSource(String url, String name) async {
    final trimmed = url.trim();
    if (trimmed.isEmpty) {
      throw StateError('A VPM repository url is required.');
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
    if (!file.existsSync()) {
      return const VpmManifest();
    }
    try {
      return VpmManifest.fromJson(
        jsonDecode(file.readAsStringSync()) as Map<String, Object?>,
      );
    } on Object {
      return const VpmManifest();
    }
  }

  void _writeVpmManifest(String root, VpmManifest manifest) {
    final dir = Directory(p.join(root, 'Packages'))
      ..createSync(recursive: true);
    File(
      p.join(dir.path, 'vpm-manifest.json'),
    ).writeAsStringSync(_prettyJson(manifest.toJson()));
  }

  String _installedVpmVersion(String root, String id) {
    final file = File(p.join(root, 'Packages', id, 'package.json'));
    if (!file.existsSync()) {
      return '';
    }
    try {
      final decoded = jsonDecode(file.readAsStringSync());
      if (decoded is Map && decoded['version'] is String) {
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

    if (restore) {
      // Prune packages that were managed (previously locked) but are no longer in the resolution.
      final resolvedIds = resolution.packages.map((pkg) => pkg.id).toSet();
      for (final lockedId in manifest.locked.keys) {
        if (!resolvedIds.contains(lockedId)) {
          final orphan = Directory(p.join(root, 'Packages', lockedId));
          if (orphan.existsSync()) {
            orphan.deleteSync(recursive: true);
          }
        }
      }

      for (final package in resolution.packages) {
        if (_installedVpmVersion(root, package.id) == package.version) {
          continue; // already at the resolved version
        }
        if (package.url.isEmpty) {
          continue;
        }
        final bytes = await _fetchVpmBytes(package.url);
        if (package.zipSha256.isNotEmpty) {
          final actual = sha256.convert(bytes).toString().toLowerCase();
          if (actual != package.zipSha256.toLowerCase()) {
            throw StateError(
              'SHA-256 mismatch for ${package.id} ${package.version}.',
            );
          }
        }
        final target = Directory(p.join(root, 'Packages', package.id));
        if (target.existsSync()) {
          target.deleteSync(recursive: true);
        }
        _extractVpmZip(bytes, target);
      }

      // Point the embedded resolver at the same listings so a cloned copy self-heals.
      final repos = (await _loadVpmSources())
          .where((s) => s.enabled)
          .map((s) => s.url)
          .toList();
      File(
        p.join(root, 'Packages', 'vpm-resolver-repos.json'),
      ).writeAsStringSync(_prettyJson(repos));
    }

    // Write resolved (locked) versions back into the manifest.
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
    _writeVpmManifest(root, manifest.copyWith(locked: locked));
    return resolution.packages;
  }

  Future<List<VpmResolvedPackage>> _addUnityPackage(
    String projectPath,
    String id,
    String range,
  ) async {
    final root = _requireUnityProjectRoot(projectPath);
    final manifest = _readVpmManifest(root);
    final dependencies = {
      ...manifest.dependencies,
      id: range.trim().isEmpty ? '*' : range.trim(),
    };
    _writeVpmManifest(root, manifest.copyWith(dependencies: dependencies));
    return _resolveUnityProject(root, restore: true);
  }

  Future<List<VpmResolvedPackage>> _removeUnityPackage(
    String projectPath,
    String id,
  ) async {
    final root = _requireUnityProjectRoot(projectPath);
    final manifest = _readVpmManifest(root);
    final dependencies = {...manifest.dependencies}..remove(id);
    final locked = {...manifest.locked}..remove(id);
    _writeVpmManifest(
      root,
      manifest.copyWith(dependencies: dependencies, locked: locked),
    );
    final installed = Directory(p.join(root, 'Packages', id));
    if (installed.existsSync()) {
      installed.deleteSync(recursive: true);
    }
    // Re-resolve WITH restore so a still-required transitive dependency is re-extracted (its folder was just
    // deleted if it was the removed package) and orphaned packages are pruned.
    return _resolveUnityProject(root, restore: true);
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

  Future<String> _fetchVpmText(String url) async =>
      utf8.decode(await _fetchVpmBytes(url));

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

  String _resolveVpmPackageUrl(String rawUrl, String sourceUrl) {
    final trimmed = rawUrl.trim();
    if (trimmed.isEmpty || p.isAbsolute(trimmed)) {
      return trimmed;
    }
    final uri = Uri.tryParse(trimmed);
    if (uri != null && uri.hasScheme) {
      return trimmed;
    }

    final source = sourceUrl.trim();
    if (source.startsWith('http://') || source.startsWith('https://')) {
      return Uri.parse(source).resolve(trimmed).toString();
    }
    final sourcePath = source.startsWith('file://')
        ? Uri.parse(source).toFilePath(windows: Platform.isWindows)
        : source;
    return p.normalize(p.join(p.dirname(sourcePath), trimmed));
  }

  Future<List<int>> _fetchVpmBytes(String url) async {
    final trimmed = url.trim();
    if (trimmed.startsWith('http://') || trimmed.startsWith('https://')) {
      final client = HttpClient();
      try {
        final request = await client.getUrl(Uri.parse(trimmed));
        final response = await request.close();
        if (response.statusCode != 200) {
          throw StateError('HTTP ${response.statusCode} for $trimmed');
        }
        final bytes = <int>[];
        await for (final chunk in response) {
          bytes.addAll(chunk);
        }
        return bytes;
      } finally {
        client.close(force: true);
      }
    }
    final path = trimmed.startsWith('file://')
        ? Uri.parse(trimmed).toFilePath()
        : trimmed;
    return File(path).readAsBytesSync();
  }

  // Scaffolds a com.* VPM package from templates/Robotopia.UnityPackageTemplate, stamping the chosen id + name
  // into package.json. The package-maker (vpm-package-maker analog).
  Future<String> _createUnityPackage(
    String parentDirectory,
    String id,
    String name,
  ) async {
    final templateDir = Directory(
      p.join(
        _repositoryRoot.path,
        'templates',
        'Robotopia.UnityPackageTemplate',
      ),
    );
    if (!templateDir.existsSync()) {
      throw StateError(
        'Unity package template not found at ${templateDir.path}.',
      );
    }
    final root = Directory(p.join(parentDirectory, _safeName(id)));
    if (root.existsSync()) {
      throw StateError('Package already exists: ${root.path}');
    }
    _copyDirectory(templateDir, root);

    // Stamp the id + displayName into package.json.
    final packageFile = File(p.join(root.path, 'package.json'));
    if (packageFile.existsSync()) {
      try {
        final json =
            jsonDecode(packageFile.readAsStringSync()) as Map<String, Object?>;
        json['name'] = id;
        json['displayName'] = name.isEmpty ? id : name;
        packageFile.writeAsStringSync(_prettyJson(json));
      } on Object {
        // ignore — leave the template values
      }
    }
    return root.path;
  }

  void _extractVpmZip(List<int> bytes, Directory target) {
    target.createSync(recursive: true);
    final archive = ZipDecoder().decodeBytes(bytes);
    for (final file in archive.files) {
      // Reject any path-traversal segment outright (defense-in-depth alongside the isWithin check below).
      if (p.split(file.name).any((segment) => segment == '..')) {
        throw StateError(
          'Zip entry has a path-traversal segment: ${file.name}',
        );
      }
      final outputPath = p.normalize(p.join(target.path, file.name));
      // Zip-slip guard: never write outside the target.
      if (!p.isWithin(target.path, outputPath)) {
        throw StateError('Zip entry escapes the target: ${file.name}');
      }
      if (file.isFile) {
        File(outputPath)
          ..createSync(recursive: true)
          ..writeAsBytesSync(file.content as List<int>);
      } else {
        Directory(outputPath).createSync(recursive: true);
      }
    }
  }
}
