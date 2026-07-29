part of 'mod_registry_index_builder.dart';

void _addCollectedVersion(
  Map<String, List<_CollectedVersion>> byId,
  _CollectedVersion candidate,
) {
  final id = candidate.manifest.id.toLowerCase();
  final versions = byId.putIfAbsent(id, () => []);
  final duplicate = versions.where(
    (item) => item.manifest.version == candidate.manifest.version,
  );
  if (duplicate.isEmpty) {
    versions.add(candidate);
    return;
  }
  final existing = duplicate.single;
  if (existing.packageSha256 == candidate.packageSha256 &&
      _canonicalJson(existing.manifest.toJson()) ==
          _canonicalJson(candidate.manifest.toJson())) {
    return;
  }
  throw StateError(
    'Conflicting package bytes for ${candidate.manifest.id}@'
    '${candidate.manifest.version}: ${existing.sourceLabel} '
    '(${existing.packageSha256}) and ${candidate.sourceLabel} '
    '(${candidate.packageSha256}). Published versions are immutable.',
  );
}

Uri _requirePublicHttpsUrl(String value, {required String label}) {
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

class _CollectedVersion {
  _CollectedVersion({
    required this.manifest,
    required this.downloadUrl,
    required this.packageSha256,
    required this.sourceLabel,
    this.publishedAt = '',
  });

  final ModManifest manifest;
  final String downloadUrl;
  final String packageSha256;
  final String sourceLabel;
  final String publishedAt;
  String changelog = '';
}
