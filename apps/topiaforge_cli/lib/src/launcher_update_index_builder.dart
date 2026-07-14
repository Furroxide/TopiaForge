import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:path/path.dart' as p;

import 'atomic_output.dart';

part 'launcher_update_github_client.dart';
part 'launcher_update_index_helpers.dart';

class LauncherUpdateIndexConfig {
  LauncherUpdateIndexConfig({
    required this.repository,
    required this.outputDirectory,
    String? baseUrl,
  }) : baseUrl = baseUrl ?? _defaultBaseUrl(repository);

  final String repository;
  final String outputDirectory;
  final String baseUrl;
}

class LauncherUpdateIndexResult {
  const LauncherUpdateIndexResult({
    required this.itemCount,
    required this.manualReleasesUrl,
  });

  final int itemCount;
  final String manualReleasesUrl;
}

class LauncherUpdateIndexBuilder {
  LauncherUpdateIndexBuilder({
    required GitHubReleaseClient client,
    DateTime Function()? clock,
  }) : _client = client,
       _clock = clock ?? DateTime.now;

  final GitHubReleaseClient _client;
  final DateTime Function() _clock;

  Future<LauncherUpdateIndexResult> build(
    LauncherUpdateIndexConfig config,
  ) async {
    _validateRepository(config.repository);
    final baseUri = _normalizeBaseUri(config.baseUrl);
    final generatedAt = _clock().toUtc().toIso8601String();
    final releases = await _client.listReleases(config.repository);
    final output = createAtomicStagingDirectory(config.outputDirectory);

    try {
      final candidates = <_ManualReleaseCandidate>[];
      for (final release in releases) {
        if (release.draft || release.prerelease) continue;
        final version = _releaseVersion(release);
        if (version == null || version.version.contains('-')) continue;
        if (_releaseChannel(release) != 'release') continue;
        candidates.add(
          _ManualReleaseCandidate(release: release, version: version),
        );
      }
      if (candidates.isEmpty) {
        throw StateError(
          'No published stable release is available for manual-releases.json.',
        );
      }
      candidates.sort(
        (left, right) => _compareVersionSort(
          right.version.releaseLabel,
          left.version.releaseLabel,
        ),
      );
      final selected = candidates.first;
      final platformAssets = _assetsByPlatform(selected.release.assets);
      final missing = _platforms
          .where((platform) => !platformAssets.containsKey(platform))
          .toList();
      if (missing.isNotEmpty) {
        throw StateError(
          'Latest stable release ${selected.release.tagName} is missing '
          'production assets for: ${missing.join(', ')}.',
        );
      }
      final platforms = <String, Object?>{};
      for (final platform in _platforms) {
        final asset = platformAssets[platform]!;
        final digest = await _assetDigest(asset);
        platforms[platform] = {
          'url': asset.browserDownloadUrl,
          'sha256': digest.sha256,
          'size': digest.length,
        };
      }
      await output.create(recursive: true);
      final catalog = <String, Object?>{
        r'$schema':
            'https://raw.githubusercontent.com/furroxide/TopiaForge/main/schemas/topiaforge.manual-releases.schema.json',
        'formatVersion': 2,
        'manualOnly': true,
        'generatedAt': generatedAt,
        'releaseUrl':
            'https://github.com/${config.repository}/releases/tag/${Uri.encodeComponent(selected.release.tagName)}',
        'platforms': platforms,
      };
      await _writeJsonFile(
        p.join(output.path, 'manual-releases.json'),
        catalog,
      );
      await File(p.join(output.path, '.nojekyll')).writeAsString('');

      publishAtomicDirectory(output, config.outputDirectory);
      return LauncherUpdateIndexResult(
        itemCount: platforms.length,
        manualReleasesUrl: baseUri.resolve('manual-releases.json').toString(),
      );
    } on Object {
      deleteAtomicStagingDirectory(output);
      rethrow;
    }
  }

  Future<_AssetDigest> _assetDigest(GitHubAsset asset) async {
    final stream = await _client.openAsset(asset);
    var length = 0;
    final digest = await sha256
        .bind(
          stream.map((chunk) {
            length += chunk.length;
            if (length > _maxManualReleaseAssetBytes) {
              throw StateError(
                '${asset.name} exceeds the manual release asset size limit.',
              );
            }
            return chunk;
          }),
        )
        .single;
    return _AssetDigest(sha256: digest.toString(), length: length);
  }
}

class _ManualReleaseCandidate {
  const _ManualReleaseCandidate({required this.release, required this.version});

  final GitHubRelease release;
  final _ReleaseVersion version;
}
