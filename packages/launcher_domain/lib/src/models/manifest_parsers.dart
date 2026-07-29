part of '../models.dart';

List<ModDependency> _dependencyMapList(Object? value, {bool optional = false}) {
  if (value is! Map) {
    if (value == null) return const [];
    throw const FormatException('Dependencies must be an id-to-range map.');
  }

  return value.entries
      .map(
        (entry) => ModDependency(
          id: entry.key.toString(),
          versionRange: VersionRange.parse(entry.value?.toString()),
          optional: optional,
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
      .toList(growable: false);
}
