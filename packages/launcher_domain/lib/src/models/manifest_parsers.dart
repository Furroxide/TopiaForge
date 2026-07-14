part of '../models.dart';

List<ModDependency> _vpmDependencyList(Object? value) {
  if (value is! Map) {
    return const [];
  }

  return value.entries
      .map(
        (entry) => ModDependency(
          id: entry.key.toString(),
          versionRange: VersionRange.parse(entry.value?.toString()),
        ),
      )
      .toList(growable: false);
}

List<GamemodeDefinition> _gamemodeList(Object? value) {
  if (value is! List) {
    return const [];
  }

  return value
      .whereType<Map>()
      .map((item) => GamemodeDefinition.fromJson(_objectMap(item)))
      .where((item) => item.id.trim().isNotEmpty && item.name.trim().isNotEmpty)
      .toList(growable: false);
}
