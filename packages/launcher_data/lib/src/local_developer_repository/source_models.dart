part of '../local_developer_repository.dart';

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

  final SafeZipArchive archive;
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
