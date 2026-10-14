part of '../local_launcher_repository.dart';

const _maxRegistryDocumentBytes = 16 * 1024 * 1024;

class _SourceDocument {
  const _SourceDocument({required this.content, required this.baseUri});

  final String content;
  final Uri baseUri;
}

/// What one source offers: its current entries, which become catalog tiles
/// and dependency candidates, and the history rows that carry a manifest,
/// which only fill exact versions no current entry offers.
class _RegistrySourceMods {
  const _RegistrySourceMods({required this.current, this.history = const []});

  final List<RegistryMod> current;
  final List<RegistryMod> history;
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
