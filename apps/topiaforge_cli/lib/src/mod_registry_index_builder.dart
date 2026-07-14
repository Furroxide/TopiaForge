import 'dart:convert';
import 'dart:io';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:topiaforge/src/launcher_update_index_builder.dart';
import 'package:topiaforge/src/registry_entry_builder.dart';

import 'atomic_output.dart';
import 'bounded_file_reader.dart';

part 'mod_registry_validator.dart';
part 'mod_registry_collection_policy.dart';
part 'registry_publication_policy.dart';

/// Builds the published mod-registry `index.json`.
///
/// First-party mods come from GitHub Release `.topiaforgemod` assets
/// (`repository` mode — the published sha is computed from the exact hosted
/// bytes) or from a local directory of packages (`packagesDirectory` mode,
/// for self-hosters). Community mods merge in from `registry/*.json` entry
/// files whose manifests are inlined, so the build never downloads
/// community-hosted packages.
class ModRegistryIndexConfig {
  const ModRegistryIndexConfig({
    required this.outputDirectory,
    this.repository = '',
    this.packagesDirectory = '',
    this.entriesDirectory = '',
    this.changelogsDirectory = '',
    this.baseUrl = '',
    this.includePrerelease = false,
    this.registryName = 'TopiaForge Mod Registry',
  });

  final String outputDirectory;

  /// GitHub repository (`owner/name`) whose release assets are indexed.
  final String repository;

  /// Local directory of `.topiaforgemod` packages (self-hoster mode).
  final String packagesDirectory;

  /// Directory of community `registry/<id>.json` entry files.
  final String entriesDirectory;

  /// Directory scanned for `<mod>/topiaforge.mod.json` + `CHANGELOG.md`
  /// pairs; supplies the latest-version changelog for first-party mods.
  final String changelogsDirectory;

  /// Base URL for package links in `packagesDirectory` mode. When empty the
  /// index uses bare filenames, which launchers resolve against the index
  /// location — correct when packages sit next to `index.json`.
  final String baseUrl;

  final bool includePrerelease;
  final String registryName;
}

class ModRegistryIndexResult {
  const ModRegistryIndexResult({
    required this.firstPartyCount,
    required this.communityCount,
    required this.indexPath,
  });

  final int firstPartyCount;
  final int communityCount;
  final String indexPath;
}

class ModRegistryIndexBuilder {
  ModRegistryIndexBuilder({
    GitHubReleaseClient? client,
    DateTime Function()? clock,
  }) : _client = client,
       _clock = clock ?? DateTime.now;

  final GitHubReleaseClient? _client;
  final DateTime Function() _clock;

  Future<ModRegistryIndexResult> build(ModRegistryIndexConfig config) async {
    final hasRepository = config.repository.trim().isNotEmpty;
    final hasDirectory = config.packagesDirectory.trim().isNotEmpty;
    if (hasRepository == hasDirectory) {
      throw ArgumentError(
        'Exactly one of repository or packagesDirectory is required.',
      );
    }

    final firstParty = hasRepository
        ? await _collectFromReleases(config)
        : _collectFromDirectory(config);
    _attachChangelogs(firstParty, config.changelogsDirectory);

    final community = _loadCommunityEntries(
      config.entriesDirectory,
      firstPartyIds: firstParty.keys.toSet(),
    );

    final mods =
        [
          ...firstParty.values.map(
            (versions) => _indexEntry(versions, 'first-party'),
          ),
          ...community,
        ]..sort(
          (a, b) => a.manifest.id.toLowerCase().compareTo(
            b.manifest.id.toLowerCase(),
          ),
        );

    final output = createAtomicStagingDirectory(config.outputDirectory);
    final indexPath = p.join(output.path, 'index.json');
    final index = <String, Object?>{
      r'$schema': ModRegistryFormat.canonicalIndexSchemaUrl,
      'formatVersion': ModRegistryFormat.indexFormatVersion,
      'name': config.registryName,
      'generatedAt': _clock().toUtc().toIso8601String(),
      if (hasRepository)
        'sourceRepository': 'https://github.com/${config.repository.trim()}',
      'mods': mods.map((entry) => entry.toJson()).toList(),
    };
    final json = const JsonEncoder.withIndent('  ').convert(index);
    try {
      await File(indexPath).writeAsString('$json\n');
      await File(p.join(output.path, '.nojekyll')).writeAsString('');
      publishAtomicDirectory(output, config.outputDirectory);
      return ModRegistryIndexResult(
        firstPartyCount: firstParty.length,
        communityCount: community.length,
        indexPath: p.join(config.outputDirectory, 'index.json'),
      );
    } on Object {
      deleteAtomicStagingDirectory(output);
      rethrow;
    }
  }

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
      try {
        package = readModPackage(
          readBoundedRegularFileSync(file, maxBytes: CliFileLimits.package),
        );
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
            for (final version in versions.skip(1))
              RegistryVersionRef(
                version: version.version,
                downloadUrl: version.downloadUrl,
                packageSha256: version.packageSha256,
                changelog: version.changelog,
                extraFields: version.extraFields,
              ),
          ],
        ),
      );
    }
    return entries;
  }

  RegistryIndexEntry _indexEntry(
    List<_CollectedVersion> versions,
    String origin,
  ) {
    final latest = _latestOf(versions);
    final history = versions.where((item) => !identical(item, latest)).toList()
      ..sort(
        (a, b) => _versionOf(b.manifest).compareTo(_versionOf(a.manifest)),
      );
    return RegistryIndexEntry(
      manifest: latest.manifest,
      downloadUrl: latest.downloadUrl,
      packageSha256: latest.packageSha256,
      changelog: latest.changelog,
      origin: origin,
      publishedAt: latest.publishedAt,
      history: [
        for (final version in history)
          RegistryVersionRef(
            version: version.manifest.version,
            downloadUrl: version.downloadUrl,
            packageSha256: version.packageSha256,
            publishedAt: version.publishedAt,
          ),
      ],
    );
  }

  _CollectedVersion _latestOf(List<_CollectedVersion> versions) {
    var latest = versions.first;
    for (final candidate in versions.skip(1)) {
      if (_versionOf(
            candidate.manifest,
          ).compareTo(_versionOf(latest.manifest)) >
          0) {
        latest = candidate;
      }
    }
    return latest;
  }

  SemanticVersion _versionOf(ModManifest manifest) {
    return SemanticVersion.tryParse(manifest.version) ??
        SemanticVersion(0, 0, 0);
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

/// Placement rules that only make sense relative to the whole registry
/// (shared with the validator part).
List<LauncherIssue> _entryPlacementIssues(
  RegistryEntryFile entry, {
  required String fileName,
  required Set<String> firstPartyIds,
  required Set<String> seenIds,
}) {
  final issues = <LauncherIssue>[];
  final idLower = entry.id.toLowerCase();
  if (fileName != '$idLower.json') {
    issues.add(
      LauncherIssue(
        severity: IssueSeverity.error,
        subjectId: entry.id,
        message:
            'Entry file must be named "$idLower.json" (lowercase id), got '
            '"$fileName".',
      ),
    );
  }
  for (final prefix in ModRegistryFormat.reservedIdPrefixes) {
    if (idLower.startsWith(prefix)) {
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          subjectId: entry.id,
          message:
              'The "$prefix" id prefix is reserved for first-party packages.',
        ),
      );
    }
  }
  if (firstPartyIds.contains(idLower)) {
    issues.add(
      LauncherIssue(
        severity: IssueSeverity.error,
        subjectId: entry.id,
        message: 'Id "${entry.id}" collides with a first-party mod.',
      ),
    );
  }
  if (seenIds.contains(idLower)) {
    issues.add(
      LauncherIssue(
        severity: IssueSeverity.error,
        subjectId: entry.id,
        message: 'Another entry file already declares id "${entry.id}".',
      ),
    );
  }
  return issues;
}
