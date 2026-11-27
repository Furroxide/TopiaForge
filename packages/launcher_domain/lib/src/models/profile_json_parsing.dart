part of '../models.dart';

Map<String, Object?> _profileObject(Map<String, Object?> json, String field) {
  if (!json.containsKey(field)) return const {};
  final value = json[field];
  if (value is! Map || value.keys.any((key) => key is! String)) {
    throw FormatException('Profile $field must be an object.');
  }
  return Map<String, Object?>.from(value);
}

bool _profileBoolean(Map<String, Object?> json, String field) {
  if (!json.containsKey(field)) return false;
  final value = json[field];
  if (value is! bool) {
    throw FormatException('Profile $field must be a boolean.');
  }
  return value;
}

List<String> _profileStrings(
  Map<String, Object?> json,
  String field, {
  bool packageIds = false,
}) {
  if (!json.containsKey(field)) return const [];
  final value = json[field];
  if (value is! List || (packageIds && value.length > 4096)) {
    throw FormatException('Profile $field must be a bounded string array.');
  }
  final result = <String>[];
  final seen = <String>{};
  for (var index = 0; index < value.length; index++) {
    final item = value[index];
    if (item is! String ||
        (packageIds &&
            (item.trim().isEmpty || !seen.add(item.toLowerCase())))) {
      throw FormatException(
        'Profile $field has an invalid or duplicate entry '
        'at original index $index.',
      );
    }
    result.add(item);
  }
  return List.unmodifiable(result);
}

Map<String, String> _profileStringMap(
  Map<String, Object?> json,
  String field, {
  bool packageIds = false,
}) {
  final value = _profileObject(json, field);
  if (packageIds && value.length > 4096) {
    throw FormatException('Profile $field exceeds its entry bound.');
  }
  final seen = <String>{};
  final result = <String, String>{};
  for (final entry in value.entries) {
    final text = entry.value;
    if (text is! String ||
        (packageIds &&
            (entry.key.trim().isEmpty || !seen.add(entry.key.toLowerCase())))) {
      throw FormatException(
        'Profile $field has an invalid or duplicate entry '
        'for ${entry.key}.',
      );
    }
    result[entry.key] = text;
  }
  return Map.unmodifiable(result);
}
