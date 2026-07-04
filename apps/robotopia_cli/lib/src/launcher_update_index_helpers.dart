part of 'launcher_update_index_builder.dart';

const _appName = 'QuantumWorks';
const _packageId = 'com.quantumworks.robotopia.launcher';
const _minimumUpdaterVersion = '2.4.2';
const _channels = ['release', 'beta', 'nightly'];
const _platforms = ['windows', 'macos', 'linux'];

Map<String, Object?> _descriptor({
  required LauncherUpdateIndexConfig config,
  required String generatedAt,
  required _ReleaseVersion version,
  required String platform,
  required String channel,
  required GitHubAsset asset,
  required _AssetDigest artifact,
}) {
  final descriptor = <String, Object?>{
    'schemaVersion': 3,
    'packageId': config.packageId,
    'appName': _descriptorAppName(config.appName, platform),
    'version': version.version,
  };
  if (version.buildNumber != null) {
    descriptor['buildNumber'] = version.buildNumber;
  }
  descriptor.addAll({
    'platform': platform,
    'channel': channel,
    'artifact': <String, Object?>{
      'kind': 'zip',
      'url': asset.browserDownloadUrl,
      'sha256': artifact.sha256,
      'length': artifact.length,
    },
    'install': <String, Object?>{
      'strategy': platform == 'macos'
          ? 'wholeBundleReplace'
          : 'wholeDirectoryReplace',
    },
    'minimumUpdaterVersion': config.minimumUpdaterVersion,
    'generatedAt': generatedAt,
  });
  return descriptor;
}

String _descriptorAppName(String appName, String platform) {
  if (platform == 'macos' && !appName.endsWith('.app')) {
    return '$appName.app';
  }
  return appName;
}

Map<String, Object?> _archiveItem({
  required _ReleaseVersion version,
  required String platform,
  required String channel,
  required bool mandatory,
  required String releaseUrl,
}) {
  final item = <String, Object?>{'version': version.version};
  if (version.buildNumber != null) {
    item['buildNumber'] = version.buildNumber;
  }
  item.addAll({
    'platform': platform,
    'channel': channel,
    'mandatory': mandatory,
    'release': releaseUrl,
  });
  return item;
}

Map<String, Map<String, dynamic>> _emptyChannelsIndex() {
  return {
    for (final channel in _channels)
      channel: {'latest': <String, dynamic>{}, 'versions': <String, dynamic>{}},
  };
}

void _addIndexEntry({
  required Map<String, Map<String, dynamic>> channelsIndex,
  required Map<String, dynamic> versionsIndex,
  required String channel,
  required String version,
  required String platform,
  required String releaseUrl,
  required String artifactUrl,
  required String tagName,
  required String publishedAt,
}) {
  final channelVersions = channelsIndex[channel]!['versions'] as Map;
  final channelVersion = _mapAt(channelVersions, version);
  channelVersion[platform] = _indexPlatformEntry(
    releaseUrl: releaseUrl,
    artifactUrl: artifactUrl,
    tagName: tagName,
    publishedAt: publishedAt,
  );

  final versionChannels = _mapAt(versionsIndex, version);
  final versionChannel = _mapAt(versionChannels, channel);
  versionChannel[platform] = _indexPlatformEntry(
    releaseUrl: releaseUrl,
    artifactUrl: artifactUrl,
    tagName: tagName,
    publishedAt: publishedAt,
  );
}

Map<String, dynamic> _mapAt(Map<dynamic, dynamic> parent, String key) {
  final existing = parent[key];
  if (existing is Map<String, dynamic>) {
    return existing;
  }
  final value = <String, dynamic>{};
  parent[key] = value;
  return value;
}

Map<String, Object?> _indexPlatformEntry({
  required String releaseUrl,
  required String artifactUrl,
  required String tagName,
  required String publishedAt,
}) {
  return {
    'release': releaseUrl,
    'artifact': artifactUrl,
    'tagName': tagName,
    'publishedAt': publishedAt,
  };
}

void _setLatestChannelVersions(
  Map<String, Map<String, dynamic>> channelsIndex,
) {
  for (final channel in channelsIndex.keys) {
    final versions = channelsIndex[channel]!['versions'] as Map;
    if (versions.isEmpty) {
      continue;
    }
    final sorted = versions.keys.cast<String>().toList()
      ..sort(_compareVersionSort);
    final latest = sorted.last;
    final latestVersion = _ReleaseVersion.fromLabel(latest);
    channelsIndex[channel]!['latest'] = {
      'version': latestVersion.version,
      if (latestVersion.buildNumber != null)
        'buildNumber': latestVersion.buildNumber,
      'platforms': versions[latest],
    };
  }
}

Map<String, GitHubAsset> _assetsByPlatform(List<GitHubAsset> assets) {
  final result = <String, GitHubAsset>{};
  for (final asset in assets) {
    final platform = _assetPlatform(asset.name);
    if (platform == null || result.containsKey(platform)) {
      continue;
    }
    result[platform] = asset;
  }
  return result;
}

String? _assetPlatform(String name) {
  final lower = name.toLowerCase();
  if (!lower.endsWith('.zip')) {
    return null;
  }
  if (RegExp(r'(symbols?|debug|pdb|dsym)').hasMatch(lower)) {
    return null;
  }
  if (RegExp(
    r'(^|[-_.])(windows-x64|win-x64|windows|win64|win)([-_.]|$)',
  ).hasMatch(lower)) {
    return 'windows';
  }
  if (RegExp(r'(^|[-_.])(macos|mac|darwin|osx)([-_.]|$)').hasMatch(lower)) {
    return 'macos';
  }
  if (RegExp(r'(^|[-_.])linux([-_.]|$)').hasMatch(lower)) {
    return 'linux';
  }
  return null;
}

_ReleaseVersion? _releaseVersion(GitHubRelease release) {
  final pattern = RegExp(r'v?(\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)(?:\+(\d+))?');
  for (final candidate in [release.tagName, release.name]) {
    final match = pattern.firstMatch(candidate);
    if (match == null) {
      continue;
    }
    return _ReleaseVersion(
      version: match.group(1)!,
      buildNumber: int.tryParse(match.group(2) ?? ''),
    );
  }
  return null;
}

String _releaseChannel(GitHubRelease release) {
  final explicit = RegExp(
    r'^\s*update-channel:\s*(release|beta|nightly)\b',
    caseSensitive: false,
    multiLine: true,
  ).firstMatch(release.body);
  if (explicit != null) {
    return explicit.group(1)!.toLowerCase();
  }

  final label = '${release.tagName} ${release.name}'.toLowerCase();
  if (RegExp(r'\b(nightly|canary|dev)\b').hasMatch(label)) {
    return 'nightly';
  }
  if (RegExp(r'\b(alpha|beta|preview|pre|rc)\b').hasMatch(label)) {
    return 'beta';
  }
  if (release.prerelease) {
    return 'beta';
  }
  return 'release';
}

bool _isMandatoryRelease(GitHubRelease release) {
  return RegExp(
    r'^\s*mandatory-update:\s*true\b',
    caseSensitive: false,
    multiLine: true,
  ).hasMatch(release.body);
}

int _compareArchiveItems(
  Map<String, Object?> left,
  Map<String, Object?> right,
) {
  return _compareMany([
    left['channel'].toString().compareTo(right['channel'].toString()),
    left['platform'].toString().compareTo(right['platform'].toString()),
    _compareVersionSort(
      left['version'].toString(),
      right['version'].toString(),
    ),
    (left['buildNumber'] as int? ?? 0).compareTo(
      right['buildNumber'] as int? ?? 0,
    ),
  ]);
}

int _compareVersionSort(String left, String right) {
  final leftKey = _VersionSortKey.parse(left);
  final rightKey = _VersionSortKey.parse(right);
  return _compareMany([
    leftKey.major.compareTo(rightKey.major),
    leftKey.minor.compareTo(rightKey.minor),
    leftKey.patch.compareTo(rightKey.patch),
    leftKey.stability.compareTo(rightKey.stability),
    leftKey.buildNumber.compareTo(rightKey.buildNumber),
    left.compareTo(right),
  ]);
}

int _compareMany(List<int> values) {
  for (final value in values) {
    if (value != 0) {
      return value;
    }
  }
  return 0;
}

String _outputPath(Directory output, String descriptorPath) {
  return p.joinAll([output.path, ...descriptorPath.split('/')]);
}

Future<void> _writeJsonFile(String path, Object? value) async {
  final file = File(path);
  await file.parent.create(recursive: true);
  final json = const JsonEncoder.withIndent('  ').convert(value);
  await file.writeAsString('$json\n');
}

String _safePathSegment(String value) {
  return value.replaceAll(RegExp(r'[\\/:*?"<>|]'), '_');
}

Uri _normalizeBaseUri(String baseUrl) {
  final normalized = baseUrl.trim().endsWith('/')
      ? baseUrl.trim()
      : '${baseUrl.trim()}/';
  final uri = Uri.parse(normalized);
  if (!uri.hasScheme || uri.host.isEmpty) {
    throw ArgumentError.value(baseUrl, 'baseUrl', 'Must be an absolute URL.');
  }
  return uri;
}

String _defaultBaseUrl(String repository) {
  final parts = _repositoryParts(repository);
  return 'https://${parts.owner}.github.io/${parts.name}/';
}

void _validateRepository(String repository) {
  _repositoryParts(repository);
}

({String owner, String name}) _repositoryParts(String repository) {
  final parts = repository.split('/');
  if (parts.length != 2 || parts.any((part) => part.trim().isEmpty)) {
    throw ArgumentError.value(
      repository,
      'repository',
      'Expected GitHub repository in owner/name form.',
    );
  }
  return (owner: parts[0], name: parts[1]);
}

class _ReleaseVersion {
  const _ReleaseVersion({required this.version, required this.buildNumber});

  factory _ReleaseVersion.fromLabel(String label) {
    final match = RegExp(
      r'^(\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?)(?:\+(\d+))?$',
    ).firstMatch(label);
    if (match == null) {
      return _ReleaseVersion(version: label, buildNumber: null);
    }
    return _ReleaseVersion(
      version: match.group(1)!,
      buildNumber: int.tryParse(match.group(2) ?? ''),
    );
  }

  final String version;
  final int? buildNumber;

  String get releaseLabel =>
      buildNumber == null ? version : '$version+$buildNumber';
}

class _AssetDigest {
  const _AssetDigest({required this.sha256, required this.length});

  final String sha256;
  final int length;
}

class _VersionSortKey {
  const _VersionSortKey({
    required this.major,
    required this.minor,
    required this.patch,
    required this.stability,
    required this.buildNumber,
  });

  factory _VersionSortKey.parse(String version) {
    final match = RegExp(
      r'^(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+(\d+))?$',
    ).firstMatch(version);
    if (match == null) {
      return const _VersionSortKey(
        major: 0,
        minor: 0,
        patch: 0,
        stability: 0,
        buildNumber: 0,
      );
    }
    return _VersionSortKey(
      major: int.parse(match.group(1)!),
      minor: int.parse(match.group(2)!),
      patch: int.parse(match.group(3)!),
      stability: match.group(4) == null ? 1 : 0,
      buildNumber: int.tryParse(match.group(5) ?? '') ?? 0,
    );
  }

  final int major;
  final int minor;
  final int patch;
  final int stability;
  final int buildNumber;
}
