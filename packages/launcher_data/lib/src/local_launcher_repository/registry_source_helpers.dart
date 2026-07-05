part of '../local_launcher_repository.dart';

/// Registry catalog loading: merges every enabled package source into one
/// deduped mod list with per-source health, tolerating dead sources.
/// (Split from storage_helpers.dart for the 500-line file cap.)
extension _RegistrySourceHelpers on LocalLauncherRepository {
  Future<List<RegistryMod>> _loadRegistryMods(
    List<InstalledMod> installedMods,
    List<PackageSource> sources,
  ) async {
    return (await _loadRegistryOutcome(installedMods, sources)).mods;
  }

  Future<_RegistryLoadOutcome> _loadRegistryOutcome(
    List<InstalledMod> installedMods,
    List<PackageSource> sources,
  ) async {
    final byId = <String, RegistryMod>{};
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
        for (final mod in mods) {
          final id = mod.manifest.id.toLowerCase();
          final existing = byId[id];
          // One tile per mod id: keep the highest version. On a tie the
          // earlier source wins — the bundled local source is listed first,
          // so an equal version installs from disk instead of re-downloading.
          if (existing == null ||
              _isNewerVersion(mod.manifest.version, existing.manifest.version)) {
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

    final mods = byId.values.map((mod) {
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
    }).toList();
    return _RegistryLoadOutcome(mods: mods, statuses: statuses);
  }

  bool _isRemoteSource(PackageSource source) {
    return source.url.trim().toLowerCase().startsWith('https://');
  }

  Future<List<RegistryMod>> _loadRegistrySource(PackageSource source) async {
    // A local source can point at a DIRECTORY of .robotopiamod packages. The catalog is then
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
    return mods;
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

    final latestById = <String, RegistryMod>{};
    final packageFiles = directory.listSync().whereType<File>().where(
      (file) => file.path.toLowerCase().endsWith('.robotopiamod'),
    );
    for (final file in packageFiles) {
      try {
        final package = await _readPackage(file.path);
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
      } on Object catch (error) {
        await _appendLauncherLog('Skipped package ${file.path}: $error');
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
      return _SourceDocument(
        content: await File(source.url).readAsString(),
        baseUri: Uri.file(p.dirname(source.url)),
      );
    }
    final uri = Uri.tryParse(source.url);
    if (uri != null && uri.scheme == 'file') {
      final path = uri.toFilePath(windows: Platform.isWindows);
      return _SourceDocument(
        content: await File(path).readAsString(),
        baseUri: Uri.file(p.dirname(path)),
      );
    }

    if (uri != null && uri.scheme == 'https') {
      // Bounded so a hung host can never stall the snapshot load — a dead
      // source degrades to a per-source failure status instead.
      final client = HttpClient()
        ..connectionTimeout = const Duration(seconds: 15);
      try {
        final response = await (await client.getUrl(
          uri,
        )).close().timeout(const Duration(seconds: 30));
        if (response.statusCode < 200 || response.statusCode >= 300) {
          throw StateError(
            'HTTP ${response.statusCode} while reading ${source.url}.',
          );
        }
        final content = await utf8
            .decodeStream(response)
            .timeout(const Duration(seconds: 30));
        return _SourceDocument(content: content, baseUri: uri);
      } finally {
        client.close(force: true);
      }
    }

    if (uri != null && uri.hasScheme) {
      throw StateError('Unsupported package source scheme: ${uri.scheme}');
    }

    return _SourceDocument(
      content: await File(source.url).readAsString(),
      baseUri: Uri.file(p.dirname(source.url)),
    );
  }

  List<RegistryMod> _flatRegistryMods(
    Map<String, Object?> decoded,
    PackageSource source,
    Uri baseUri,
  ) {
    return (decoded['mods'] as List? ?? const []).whereType<Map>().map((item) {
      final json = item.map((key, value) => MapEntry(key.toString(), value));
      final parsed = RegistryMod.fromJson(json);
      final localPath = json['localPath'] as String?;
      final packageBaseUri = localPath != null && source.id == 'robotopia.local'
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
                'schemaVersion': versionJson['schemaVersion'] ?? 2,
                'name': versionJson['name'] ?? packageId,
                'displayName':
                    versionJson['displayName'] ??
                    packageJson['displayName'] ??
                    packageId,
                'version': versionJson['version'] ?? versionEntry.key,
              }
            : manifestJson;
        final normalizedManifest = _normalizeManifestAliases(manifestSource);
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
            manifest: ModManifest.fromJson(normalizedManifest),
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
      return rawUrl;
    }
    if (baseUri.scheme == 'file') {
      return Uri.file(
        p.normalize(
          p.join(baseUri.toFilePath(windows: Platform.isWindows), rawUrl),
        ),
      ).toString();
    }
    return baseUri.resolve(rawUrl).toString();
  }
}

class _SourceDocument {
  const _SourceDocument({required this.content, required this.baseUri});

  final String content;
  final Uri baseUri;
}

class _RegistryLoadOutcome {
  const _RegistryLoadOutcome({required this.mods, required this.statuses});

  final List<RegistryMod> mods;
  final List<PackageSourceStatus> statuses;
}

Map<String, Object?> _objectMap(Object? value) {
  if (value is! Map) {
    return const {};
  }
  return value.map((key, mapValue) => MapEntry(key.toString(), mapValue));
}

Map<String, Object?> _normalizeManifestAliases(Map<String, Object?> json) {
  return json;
}
