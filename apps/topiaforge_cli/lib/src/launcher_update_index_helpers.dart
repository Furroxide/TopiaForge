part of 'launcher_update_index_builder.dart';

const _platforms = ['windows', 'macos', 'linux'];
const _maxManualReleaseAssetBytes = 4 * 1024 * 1024 * 1024;

Map<String, GitHubAsset> _assetsByPlatform(List<GitHubAsset> assets) {
  final result = <String, GitHubAsset>{};
  for (final asset in assets) {
    final platform = _assetPlatform(asset.name);
    if (platform == null) {
      continue;
    }
    final existing = result[platform];
    if (existing != null) {
      throw StateError(
        'Release has multiple production assets for $platform: '
        '${existing.name} and ${asset.name}.',
      );
    }
    _requirePublicHttpsUri(asset.browserDownloadUrl, label: asset.name);
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

Future<void> _writeJsonFile(String path, Object? value) async {
  final file = File(path);
  await file.parent.create(recursive: true);
  final json = const JsonEncoder.withIndent('  ').convert(value);
  await file.writeAsString('$json\n');
}

Uri _normalizeBaseUri(String baseUrl) {
  final normalized = baseUrl.trim().endsWith('/')
      ? baseUrl.trim()
      : '${baseUrl.trim()}/';
  return _requirePublicHttpsUri(normalized, label: 'baseUrl');
}

Uri _requirePublicHttpsUri(String value, {required String label}) {
  final uri = Uri.tryParse(value.trim());
  if (uri == null ||
      !uri.isAbsolute ||
      uri.scheme != 'https' ||
      uri.host.isEmpty ||
      uri.userInfo.isNotEmpty ||
      uri.hasQuery ||
      uri.hasFragment) {
    throw ArgumentError.value(
      value,
      label,
      'Must be an absolute HTTPS URL without credentials, query, or fragment.',
    );
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
