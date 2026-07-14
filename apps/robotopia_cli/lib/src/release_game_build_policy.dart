List<String> validateRobotopiaGameBuildMetadata({
  required Map<String, Object?> metadata,
  required int policyBuildId,
  required bool requireLatestAtRelease,
  required Map<String, Object?> baseline,
}) {
  final issues = <String>[];
  if (metadata['buildId'] != policyBuildId || policyBuildId != 2227) {
    issues.add('Game build metadata must be pinned to build 2227.');
  }
  if (!requireLatestAtRelease) {
    issues.add(
      'Release policy must require the pinned game build to remain latest.',
    );
  }
  if (metadata['sourcePlatform'] != 'windows') {
    issues.add('Game build sourcePlatform must be windows.');
  }
  for (final key in const ['baseUrl', 'manifestUrl']) {
    final raw = metadata[key] as String? ?? '';
    final uri = Uri.tryParse(raw);
    if (uri == null ||
        uri.scheme != 'https' ||
        uri.host.isEmpty ||
        uri.userInfo.isNotEmpty ||
        uri.query.isNotEmpty ||
        uri.fragment.isNotEmpty) {
      issues.add('Game build $key must be a credential-free HTTPS URL.');
    }
  }
  final archivesValue = metadata['archives'];
  final archives = archivesValue is Map
      ? archivesValue.map((key, value) => MapEntry(key.toString(), value))
      : <String, Object?>{};
  if (!_sameStringSet(archives.keys.toSet(), const {'windows', 'mac'})) {
    issues.add('Game build archives must contain exactly windows and mac.');
  }
  const expectedPaths = {
    'windows': 'Robotopia-v02227-Win64.7z',
    'mac': 'Robotopia-v02227-Mac.7z',
  };
  for (final platform in expectedPaths.keys) {
    final value = archives[platform];
    if (value is! Map) {
      issues.add('Game build archive $platform is missing.');
      continue;
    }
    final archive = value.map((key, value) => MapEntry(key.toString(), value));
    if (!_sameStringSet(archive.keys.toSet(), const {'path', 'sha256'}) ||
        archive['path'] != expectedPaths[platform] ||
        !_isSha256Value(archive['sha256'])) {
      issues.add(
        'Game build archive $platform must have the expected build-2227 path and SHA-256.',
      );
    }
  }
  if (baseline['gameVersionLabel'] != 'build 2227' ||
      baseline['gameVersion'] != '0.0.2227') {
    issues.add('Compatibility baseline must identify build 2227 as 0.0.2227.');
  }
  return issues;
}

bool _isSha256Value(Object? value) =>
    value is String && RegExp(r'^[0-9a-f]{64}$').hasMatch(value);

bool _sameStringSet(Set<String> left, Set<String> right) =>
    left.length == right.length && left.containsAll(right);
