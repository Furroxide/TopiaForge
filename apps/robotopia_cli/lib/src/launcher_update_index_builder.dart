import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:path/path.dart' as p;

part 'launcher_update_github_client.dart';
part 'launcher_update_index_helpers.dart';

class LauncherUpdateIndexConfig {
  LauncherUpdateIndexConfig({
    required this.repository,
    required this.outputDirectory,
    String? baseUrl,
    this.appName = _appName,
    this.packageId = _packageId,
    this.minimumUpdaterVersion = _minimumUpdaterVersion,
  }) : baseUrl = baseUrl ?? _defaultBaseUrl(repository);

  final String repository;
  final String outputDirectory;
  final String baseUrl;
  final String appName;
  final String packageId;
  final String minimumUpdaterVersion;
}

class LauncherUpdateIndexResult {
  const LauncherUpdateIndexResult({
    required this.itemCount,
    required this.appArchiveUrl,
  });

  final int itemCount;
  final String appArchiveUrl;
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
    final output = Directory(config.outputDirectory);
    final baseUri = _normalizeBaseUri(config.baseUrl);
    final generatedAt = _clock().toUtc().toIso8601String();
    final releases = await _client.listReleases(config.repository);
    final channelsIndex = _emptyChannelsIndex();
    final versionsIndex = <String, dynamic>{};
    final items = <Map<String, Object?>>[];

    for (final release in releases) {
      if (release.draft) {
        continue;
      }
      final version = _releaseVersion(release);
      if (version == null) {
        continue;
      }

      final channel = _releaseChannel(release);
      final mandatory = _isMandatoryRelease(release);
      final platformAssets = _assetsByPlatform(release.assets);
      for (final platform in _platforms) {
        final asset = platformAssets[platform];
        if (asset == null) {
          continue;
        }

        final artifact = await _assetDigest(asset);
        final versionPath = _safePathSegment(version.releaseLabel);
        final descriptorPath =
            'releases/$versionPath/$channel/$platform/release.json';
        final descriptorUrl = baseUri.resolve(descriptorPath).toString();
        final descriptor = _descriptor(
          config: config,
          generatedAt: generatedAt,
          version: version,
          platform: platform,
          channel: channel,
          asset: asset,
          artifact: artifact,
        );

        await _writeJsonFile(_outputPath(output, descriptorPath), descriptor);

        items.add(
          _archiveItem(
            version: version,
            platform: platform,
            channel: channel,
            mandatory: mandatory,
            releaseUrl: descriptorUrl,
          ),
        );
        _addIndexEntry(
          channelsIndex: channelsIndex,
          versionsIndex: versionsIndex,
          channel: channel,
          version: version.releaseLabel,
          platform: platform,
          releaseUrl: descriptorUrl,
          artifactUrl: asset.browserDownloadUrl,
          tagName: release.tagName,
          publishedAt: release.publishedAt,
        );
      }
    }

    items.sort(_compareArchiveItems);
    _setLatestChannelVersions(channelsIndex);
    await output.create(recursive: true);

    final archive = <String, Object?>{
      'schemaVersion': 3,
      'appName': config.appName,
      'generatedAt': generatedAt,
      'sourceRepository': 'https://github.com/${config.repository}',
      'channels': channelsIndex,
      'versions': versionsIndex,
      'items': items,
    };
    await _writeJsonFile(p.join(output.path, 'app-archive.json'), archive);
    await _writeJsonFile(p.join(output.path, 'index.json'), archive);
    await File(p.join(output.path, '.nojekyll')).writeAsString('');

    return LauncherUpdateIndexResult(
      itemCount: items.length,
      appArchiveUrl: baseUri.resolve('app-archive.json').toString(),
    );
  }

  Future<_AssetDigest> _assetDigest(GitHubAsset asset) async {
    final stream = await _client.openAsset(asset);
    var length = 0;
    final digest = await sha256
        .bind(
          stream.map((chunk) {
            length += chunk.length;
            return chunk;
          }),
        )
        .single;
    return _AssetDigest(sha256: digest.toString(), length: length);
  }
}
