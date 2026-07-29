part of '../local_launcher_repository.dart';

const _maxRegistryDocumentBytes = 16 * 1024 * 1024;

class _SourceDocument {
  const _SourceDocument({required this.content, required this.baseUri});

  final String content;
  final Uri baseUri;
}

class _RegistryLoadOutcome {
  const _RegistryLoadOutcome({
    required this.mods,
    required this.candidates,
    required this.statuses,
  });

  final List<RegistryMod> mods;
  final List<RegistryMod> candidates;
  final List<PackageSourceStatus> statuses;
}

Map<String, Object?> _objectMap(Object? value) {
  if (value is! Map) {
    return const {};
  }
  return value.map((key, mapValue) => MapEntry(key.toString(), mapValue));
}
