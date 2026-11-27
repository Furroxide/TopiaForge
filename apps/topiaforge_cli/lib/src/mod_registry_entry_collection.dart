part of 'mod_registry_index_builder.dart';

/// Entry collection for [ModRegistryIndexBuilder]: first-party versions from
/// GitHub release assets or a packages directory (with their changelogs), and
/// the validated community entry files.
///
/// Split out of `mod_registry_index_builder.dart` to stay inside the 500-line
/// cap in AGENTS.md.
extension _ModRegistryEntryCollection on ModRegistryIndexBuilder {
  /// Collected versions per lowercase mod id, newest release first (GitHub
  /// lists releases newest-first; a re-uploaded id+version keeps the newest).
  Future<Map<String, List<_CollectedVersion>>> _collectFromReleases(
    ModRegistryIndexConfig config,
  ) async {
    final client = _client;
    if (client == null) {
      throw StateError('A GitHub client is required for repository mode.');
    }
    final byId = <String, List<_CollectedVersion>>{};
    final releases = await client.listReleases(config.repository.trim());
    for (final release in releases) {
      if (release.draft) {
        continue;
      }
      if (release.prerelease && !config.includePrerelease) {
        continue;
      }
      for (final asset in release.assets) {
        if (!asset.name.toLowerCase().endsWith('.topiaforgemod')) {
          continue;
        }
        final bytes = await _readAssetBytes(client, asset);
        final ModPackageSummary package;
        try {
          package = readModPackage(bytes);
        } on StateError catch (error) {
          throw StateError(
            'Release asset ${asset.name} (${release.tagName}) is not a valid '
            'mod package: ${error.message}',
          );
        }
        _requirePublicHttpsUrl(
          asset.browserDownloadUrl,
          label: 'release asset ${asset.name}',
        );
        _addCollectedVersion(
          byId,
          _CollectedVersion(
            manifest: package.manifest,
            downloadUrl: asset.browserDownloadUrl,
            packageSha256: package.sha256Hex,
            publishedAt: release.publishedAt,
            sourceLabel: '${release.tagName}/${asset.name}',
            packageBytes: bytes.length,
          ),
        );
      }
    }
    return byId;
  }

  Map<String, List<_CollectedVersion>> _collectFromDirectory(
    ModRegistryIndexConfig config,
  ) {
    final directory = Directory(config.packagesDirectory);
    if (!directory.existsSync()) {
      throw StateError(
        'Packages directory does not exist: ${config.packagesDirectory}',
      );
    }
    final baseUri = config.baseUrl.trim().isEmpty
        ? null
        : _normalizeAbsoluteBase(config.baseUrl);
    final files =
        listBoundedDirectorySync(directory)
            .whereType<File>()
            .where((file) => file.path.toLowerCase().endsWith('.topiaforgemod'))
            .toList()
          ..sort((a, b) => a.path.compareTo(b.path));

    final byId = <String, List<_CollectedVersion>>{};
    for (final file in files) {
      final ModPackageSummary package;
      final List<int> bytes;
      try {
        bytes = readBoundedRegularFileSync(
          file,
          maxBytes: CliFileLimits.package,
        );
        package = readModPackage(bytes);
      } on StateError catch (error) {
        throw StateError(
          '${p.basename(file.path)} is not a valid mod package: '
          '${error.message}',
        );
      }
      final fileName = p.basename(file.path);
      _addCollectedVersion(
        byId,
        _CollectedVersion(
          manifest: package.manifest,
          downloadUrl: baseUri == null
              ? fileName
              : baseUri.resolve(Uri.encodeComponent(fileName)).toString(),
          packageSha256: package.sha256Hex,
          sourceLabel: file.path,
          packageBytes: bytes.length,
        ),
      );
    }
    return byId;
  }

  void _attachChangelogs(
    Map<String, List<_CollectedVersion>> byId,
    String changelogsDirectory,
  ) {
    if (changelogsDirectory.trim().isEmpty) {
      return;
    }
    final directory = Directory(changelogsDirectory);
    if (!directory.existsSync()) {
      return;
    }
    for (final modDir in listBoundedDirectorySync(
      directory,
    ).whereType<Directory>()) {
      final manifestFile = File(p.join(modDir.path, 'topiaforge.mod.json'));
      final changelogFile = File(p.join(modDir.path, 'CHANGELOG.md'));
      if (!manifestFile.existsSync() || !changelogFile.existsSync()) {
        continue;
      }
      final Object? decoded;
      try {
        decoded = readBoundedJsonObjectSync(
          manifestFile,
          maxBytes: CliFileLimits.manifest,
        );
      } on FormatException {
        continue;
      }
      if (decoded is! Map) {
        continue;
      }
      final id = (decoded['name'] as String?)?.toLowerCase() ?? '';
      final versions = byId[id];
      if (versions == null || versions.isEmpty) {
        continue;
      }
      _latestOf(versions).changelog = readBoundedTextFileSync(
        changelogFile,
        maxBytes: CliFileLimits.changelog,
        allowEmpty: true,
      ).trim();
    }
  }

  List<RegistryIndexEntry> _loadCommunityEntries(
    String entriesDirectory, {
    required Set<String> firstPartyIds,
    required int historyManifests,
  }) {
    if (entriesDirectory.trim().isEmpty) {
      return const [];
    }
    final directory = Directory(entriesDirectory);
    if (!directory.existsSync()) {
      return const [];
    }
    final files =
        listBoundedDirectorySync(directory)
            .whereType<File>()
            .where((file) => file.path.toLowerCase().endsWith('.json'))
            .toList()
          ..sort((a, b) => a.path.compareTo(b.path));

    final entries = <RegistryIndexEntry>[];
    final seenIds = <String>{};
    for (final file in files) {
      final name = p.basename(file.path);
      final RegistryEntryFile entry;
      try {
        entry = RegistryEntryFile.fromJson(
          readBoundedJsonObjectSync(
            file,
            maxBytes: CliFileLimits.registryEntry,
          ),
        );
      } on Object {
        throw StateError('Registry entry $name is not valid JSON.');
      }
      final issues = [
        ...entry.validate(),
        ..._entryPlacementIssues(
          entry,
          fileName: name,
          firstPartyIds: firstPartyIds,
          seenIds: seenIds,
        ),
      ].where((issue) => issue.isBlocking).toList();
      for (final version in entry.versions) {
        final manifest = version.manifest;
        if (manifest == null) continue;
        issues.addAll(manifest.validate());
        issues.addAll(validateManifestPublicationLicense(manifest));
      }
      if (issues.isNotEmpty) {
        final details = issues.map((issue) => issue.message).join(' ');
        throw StateError(
          'Registry entry $name failed validation — run '
          '`topiaforge registry validate` for details. $details',
        );
      }
      seenIds.add(entry.id.toLowerCase());

      final versions = entry.sortedVersions;
      final latest = versions.first;
      entries.add(
        RegistryIndexEntry(
          manifest: latest.manifest!,
          downloadUrl: latest.downloadUrl,
          packageSha256: latest.packageSha256,
          changelog: latest.changelog,
          origin: 'community',
          extraFields: latest.extraFields,
          history: [
            for (final (index, version) in versions.skip(1).indexed)
              RegistryVersionRef(
                version: version.version,
                downloadUrl: version.downloadUrl,
                packageSha256: version.packageSha256,
                changelog: version.changelog,
                manifest: index < historyManifests ? version.manifest : null,
                // An entry may state its archive size; history keeps it.
                packageBytes: switch (version.extraFields['packageBytes']) {
                  final int bytes
                      when bytes >= 0 &&
                          bytes <= ModRegistryFormat.maxPackageBytes =>
                    bytes,
                  _ => null,
                },
                extraFields: version.extraFields,
              ),
          ],
        ),
      );
    }
    return entries;
  }

  Future<List<int>> _readAssetBytes(
    GitHubReleaseClient client,
    GitHubAsset asset,
  ) async {
    final stream = await client.openAsset(asset);
    final bytes = <int>[];
    await for (final chunk in stream) {
      bytes.addAll(chunk);
      if (bytes.length > ModRegistryFormat.maxPackageBytes) {
        throw StateError(
          'Release asset ${asset.name} is larger than the 512 MB launcher '
          'limit.',
        );
      }
    }
    return bytes;
  }

  Uri _normalizeAbsoluteBase(String baseUrl) {
    final normalized = baseUrl.trim().endsWith('/')
        ? baseUrl.trim()
        : '${baseUrl.trim()}/';
    final uri = _requirePublicHttpsUrl(normalized, label: 'baseUrl');
    return uri;
  }
}
