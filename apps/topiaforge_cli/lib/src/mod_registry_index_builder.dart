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
part 'mod_registry_entry_collection.dart';
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
