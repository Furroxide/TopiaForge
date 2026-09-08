part of 'manifest_migration.dart';

extension _MigrationNormalization on _MigrationWork {
  void _normalizeV3(Map<String, Object?> map) {
    final required = <String, Object?>{}, optional = <String, Object?>{};
    final seen = <String>{};
    void add(
      String id,
      Object? range,
      String path,
      int? index,
      bool isOptional,
    ) {
      if (!ModManifest.isValidId(id) || !seen.add(id.toLowerCase())) {
        issue(
          'dependency-identity',
          path,
          'Dependency IDs must be valid and unique across required and optional collections.',
          index: index,
          id: id,
        );
        return;
      }
      if (range is! String || range.trim().isEmpty) {
        issue(
          'dependency-range',
          path,
          'An explicit, nonempty dependency range is required.',
          index: index,
          id: id,
        );
        return;
      }
      try {
        final normalized = _legacyRange(range);
        (isOptional ? optional : required)[id] = normalized;
      } on FormatException catch (error) {
        issue('dependency-range', path, error.message, index: index, id: id);
      }
    }

    void read(String field, bool isOptional) {
      if (!map.containsKey(field)) return;
      final raw = map[field];
      if (raw is Map<String, Object?>) {
        if (raw.length > 128) {
          issue(
            'dependency-limit',
            '/$field',
            'Dependencies cannot exceed 128 entries.',
          );
        }
        for (final entry in raw.entries) {
          add(entry.key, entry.value, '/$field/${entry.key}', null, isOptional);
        }
      } else if (raw is List && field != 'vpmDependencies') {
        if (raw.length > 128) {
          issue(
            'dependency-limit',
            '/$field',
            'Dependencies cannot exceed 128 entries.',
          );
        }
        for (var index = 0; index < raw.length; index++) {
          final entry = raw[index], path = '/$field/$index';
          final id = entry is Map && entry['id'] is String
              ? entry['id'] as String
              : null;
          if (entry is! Map<String, Object?> ||
              id == null ||
              entry.keys.any(
                (key) => !const [
                  'id',
                  'version',
                  'versionRange',
                  'optional',
                ].contains(key),
              )) {
            issue(
              'malformed-dependency',
              path,
              'Expected a dependency object with string id and explicit version or versionRange.',
              index: index,
              id: id,
            );
            continue;
          }
          if (entry.containsKey('optional') &&
              (entry['optional'] is! bool ||
                  (isOptional && entry['optional'] == false))) {
            issue(
              'malformed-dependency',
              '$path/optional',
              'The optional flag must be a boolean consistent with its collection.',
              index: index,
              id: id,
            );
            continue;
          }
          if (entry.containsKey('version') &&
              entry.containsKey('versionRange') &&
              entry['version'] != entry['versionRange']) {
            issue(
              'dependency-range',
              path,
              'Conflicting version and versionRange values require an author decision.',
              index: index,
              id: id,
            );
            continue;
          }
          add(
            id,
            entry.containsKey('versionRange')
                ? entry['versionRange']
                : entry['version'],
            path,
            index,
            isOptional || entry['optional'] == true,
          );
        }
      } else {
        issue(
          'malformed-dependencies',
          '/$field',
          'Expected a dependency map${field == 'vpmDependencies' ? '' : ' or array'}.',
        );
      }
    }

    read('vpmDependencies', false);
    read('dependencies', false);
    read('optionalDependencies', true);
    if (map.containsKey('vpmDependencies') || map.containsKey('dependencies')) {
      map['dependencies'] = required;
    }
    if (map.containsKey('optionalDependencies') || optional.isNotEmpty) {
      map['optionalDependencies'] = optional;
    }
    map.remove('vpmDependencies');
    List<String>? strings(String field) {
      if (!map.containsKey(field)) return null;
      final raw = map[field];
      if (raw is! List ||
          raw.length > 64 ||
          raw.any((v) => v is! String || v.isEmpty) ||
          raw.toSet().length != raw.length) {
        issue(
          'malformed-capabilities',
          '/$field',
          'Expected at most 64 distinct nonempty strings.',
        );
        return null;
      }
      return raw.cast<String>();
    }

    final permissions = strings('permissions'),
        capabilities = strings('capabilities');
    if (permissions != null) {
      map['capabilities'] = [
        ...?capabilities,
        ...permissions.where((p) => capabilities?.contains(p) != true),
      ];
    }
    map.remove('permissions');
    if (map.containsKey('conflicts')) {
      final raw = map['conflicts'];
      if (raw is! List || raw.length > 128) {
        issue(
          'malformed-conflicts',
          '/conflicts',
          'Expected an array of at most 128 conflicts.',
        );
      } else {
        for (var index = 0; index < raw.length; index++) {
          final item = raw[index];
          final id = item is Map && item['id'] is String
              ? item['id'] as String
              : null;
          if (item is! Map<String, Object?>) {
            issue(
              'malformed-conflict',
              '/conflicts/$index',
              'Expected a conflict object.',
              index: index,
            );
            continue;
          }
          if (item.containsKey('version')) {
            if (item['version'] is! String ||
                (item.containsKey('versionRange') &&
                    item['versionRange'] != item['version'])) {
              issue(
                'conflict-range',
                '/conflicts/$index',
                'Conflicting or malformed version aliases require repair.',
                index: index,
                id: id,
              );
              continue;
            }
            item['versionRange'] = item.remove('version');
          }
          final range = item['versionRange'];
          if (range is String && range.trim().isNotEmpty) {
            try {
              item['versionRange'] = _legacyRange(range);
            } on FormatException {
              /* Raw guard reports the original index. */
            }
          }
        }
      }
    }
  }
}

String _legacyRange(String value) {
  try {
    VersionRange.parse(value);
    return value;
  } on FormatException {
    if (value.length < 2 || (value[0] != '^' && value[0] != '~')) rethrow;
    final minimum = SemanticVersion.tryParse(value.substring(1));
    if (minimum == null) {
      throw const FormatException('Invalid V3 dependency version range.');
    }
    final maximum = value[0] == '~'
        ? minimum.incrementMinor()
        : minimum.majorNumber.isPositive
        ? minimum.incrementMajor()
        : minimum.minorNumber.isPositive
        ? minimum.incrementMinor()
        : minimum.incrementPatch();
    return '>=$minimum <$maximum';
  }
}
