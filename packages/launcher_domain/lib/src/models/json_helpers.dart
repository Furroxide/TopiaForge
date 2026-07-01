part of '../models.dart';

bool _isUnsafeRelativePath(String value) {
  final normalized = value.replaceAll('\\', '/');
  return normalized.startsWith('/') ||
      RegExp(r'^[A-Za-z]:/').hasMatch(normalized) ||
      normalized.split('/').contains('..');
}

List<String> _stringList(Object? value) {
  if (value is! List) {
    return const [];
  }

  return value
      .whereType<Object>()
      .map((item) => item.toString())
      .where((item) => item.trim().isNotEmpty)
      .toList(growable: false);
}

Map<String, String> _stringMap(Object? value) {
  if (value is! Map) {
    return const {};
  }

  return value.map(
    (key, mapValue) => MapEntry(key.toString(), mapValue.toString()),
  );
}

Map<String, Object?> _objectMap(Object? value) {
  if (value is! Map) {
    return const {};
  }

  return value.map((key, mapValue) => MapEntry(key.toString(), mapValue));
}

List<ModDependency> _dependencyList(Object? value) {
  if (value is! List) {
    return const [];
  }

  return value
      .whereType<Map>()
      .map(
        (item) => ModDependency.fromJson(
          item.map((key, mapValue) => MapEntry(key.toString(), mapValue)),
        ),
      )
      .toList(growable: false);
}

List<ModConflict> _conflictList(Object? value) {
  if (value is! List) {
    return const [];
  }

  return value
      .whereType<Map>()
      .map(
        (item) => ModConflict.fromJson(
          item.map((key, mapValue) => MapEntry(key.toString(), mapValue)),
        ),
      )
      .toList(growable: false);
}
