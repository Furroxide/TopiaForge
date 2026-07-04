part of '../local_developer_repository.dart';

extension LocalDeveloperSourceHelpers on LocalDeveloperRepository {
  PackageSource _localSource() {
    return PackageSource(
      id: 'robotopia.local',
      name: 'Bundled Local Packages',
      // Derived from the built .robotopiamod packages in dist/, not a hand-maintained file.
      url: Uri.file(p.join(_repositoryRoot.path, 'dist')).toString(),
      builtIn: true,
    );
  }

  Future<List<RegistryMod>> _loadRegistryMods(
    List<PackageSource> sources,
  ) async {
    final mods = <RegistryMod>[];
    for (final source in sources.where((item) => item.enabled)) {
      mods.addAll(await _loadRegistrySource(source));
    }
    return mods;
  }

  Future<List<RegistryMod>> _loadRegistrySource(PackageSource source) async {
    // A local source can point at a DIRECTORY of .robotopiamod packages: derive the catalog
    // straight from the packages (manifest + sha read from each file) so there is no separate
    // metadata file that can drift out of sync with the packages on disk.
    final directory = _resolveDirectorySource(source);
    if (directory != null) {
      return _packagesInDirectory(directory, source);
    }

    final document = await _readSourceDocument(source);
    final decoded = jsonDecode(document.content) as Map<String, Object?>;
    return [
      ..._flatRegistryMods(decoded, source, document.baseUri),
      ..._packageRegistryMods(decoded, source, document.baseUri),
    ];
  }

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
        return _SourceDocument(
          content: await utf8.decodeStream(response),
          baseUri: uri,
        );
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
            : _normalizeManifestAliases(manifestJson);
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
    return _normalizeManifestAliases({
      ...versionJson,
      'schemaVersion': versionJson['schemaVersion'] ?? 2,
      'name': versionJson['name'] ?? packageId,
      'displayName':
          versionJson['displayName'] ?? packageJson['displayName'] ?? packageId,
      'version': versionJson['version'] ?? version,
    });
  }

  Map<String, Object?> _normalizeManifestAliases(Map<String, Object?> json) {
    return json;
  }

  Future<DeveloperLock> _restoreLockedPackages(
    String root,
    DeveloperLock lock,
  ) async {
    final restored = <LockedPackage>[];
    for (final package in lock.packages) {
      final result = await _readPackage(
        package.packageUrl,
        expectedSha256: package.packageSha256,
      );
      final packageRoot = Directory(
        p.join(root, '.robotopia', 'packages', package.id, package.version),
      )..createSync(recursive: true);
      final packageFile = File(
        p.join(
          packageRoot.path,
          '${package.id}-${package.version}.robotopiamod',
        ),
      );
      await packageFile.writeAsBytes(result.bytes);
      final extracted = Directory(p.join(packageRoot.path, 'extracted'));
      if (extracted.existsSync()) {
        extracted.deleteSync(recursive: true);
      }
      extracted.createSync(recursive: true);
      for (final file in result.archive.files) {
        final safePath = _safeArchivePath(file.name);
        final outputPath = p.join(extracted.path, safePath);
        if (file.isFile) {
          File(outputPath)
            ..createSync(recursive: true)
            ..writeAsBytesSync(file.content as List<int>);
        } else {
          Directory(outputPath).createSync(recursive: true);
        }
      }
      restored.add(
        LockedPackage(
          id: package.id,
          name: package.name,
          version: package.version,
          packageUrl: package.packageUrl,
          packageSha256: result.sha256Hex,
          sourceId: package.sourceId,
          sourceName: package.sourceName,
          dependencies: package.dependencies,
          apiAssemblies: package.apiAssemblies,
          cachePath: p.relative(packageFile.path, from: root),
        ),
      );
    }
    return DeveloperLock(
      schemaVersion: lock.schemaVersion,
      projectId: lock.projectId,
      resolvedAtUtc: lock.resolvedAtUtc,
      packages: restored,
      dependencyGraph: lock.dependencyGraph,
    );
  }

  Future<_PackageReadResult> _readPackage(
    String packageReference, {
    required String expectedSha256,
  }) async {
    final bytes = await _readPackageBytes(packageReference);
    final actualSha = sha256.convert(bytes).toString();
    if (expectedSha256.trim().isNotEmpty &&
        actualSha.toLowerCase() != expectedSha256.trim().toLowerCase()) {
      throw StateError(
        'Package SHA-256 mismatch for $packageReference. Expected $expectedSha256 but got $actualSha.',
      );
    }
    final archive = ZipDecoder().decodeBytes(bytes);
    for (final file in archive.files) {
      _safeArchivePath(file.name);
    }
    final manifestFile = archive.files.firstWhere(
      (file) => file.name.replaceAll('\\', '/') == 'robotopia.mod.json',
      orElse: () => throw StateError('Package is missing robotopia.mod.json.'),
    );
    final manifest = ModManifest.fromJson(
      jsonDecode(utf8.decode(manifestFile.content as List<int>))
          as Map<String, Object?>,
    );
    return _PackageReadResult(
      archive: archive,
      manifest: manifest,
      bytes: bytes,
      sha256Hex: actualSha,
    );
  }

  Future<List<int>> _readPackageBytes(String packageReference) async {
    final uri = Uri.tryParse(packageReference);
    if (uri != null && uri.scheme == 'file') {
      return File(uri.toFilePath(windows: Platform.isWindows)).readAsBytes();
    }
    if (uri != null && uri.scheme == 'https') {
      final client = HttpClient();
      try {
        final response = await (await client.getUrl(uri)).close();
        if (response.statusCode < 200 || response.statusCode >= 300) {
          throw StateError('Download failed with HTTP ${response.statusCode}.');
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
    return File(packageReference).readAsBytes();
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

  String _safeArchivePath(String rawPath) {
    final normalized = rawPath.replaceAll('\\', '/');
    final parts = normalized.split('/');
    if (normalized.startsWith('/') ||
        RegExp(r'^[A-Za-z]:/').hasMatch(normalized) ||
        parts.any((part) => part == '..')) {
      throw StateError('Package contains an unsafe path: $rawPath');
    }
    return normalized;
  }
}

class _SourceDocument {
  const _SourceDocument({required this.content, required this.baseUri});

  final String content;
  final Uri baseUri;
}

class _PackageReadResult {
  const _PackageReadResult({
    required this.archive,
    required this.manifest,
    required this.bytes,
    required this.sha256Hex,
  });

  final Archive archive;
  final ModManifest manifest;
  final List<int> bytes;
  final String sha256Hex;
}

Map<String, Object?> _objectMap(Object? value) {
  if (value is! Map) {
    return const {};
  }
  return value.map((key, mapValue) => MapEntry(key.toString(), mapValue));
}
