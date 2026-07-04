part of '../local_launcher_repository.dart';

extension _StorageHelpers on LocalLauncherRepository {
  Future<List<LauncherProfile>> _loadProfiles() async {
    if (!_profilesFile.existsSync()) {
      final defaults = [LauncherProfile.defaultProfile()];
      await saveProfiles(defaults, defaults.first.id);
      return defaults;
    }

    final decoded = jsonDecode(await _profilesFile.readAsString());
    final profiles = (decoded is Map ? decoded['profiles'] : null) as List?;
    final result = profiles == null
        ? <LauncherProfile>[]
        : profiles
              .whereType<Map>()
              .map(
                (item) => LauncherProfile.fromJson(
                  item.map((key, value) => MapEntry(key.toString(), value)),
                ),
              )
              .toList();
    return result.isEmpty ? [LauncherProfile.defaultProfile()] : result;
  }

  Future<List<PackageSource>> _loadPackageSources() async {
    if (!_sourcesFile.existsSync()) {
      return _defaultPackageSources();
    }

    final decoded = jsonDecode(await _sourcesFile.readAsString());
    final sources = (decoded is Map ? decoded['sources'] : null) as List?;
    final parsed = sources == null
        ? <PackageSource>[]
        : sources
              .whereType<Map>()
              .map(
                (item) => PackageSource.fromJson(
                  item.map((key, value) => MapEntry(key.toString(), value)),
                ),
              )
              .where(
                (source) =>
                    source.id.trim().isNotEmpty && source.url.trim().isNotEmpty,
              )
              // The built-in source is app-managed: always reconcile its URL to the current
              // default so an older persisted entry (e.g. one that still points at a removed
              // mod_registry.json file) cannot pin a stale catalog location.
              .map(
                (source) => source.id == 'robotopia.local'
                    ? _defaultPackageSources().first.copyWith(
                        enabled: source.enabled,
                      )
                    : source,
              )
              .toList();
    return parsed.isEmpty ? _defaultPackageSources() : parsed;
  }

  List<PackageSource> _defaultPackageSources() {
    return [
      PackageSource(
        id: 'robotopia.local',
        name: 'Bundled Local Packages',
        // Point at the directory of built .robotopiamod packages. The catalog is derived
        // directly from those packages (manifest + sha read from each file), so there is no
        // separate metadata to drift out of sync with the packages themselves.
        url: Uri.file(p.join(_repositoryRoot.path, 'dist')).toString(),
        builtIn: true,
      ),
    ];
  }

  Future<List<RegistryMod>> _loadRegistryMods(
    List<InstalledMod> installedMods,
    List<PackageSource> sources,
  ) async {
    final mods = <RegistryMod>[];
    for (final source in sources.where((source) => source.enabled)) {
      try {
        mods.addAll(await _loadRegistrySource(source));
      } on Object catch (error) {
        await _appendLauncherLog('Package source ${source.id} failed: $error');
      }
    }

    return mods.map((mod) {
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
    if (uri != null && uri.scheme == 'file') {
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

  Future<_SourceDocument> _readSourceDocument(PackageSource source) async {
    final uri = Uri.tryParse(source.url);
    if (uri != null && uri.scheme == 'file') {
      final path = uri.toFilePath(windows: Platform.isWindows);
      return _SourceDocument(
        content: await File(path).readAsString(),
        baseUri: Uri.file(p.dirname(path)),
      );
    }

    if (uri != null && uri.scheme == 'https') {
      final client = HttpClient();
      try {
        final response = await (await client.getUrl(uri)).close();
        if (response.statusCode < 200 || response.statusCode >= 300) {
          throw StateError(
            'HTTP ${response.statusCode} while reading ${source.url}.',
          );
        }
        final content = await utf8.decodeStream(response);
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

  Future<Map<String, Object?>> _loadSettings() async {
    if (!_settingsFile.existsSync()) {
      return <String, Object?>{};
    }

    final decoded = jsonDecode(await _settingsFile.readAsString());
    return decoded is Map<String, Object?> ? decoded : <String, Object?>{};
  }

  Future<void> _saveSettings(Map<String, Object?> settings) async {
    await _settingsFile.create(recursive: true);
    await _settingsFile.writeAsString(_prettyJson(settings));
  }

  Future<String> _readLauncherLog({int maxLines = 200}) async {
    if (!_launcherLogFile.existsSync()) {
      return '';
    }

    return _tail(await _launcherLogFile.readAsLines(), maxLines).join('\n');
  }

  Future<void> _appendLauncherLog(String message) async {
    await _launcherLogFile.create(recursive: true);
    await _launcherLogFile.writeAsString(
      '${DateTime.now().toUtc().toIso8601String()} $message\n',
      mode: FileMode.append,
    );
  }

  Future<WorldCatalog> _loadWorldCatalog(
    GameInstall install,
    List<InstalledMod> installedMods,
    List<RegistryMod> registryMods,
  ) async {
    final file = File(
      p.join(_managerData(install).path, 'robotopia.worlds', 'catalog.json'),
    );
    WorldCatalog catalog;
    if (!file.existsSync()) {
      catalog = WorldCatalog.fallback();
    } else {
      try {
        catalog = WorldCatalog.fromJson(
          jsonDecode(await file.readAsString()) as Map<String, Object?>,
        );
      } on Object catch (error) {
        await _appendLauncherLog('World catalog read failed: $error');
        catalog = WorldCatalog.fallback();
      }
    }

    return _mergeManifestGamemodes(catalog, installedMods, registryMods);
  }

  WorldCatalog _mergeManifestGamemodes(
    WorldCatalog catalog,
    List<InstalledMod> installedMods,
    List<RegistryMod> registryMods,
  ) {
    final gamemodes = [...catalog.gamemodes];
    final seen = {for (final gamemode in gamemodes) gamemode.id.toLowerCase()};
    final installedIds = {
      for (final mod in installedMods.where((mod) => mod.enabled))
        mod.id.toLowerCase(),
    };

    for (final mod in installedMods.where((mod) => mod.enabled)) {
      for (final gamemode in mod.manifest?.worldGamemodes ?? const []) {
        if (seen.add(gamemode.id.toLowerCase())) {
          gamemodes.add(gamemode);
        }
      }
    }

    for (final mod in registryMods.where(
      (mod) => installedIds.contains(mod.manifest.id.toLowerCase()),
    )) {
      for (final gamemode in mod.manifest.worldGamemodes) {
        if (seen.add(gamemode.id.toLowerCase())) {
          gamemodes.add(gamemode);
        }
      }
    }

    return WorldCatalog(worlds: catalog.worlds, gamemodes: gamemodes);
  }

  Future<void> _writeWorldSelection(
    GameInstall install,
    WorldSelection selection,
  ) async {
    final file = File(
      p.join(_managerConfig(install).path, 'robotopia.worlds.json'),
    );
    await file.create(recursive: true);
    await file.writeAsString(_prettyJson(selection.toRuntimeConfig()));
  }
}

class _SourceDocument {
  const _SourceDocument({required this.content, required this.baseUri});

  final String content;
  final Uri baseUri;
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
