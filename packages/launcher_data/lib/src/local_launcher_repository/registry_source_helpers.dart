part of '../local_launcher_repository.dart';

/// Registry catalog loading: merges every enabled package source into one
/// deduped mod list with per-source health, tolerating dead sources.
/// (Split from storage_helpers.dart for the 500-line file cap.)
extension _RegistrySourceHelpers on LocalLauncherRepository {
  void _validatePackageSources(List<PackageSource> sources) {
    if (sources.length > 128) {
      throw StateError('At most 128 package sources may be configured.');
    }
    final ids = <String>{};
    for (final source in sources) {
      final id = source.id.trim();
      if (!PackageSourceId.isValid(source.id)) {
        throw StateError(
          'Package source ids must use the safe TopiaForge source id format.',
        );
      }
      if (!ids.add(id.toLowerCase())) {
        throw StateError('Duplicate package source id: $id');
      }
      final value = source.url.trim();
      if (value.isEmpty || value.length > 4096) {
        throw StateError(
          'Package source URLs must contain 1 to 4096 characters.',
        );
      }
      if (_isWindowsPathLike(value)) {
        continue;
      }
      final uri = Uri.tryParse(value);
      if (uri == null || !uri.hasScheme) {
        continue;
      }
      if (uri.scheme == 'file') {
        continue;
      }
      if (!isPublicHttpsUri(uri)) {
        throw StateError(
          'Remote package sources must use an absolute HTTPS URL without credentials, query, or fragment.',
        );
      }
    }
  }

  Future<List<RegistryMod>> _loadRegistryCandidates(
    List<InstalledMod> installedMods,
    List<PackageSource> sources,
  ) async {
    return (await _loadRegistryOutcome(installedMods, sources)).candidates;
  }

  Future<_RegistryLoadOutcome> _loadRegistryOutcome(
    List<InstalledMod> installedMods,
    List<PackageSource> sources,
  ) async {
    final byId = <String, RegistryMod>{};
    final byIdVersion = <String, RegistryMod>{};
    final statuses = <PackageSourceStatus>[];
    for (final source in sources.where((source) => source.enabled)) {
      try {
        final mods = await _loadRegistrySource(source);
        statuses.add(
          PackageSourceStatus(
            sourceId: source.id,
            sourceName: source.name,
            ok: true,
            message: 'Loaded ${mods.length} package(s).',
            modCount: mods.length,
            remote: _isRemoteSource(source),
          ),
        );
        for (final mod in mods.where(
          (mod) => !mod.manifest.validate().any((issue) => issue.isBlocking),
        )) {
          final id = mod.manifest.id.toLowerCase();
          byIdVersion.putIfAbsent(
            '$id@${mod.manifest.version.toLowerCase()}',
            () => mod,
          );
          final existing = byId[id];
          // One tile per mod id: keep the highest version. On a tie the
          // earlier source wins — the bundled local source is listed first,
          // so an equal version installs from disk instead of re-downloading.
          if (existing == null ||
              _isNewerVersion(
                mod.manifest.version,
                existing.manifest.version,
              )) {
            byId[id] = mod;
          }
        }
      } on Object catch (error) {
        await _appendLauncherLog('Package source ${source.id} failed: $error');
        statuses.add(
          PackageSourceStatus(
            sourceId: source.id,
            sourceName: source.name,
            ok: false,
            message: '$error',
            remote: _isRemoteSource(source),
          ),
        );
      }
    }

    RegistryMod withInstalledVersion(RegistryMod mod) {
      final installed = installedMods
          .where(
            (item) => item.id.toLowerCase() == mod.manifest.id.toLowerCase(),
          )
          .firstOrNull;
      return RegistryMod(
        manifest: mod.manifest,
        downloadUrl: mod.downloadUrl,
        packageSha256: mod.packageSha256,
        changelog: mod.changelog,
        sourceId: mod.sourceId,
        sourceName: mod.sourceName,
        installedVersion: installed?.version,
      );
    }

    return _RegistryLoadOutcome(
      mods: byId.values.map(withInstalledVersion).toList(),
      candidates: byIdVersion.values.map(withInstalledVersion).toList(),
      statuses: statuses,
    );
  }

  bool _isRemoteSource(PackageSource source) {
    return source.url.trim().toLowerCase().startsWith('https://');
  }

  Future<List<RegistryMod>> _loadRegistrySource(PackageSource source) async {
    // A local source can point at a DIRECTORY of .topiaforgemod packages. The catalog is then
    // derived straight from the packages (manifest + sha read from each file) with no separate
    // pinned metadata, so the listing can never disagree with the packages on disk.
    final directory = _resolveDirectorySource(source);
    if (directory != null) {
      return _packagesInDirectory(directory, source);
    }

    final document = await _readSourceDocument(source);
    final decoded = jsonDecode(document.content) as Map<String, Object?>;
    final mods = <RegistryMod>[
      ..._flatRegistryMods(decoded, source, document.baseUri),
      ..._vpmRegistryMods(decoded, source, document.baseUri),
    ];
    _validateRegistryModTrust(source, mods);
    return mods;
  }

  void _validateRegistryModTrust(PackageSource source, List<RegistryMod> mods) {
    for (final mod in mods) {
      final reference = mod.downloadUrl.trim();
      final uri = Uri.tryParse(reference);
      if (uri == null || !uri.hasScheme) {
        throw StateError(
          '${mod.manifest.id} has no absolute package download URL.',
        );
      }
      if (uri.scheme == 'file') {
        if (_isRemoteSource(source)) {
          throw StateError(
            '${mod.manifest.id} from a remote source points at a local file.',
          );
        }
        continue;
      }
      if (!isPublicHttpsUri(uri)) {
        throw StateError(
          '${mod.manifest.id} package downloads must use an absolute HTTPS URL without credentials, query, or fragment.',
        );
      }
      if (!RegExp(r'^[0-9a-fA-F]{64}$').hasMatch(mod.packageSha256.trim())) {
        throw StateError(
          '${mod.manifest.id} remote package is missing a valid SHA-256 hash.',
        );
      }
    }
  }

  // Returns the directory a source points at, or null when the source is a document (JSON/VPM
  // file or https registry). A file:// or bare path with no extension is treated as a package
  // directory — including one that does not exist yet, so an unbuilt dist/ yields an empty
  // catalog rather than a read error. An existing file (e.g. a .json registry) stays a document.
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

    final byIdVersion = <String, RegistryMod>{};
    final packageFiles =
        directory
            .listSync()
            .whereType<File>()
            .where((file) => file.path.toLowerCase().endsWith('.topiaforgemod'))
            .toList()
          ..sort((left, right) {
            final insensitive = left.path.toLowerCase().compareTo(
              right.path.toLowerCase(),
            );
            return insensitive != 0
                ? insensitive
                : left.path.compareTo(right.path);
          });
    for (final file in packageFiles) {
      try {
        final package = await _readPackage(file.path);
        final id = package.manifest.id.toLowerCase();
        final key = '$id@${package.manifest.version.toLowerCase()}';
        byIdVersion.putIfAbsent(
          key,
          () => RegistryMod(
            manifest: package.manifest,
            downloadUrl: Uri.file(file.path).toString(),
            packageSha256: package.sha256Hex,
            sourceId: source.id,
            sourceName: source.name,
          ),
        );
      } on Object catch (error) {
        await _appendLauncherLog('Skipped package ${file.path}: $error');
      }
    }
    return byIdVersion.values.toList();
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
      final bytes = await _readLauncherFileBounded(
        File(source.url),
        _maxRegistryDocumentBytes,
      );
      return _SourceDocument(
        content: utf8.decode(bytes),
        baseUri: Uri.file(p.dirname(source.url)),
      );
    }
    final uri = Uri.tryParse(source.url);
    if (uri != null && uri.scheme == 'file') {
      final path = uri.toFilePath(windows: Platform.isWindows);
      final bytes = await _readLauncherFileBounded(
        File(path),
        _maxRegistryDocumentBytes,
      );
      return _SourceDocument(
        content: utf8.decode(bytes),
        baseUri: Uri.file(p.dirname(path)),
      );
    }

    if (uri != null && uri.scheme == 'https') {
      final fetched = await fetchHttpsBytes(
        uri,
        maxBytes: _maxRegistryDocumentBytes,
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

    final bytes = await _readLauncherFileBounded(
      File(source.url),
      _maxRegistryDocumentBytes,
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
      final downloadUrl = _resolvePackageUrl(
        parsed.downloadUrl.isNotEmpty ? parsed.downloadUrl : localPath ?? '',
        packageBaseUri,
      );
      return RegistryMod(
        manifest: parsed.manifest,
        downloadUrl: downloadUrl,
        packageSha256: parsed.packageSha256,
        changelog: parsed.changelog,
        sourceId: source.id,
        sourceName: source.name,
      );
    }).toList();
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
            ? <String, Object?>{
                ...versionJson,
                'schemaVersion': versionJson['schemaVersion'] ?? 3,
                'name': versionJson['name'] ?? packageId,
                'displayName':
                    versionJson['displayName'] ??
                    packageJson['displayName'] ??
                    packageId,
                'version': versionJson['version'] ?? versionEntry.key,
              }
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

const _maxRegistryDocumentBytes = 16 * 1024 * 1024;

class _SourceDocument {
  const _SourceDocument({required this.content, required this.baseUri});

  final String content;
  final Uri baseUri;
}

class _RegistryLoadOutcome {
  const _RegistryLoadOutcome({
    required this.mods,
    required this.candidates,
    required this.statuses,
  });

  final List<RegistryMod> mods;
  final List<RegistryMod> candidates;
  final List<PackageSourceStatus> statuses;
}

Map<String, Object?> _objectMap(Object? value) {
  if (value is! Map) {
    return const {};
  }
  return value.map((key, mapValue) => MapEntry(key.toString(), mapValue));
}
