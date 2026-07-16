part of '../models.dart';

String? _portableManifestPathCollisionKey(String value) {
  if (value.runes.length > 1024 ||
      value.contains('\\') ||
      value.startsWith('/') ||
      value.trim().isEmpty) {
    return null;
  }
  try {
    if (unicode.nfc(value) != value) {
      return null;
    }
    final folded = <String>[];
    for (final segment in value.split('/')) {
      if (segment.runes.length > 255 || _isUnsafePortablePathSegment(segment)) {
        return null;
      }
      final key = unicode
          .nfkc(segment)
          .toUpperCase()
          .replaceAll('\u00df', 'SS')
          .replaceAll('\u1e9e', 'SS');
      if (key.contains('/') ||
          key.contains('\\') ||
          _isUnsafePortablePathSegment(key)) {
        return null;
      }
      folded.add(key);
    }
    return folded.join('/');
  } on Object {
    return null;
  }
}

bool _isUnsafePortablePathSegment(String segment) {
  if (segment.isEmpty ||
      segment == '.' ||
      segment == '..' ||
      segment.contains(':') ||
      segment.endsWith(' ') ||
      segment.endsWith('.') ||
      segment.codeUnits.any((unit) => unit < 0x20)) {
    return true;
  }
  final deviceName = segment.split('.').first.toLowerCase();
  return _windowsReservedDeviceNames.contains(deviceName);
}

const _windowsReservedDeviceNames = {
  'con',
  'prn',
  'aux',
  'nul',
  'com1',
  'com2',
  'com3',
  'com4',
  'com5',
  'com6',
  'com7',
  'com8',
  'com9',
  'lpt1',
  'lpt2',
  'lpt3',
  'lpt4',
  'lpt5',
  'lpt6',
  'lpt7',
  'lpt8',
  'lpt9',
};

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
