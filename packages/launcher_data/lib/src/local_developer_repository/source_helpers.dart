part of '../local_developer_repository.dart';

extension LocalDeveloperSourceHelpers on LocalDeveloperRepository {
  /// Registry mods from configured sources, or the bundled local source.
  /// Failed sources mirror [resolveDeveloperProject]'s non-blocking behavior.
  /// Deliberately not on [DeveloperRepository] yet — the CLI consumes the
  /// concrete type, and widening the interface breaks external fakes.
  Future<List<RegistryMod>> loadConfiguredRegistryMods({
    String? projectPath,
  }) async {
    final root = _findProjectRoot(projectPath ?? Directory.current.path);
    var sources = [_localSource()];
    if (root != null) {
      final project = await _readProject(root.path);
      if (project.packageSources.isNotEmpty) {
        sources = project.packageSources;
      }
    }
    return (await _loadRegistryModsGuarded(sources)).mods;
  }

  PackageSource _localSource() {
    return PackageSource(
      id: 'io.github.furroxide.topiaforge.local',
      name: 'Bundled Local Packages',
      // Derived from the built .topiaforgemod packages in dist/, not a hand-maintained file.
      url: Uri.file(p.join(_repositoryRoot.path, 'dist')).toString(),
      builtIn: true,
    );
  }

  /// Loads every enabled source, degrading a failed source to a non-blocking
  /// issue instead of failing the whole resolve — an offline machine must
  /// still resolve against the bundled local packages. Failed built-ins
  /// (e.g. the official registry before its first deploy, or with no
  /// network) are info-level; a user-added source that fails is a warning.
  Future<({List<RegistryMod> mods, List<LauncherIssue> issues})>
  _loadRegistryModsGuarded(List<PackageSource> sources) async {
    final mods = <RegistryMod>[];
    final issues = <LauncherIssue>[];
    for (final source in sources.where((item) => item.enabled)) {
      try {
        mods.addAll(await _loadRegistrySource(source));
      } on Object catch (error) {
        issues.add(
          LauncherIssue(
            severity: source.builtIn
                ? IssueSeverity.info
                : IssueSeverity.warning,
            subjectId: source.id,
            message:
                'Package source ${source.id} is unavailable ($error); '
                'resolution used the remaining sources.',
          ),
        );
      }
    }
    return (mods: mods, issues: issues);
  }

  Future<List<RegistryMod>> _loadRegistrySource(PackageSource source) async {
    // A local source can point at a DIRECTORY of .topiaforgemod packages: derive the catalog
    // straight from the packages (manifest + sha read from each file) so there is no separate
    // metadata file that can drift out of sync with the packages on disk.
    final directory = _resolveDirectorySource(source);
    if (directory != null) {
      return _packagesInDirectory(directory, source);
    }

    final document = await _readSourceDocument(source);
    final decoded = jsonDecode(document.content) as Map<String, Object?>;
    final mods = [
      ..._flatRegistryMods(decoded, source, document.baseUri),
      ..._packageRegistryMods(decoded, source, document.baseUri),
    ];
    _validateRegistryTrust(source, mods);
    return mods;
  }

  void _validateRegistryTrust(PackageSource source, List<RegistryMod> mods) {
    final remoteSource = source.url.trim().toLowerCase().startsWith('https://');
    for (final mod in mods) {
      final uri = Uri.tryParse(mod.downloadUrl.trim());
      if (uri == null || !uri.hasScheme) {
        throw StateError('${mod.manifest.id} has no absolute package URL.');
      }
      if (uri.scheme == 'file') {
        if (remoteSource) {
          throw StateError(
            '${mod.manifest.id} from a remote source points at a local file.',
          );
        }
        continue;
      }
      if (!isPublicHttpsUri(uri)) {
        throw StateError('${mod.manifest.id} package URL must use HTTPS.');
      }
      if (!RegExp(r'^[0-9a-fA-F]{64}$').hasMatch(mod.packageSha256.trim())) {
        throw StateError('${mod.manifest.id} requires a valid SHA-256 hash.');
      }
    }
  }

  Directory? _resolveDirectorySource(PackageSource source) {
    final uri = Uri.tryParse(source.url);
    String? path;
    if (_isWindowsPathLike(source.url)) {
      // A drive-letter path parses as URI scheme "c"; treat it as a path.
      path = source.url;
    } else if (uri != null && uri.scheme == 'file') {
      path = uri.toFilePath(windows: Platform.isWindows);
    } else if (uri == null || !uri.hasScheme) {
      path = source.url;
    }
    if (path == null) {
      return null;
    }

    final type = FileSystemEntity.typeSync(path);
    if (type == FileSystemEntityType.directory) {
      return Directory(path);
    }
    if (type == FileSystemEntityType.notFound && p.extension(path).isEmpty) {
      return Directory(path);
    }
    return null;
  }

  Future<List<RegistryMod>> _packagesInDirectory(
    Directory directory,
    PackageSource source,
  ) async {
    if (!directory.existsSync()) {
      return const [];
    }

    final latestById = <String, RegistryMod>{};
    final packageFiles = directory.listSync().whereType<File>().where(
      (file) => file.path.toLowerCase().endsWith('.topiaforgemod'),
    );
    for (final file in packageFiles) {
      try {
        final package = await _readPackage(file.path, expectedSha256: '');
        final id = package.manifest.id.toLowerCase();
        final existing = latestById[id];
        if (existing != null &&
            !_isNewerVersion(
              package.manifest.version,
              existing.manifest.version,
            )) {
          continue;
        }
        latestById[id] = RegistryMod(
          manifest: package.manifest,
          downloadUrl: Uri.file(file.path).toString(),
          packageSha256: package.sha256Hex,
          sourceId: source.id,
          sourceName: source.name,
        );
      } on Object catch (_) {
        // Skip a malformed package rather than failing the whole catalog load.
      }
    }
    return latestById.values.toList();
  }

  bool _isNewerVersion(String candidate, String current) {
    final candidateVersion = SemanticVersion.tryParse(candidate);
    final currentVersion = SemanticVersion.tryParse(current);
    if (candidateVersion == null) {
      return false;
    }
    if (currentVersion == null) {
      return true;
    }
    return candidateVersion.compareTo(currentVersion) > 0;
  }

  bool _isWindowsPathLike(String value) {
    return RegExp(r'^[A-Za-z]:[\\/]').hasMatch(value) ||
        value.startsWith(r'\\');
  }

  Future<_SourceDocument> _readSourceDocument(PackageSource source) async {
    if (_isWindowsPathLike(source.url)) {
      final bytes = await _readDeveloperFileBounded(
        File(source.url),
        maxBytes: _maxDeveloperCatalogBytes,
        label: 'Package source',
      );
      return _SourceDocument(
        content: utf8.decode(bytes),
        baseUri: Uri.file(p.dirname(source.url)),
      );
    }
    final uri = Uri.tryParse(source.url);
    if (uri != null && uri.scheme == 'file') {
      final path = uri.toFilePath(windows: Platform.isWindows);
      final bytes = await _readDeveloperFileBounded(
        File(path),
        maxBytes: _maxDeveloperCatalogBytes,
        label: 'Package source',
      );
      return _SourceDocument(
        content: utf8.decode(bytes),
        baseUri: Uri.file(p.dirname(path)),
      );
    }
    if (uri != null && uri.scheme == 'https') {
      final fetched = await fetchHttpsBytes(
        uri,
        maxBytes: _maxDeveloperCatalogBytes,
        label: 'Package source ${source.id}',
      );
      return _SourceDocument(
        content: utf8.decode(fetched.bytes),
        baseUri: fetched.effectiveUri,
      );
    }
    if (uri != null && uri.hasScheme) {
      throw StateError('Unsupported package source scheme: ${uri.scheme}');
    }
    final bytes = await _readDeveloperFileBounded(
      File(source.url),
      maxBytes: _maxDeveloperCatalogBytes,
      label: 'Package source',
    );
    return _SourceDocument(
      content: utf8.decode(bytes),
      baseUri: Uri.file(p.dirname(source.url)),
    );
  }

  List<RegistryMod> _flatRegistryMods(
    Map<String, Object?> decoded,
    PackageSource source,
    Uri baseUri,
  ) {
    if (!decoded.containsKey('mods')) {
      return const <RegistryMod>[];
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
    return entries.whereType<Map>().map((item) {
      final json = item.map((key, value) => MapEntry(key.toString(), value));
      final parsed = RegistryMod.fromJson(json);
      final localPath = json['localPath'] as String?;
      final packageBaseUri =
          localPath != null &&
              source.id == 'io.github.furroxide.topiaforge.local'
          ? Uri.file(_repositoryRoot.path)
          : baseUri;
      return RegistryMod(
        manifest: parsed.manifest,
        downloadUrl: _resolvePackageUrl(
          parsed.downloadUrl.isNotEmpty ? parsed.downloadUrl : localPath ?? '',
          packageBaseUri,
        ),
        packageSha256: parsed.packageSha256,
        changelog: parsed.changelog,
        sourceId: source.id,
        sourceName: source.name,
      );
    }).toList();
  }

  List<RegistryMod> _packageRegistryMods(
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
            ? _manifestFromPackageJson(
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

  Map<String, Object?> _manifestFromPackageJson(
    String packageId,
    Map<String, Object?> packageJson,
    String version,
    Map<String, Object?> versionJson,
  ) {
    final manifest = <String, Object?>{
      ...versionJson,
      'schemaVersion': versionJson['schemaVersion'] ?? 4,
      'name': versionJson['name'] ?? packageId,
      'displayName':
          versionJson['displayName'] ?? packageJson['displayName'] ?? packageId,
      'version': versionJson['version'] ?? version,
    };
    // Registry transport metadata belongs to the source document, not the
    // strict V4 manifest synthesized from a VPM-style version entry.
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

  Future<_PackageReadResult> _readPackage(
    String packageReference, {
    required String expectedSha256,
  }) async {
    requireCanonicalTopiaForgePackageReference(packageReference);
    final packageUri = Uri.tryParse(packageReference);
    if (packageUri?.scheme == 'https' && expectedSha256.trim().isEmpty) {
      throw StateError('Remote packages require a SHA-256 hash.');
    }
    final bytes = await _readPackageBytes(packageReference);
    final actualSha = sha256.convert(bytes).toString();
    if (expectedSha256.trim().isNotEmpty &&
        actualSha.toLowerCase() != expectedSha256.trim().toLowerCase()) {
      throw StateError(
        'Package SHA-256 mismatch for $packageReference. Expected $expectedSha256 but got $actualSha.',
      );
    }
    final archive = _decodeDeveloperArchive(bytes, label: 'Package');
    final manifestFile = archive.entries.firstWhere(
      (file) =>
          file.isFile &&
          file.name.replaceAll('\\', '/') == 'topiaforge.mod.json',
      orElse: () => throw StateError('Package is missing topiaforge.mod.json.'),
    );
    final manifestBytes = _readDeveloperArchiveEntryBounded(
      manifestFile,
      maxBytes: _maxDeveloperManifestBytes,
      label: 'topiaforge.mod.json',
    );
    final manifest = ModManifest.fromJson(
      jsonDecode(utf8.decode(manifestBytes)) as Map<String, Object?>,
    );
    final manifestIssues = manifest
        .validate()
        .where((issue) => issue.isBlocking)
        .toList(growable: false);
    if (manifestIssues.isNotEmpty) {
      throw StateError(
        'Package manifest is invalid: '
        '${manifestIssues.map((issue) => issue.message).join(' ')}',
      );
    }
    final entryAssembly = manifest.entryAssembly.replaceAll('\\', '/');
    if (!archive.entries.any(
      (file) => file.isFile && file.name.replaceAll('\\', '/') == entryAssembly,
    )) {
      throw StateError(
        'entryAssembly was not found in package: ${manifest.entryAssembly}',
      );
    }
    return _PackageReadResult(
      archive: archive,
      manifest: manifest,
      bytes: bytes,
      sha256Hex: actualSha,
    );
  }

  Future<List<int>> _readPackageBytes(String packageReference) async {
    if (_isWindowsPathLike(packageReference)) {
      return _readDeveloperFileBounded(
        File(packageReference),
        maxBytes: _maxDeveloperArchiveBytes,
        label: 'Package',
      );
    }
    final uri = Uri.tryParse(packageReference);
    if (uri != null && uri.scheme == 'file') {
      return _readDeveloperFileBounded(
        File(uri.toFilePath(windows: Platform.isWindows)),
        maxBytes: _maxDeveloperArchiveBytes,
        label: 'Package',
      );
    }
    if (uri != null && uri.scheme == 'https') {
      final fetched = await fetchHttpsBytes(
        uri,
        maxBytes: _maxDeveloperArchiveBytes,
        label: 'Package download',
        totalTimeout: const Duration(minutes: 10),
      );
      return fetched.bytes;
    }
    if (uri != null && uri.hasScheme) {
      throw StateError('Unsupported package URL scheme: ${uri.scheme}');
    }
    return _readDeveloperFileBounded(
      File(packageReference),
      maxBytes: _maxDeveloperArchiveBytes,
      label: 'Package',
    );
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
