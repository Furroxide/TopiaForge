part of 'manifest_migration.dart';

const _compatibilityRanges = [
  'supportedGameVersionRange',
  'supportedLoaderVersionRange',
  'supportedSdkVersionRange',
];

extension _MigrationGuards on _MigrationWork {
  void _validateCommon(
    Map<String, Object?> map, {
    bool allowMissingRanges = false,
  }) {
    void rejectNull(Object? value, String path) {
      if (value == null) {
        issue('invalid-null', path, 'Explicit null is not permitted here.');
        return;
      }
      if (value is Map) {
        for (final entry in value.entries) {
          rejectNull(entry.value, '$path/${entry.key}');
        }
      } else if (value is List) {
        for (var i = 0; i < value.length; i++) {
          rejectNull(value[i], '$path/$i');
        }
      }
    }

    for (final field in _compatibilityRanges) {
      if (map.containsKey(field) &&
          (map[field] is! String || (map[field] as String).trim().isEmpty)) {
        issue(
          'invalid-range',
          '/$field',
          'A present compatibility range must be an explicit nonempty string; repair it instead of defaulting to any version.',
        );
      }
    }
    for (final entry in map.entries) {
      if (!entry.key.startsWith('x-')) rejectNull(entry.value, '/${entry.key}');
    }
    try {
      final manifest = ModManifest.fromJson(map);
      for (final validation in manifest.validate().where(
        (value) => value.isBlocking,
      )) {
        if (allowMissingRanges &&
            _compatibilityRanges.any(
              (field) =>
                  !map.containsKey(field) && validation.message.contains(field),
            )) {
          continue;
        }
        issue('invalid-manifest', '', validation.message);
      }
    } on Object catch (error) {
      issue(
        'invalid-manifest',
        '',
        'Manifest fields cannot be decoded: $error',
      );
    }
  }

  void _validateMigrationCollections(Map<String, Object?> map) {
    if (map.containsKey(r'$schema') &&
        (map[r'$schema'] is! String ||
            (map[r'$schema'] as String).runes.length > 512)) {
      issue(
        'malformed-schema-pointer',
        r'/$schema',
        'The original schema pointer must be a string of at most 512 Unicode characters before its mechanical update.',
      );
    }
    for (final field in [
      'loadAfter',
      'loadBefore',
      'tags',
      'capabilities',
      'permissions',
      'platforms',
      'architectures',
      'contentTargets',
      'screenshots',
      'licenseFiles',
      'apiAssemblies',
    ]) {
      if (!map.containsKey(field)) continue;
      final raw = map[field];
      if (raw is! List) {
        issue('malformed-array', '/$field', 'Expected an array of strings.');
        continue;
      }
      final seen = <String>{};
      for (var index = 0; index < raw.length; index++) {
        final value = raw[index];
        if (value is! String || value.isEmpty || !seen.add(value)) {
          issue(
            'malformed-array-entry',
            '/$field/$index',
            'Expected a distinct nonempty string at the original array index.',
            index: index,
            id: value is String ? value : null,
          );
        }
      }
    }
    if (!map.containsKey('conflicts')) return;
    final raw = map['conflicts'];
    if (raw is! List) {
      issue('malformed-conflicts', '/conflicts', 'Expected a conflict array.');
      return;
    }
    final seen = <String>{};
    for (var index = 0; index < raw.length; index++) {
      final value = raw[index];
      final id = value is Map && value['id'] is String
          ? value['id'] as String
          : null;
      final path = '/conflicts/$index';
      if (value is! Map<String, Object?> ||
          id == null ||
          !ModManifest.isValidId(id) ||
          !seen.add(id.toLowerCase())) {
        issue(
          'malformed-conflict',
          path,
          'Expected a conflict object with a valid, unique string ID.',
          index: index,
          id: id,
        );
        continue;
      }
      final allowed = {
        'id',
        'versionRange',
        'reason',
        if (version == 3) 'version',
      };
      if (value.keys.any((key) => !allowed.contains(key))) {
        issue(
          'malformed-conflict',
          path,
          'Unexpected legacy conflict field.',
          index: index,
          id: id,
        );
      }
      for (final field in ['versionRange', if (version == 3) 'version']) {
        if (!value.containsKey(field)) continue;
        final range = value[field];
        if (range is! String ||
            range.trim().isEmpty ||
            range.runes.length > 256) {
          issue(
            'conflict-range',
            '$path/$field',
            'A present conflict range must be a nonempty string of at most 256 characters.',
            index: index,
            id: id,
          );
        } else {
          try {
            if (version == 3) {
              _legacyRange(range);
            } else {
              VersionRange.parse(range);
            }
          } on FormatException catch (error) {
            issue(
              'conflict-range',
              '$path/$field',
              error.message,
              index: index,
              id: id,
            );
          }
        }
      }
      if (value.containsKey('reason') &&
          (value['reason'] is! String ||
              (value['reason'] as String).runes.length > 512)) {
        issue(
          'malformed-conflict',
          '$path/reason',
          'A present conflict reason must be a string of at most 512 Unicode characters.',
          index: index,
          id: id,
        );
      }
    }
  }

  List<Map<String, Object?>> _legacyModes(Map<String, Object?> map) {
    if (!map.containsKey('worldGamemodes')) return [];
    final raw = map['worldGamemodes'];
    if (raw is! List || raw.length > 64) {
      issue(
        'malformed-modes',
        '/worldGamemodes',
        'Expected an array with at most 64 legacy gamemodes.',
      );
      return [];
    }
    final result = <Map<String, Object?>>[];
    final seen = <String>{};
    for (var index = 0; index < raw.length; index++) {
      final entry = raw[index];
      final path = '/worldGamemodes/$index';
      final id = entry is Map && entry['id'] is String
          ? entry['id'] as String
          : null;
      if (entry is! Map<String, Object?>) {
        issue(
          'malformed-mode',
          path,
          'Expected a legacy gamemode object.',
          index: index,
        );
        continue;
      }
      if (entry.keys.any(
        (key) => !const ['id', 'name', 'description'].contains(key),
      )) {
        issue(
          'malformed-mode',
          path,
          'Legacy entries permit only id, name and description.',
          index: index,
          id: id,
        );
      }
      if (id == null ||
          !RegExp(r'^[A-Za-z0-9][A-Za-z0-9_.-]{1,63}$').hasMatch(id)) {
        issue(
          'malformed-mode',
          '$path/id',
          'Legacy ID must be a 2–64-character ASCII ID under the source contract.',
          index: index,
          id: id,
        );
      } else if (!seen.add(id.toLowerCase())) {
        issue(
          'duplicate-mode',
          '$path/id',
          'Duplicate legacy gamemode ID.',
          index: index,
          id: id,
        );
      }
      final name = entry['name'];
      if (name is! String || name.isEmpty || name.runes.length > 128) {
        issue(
          'malformed-mode',
          '$path/name',
          'Legacy name must be a nonempty string of at most 128 Unicode characters.',
          index: index,
          id: id,
        );
      }
      if (entry.containsKey('description') &&
          (entry['description'] is! String ||
              (entry['description'] as String).runes.length > 1024)) {
        issue(
          'malformed-mode',
          '$path/description',
          'Legacy description must be a string of at most 1024 Unicode characters.',
          index: index,
          id: id,
        );
      }
      result.add(entry);
    }
    return result;
  }
}
