part of 'topiaforge.dart';

/// `topiaforge mod ...` — manifest management after creation. Every schema-v4 field of `topiaforge.mod.json` can
/// be shown and edited from the terminal; edits are validated on write (same rules as `check package`).
extension _TopiaForgeModCommands on _TopiaForgeCli {
  static const _scalarFields = {
    'version': 'version',
    'display-name': 'displayName',
    'description': 'description',
    'license': 'license',
    'category': 'category',
    'icon': 'icon',
    'homepage': 'homepage',
    'source': 'source',
    'entry-assembly': 'entryAssembly',
    'entry-type': 'entryType',
    'author': 'author.name',
    'author-email': 'author.email',
    'author-url': 'author.url',
    'game-version-range': 'supportedGameVersionRange',
    'loader-version-range': 'supportedLoaderVersionRange',
    'sdk-version-range': 'supportedSdkVersionRange',
  };

  static const _listFields = {
    'tag': 'tags',
    'capability': 'capabilities',
    'load-after': 'loadAfter',
    'load-before': 'loadBefore',
    'screenshot': 'screenshots',
    'api-assembly': 'apiAssemblies',
  };

  Future<int> _mod(List<String> args) async {
    final sub = args.firstOrNull;
    switch (sub) {
      case 'show':
        return _modShow(args.skip(1).toList());
      case 'set':
        return _modSet(args.skip(1).toList());
      case 'bump':
        return _modBump(args.skip(1).toList());
      case 'add':
        return _modEditList(args.skip(1).toList(), add: true);
      case 'remove':
        return _modEditList(args.skip(1).toList(), add: false);
      default:
        stdout.writeln('Usage:');
        stdout.writeln('  topiaforge mod show [--project path]');
        stdout.writeln(
          '  topiaforge mod set <field> <value> [--project path]      '
          '(fields: ${_scalarFields.keys.join(', ')})',
        );
        stdout.writeln(
          '  topiaforge mod bump [major|minor|patch] [--project path]',
        );
        stdout.writeln(
          '  topiaforge mod add|remove tag|capability|load-after|load-before|screenshot|api-assembly <value> [--project path]',
        );
        stdout.writeln(
          '  topiaforge mod add|remove dependency|optional-dependency|conflict <id[@range]> [--project path]',
        );
        stdout.writeln(
          '  topiaforge mod add|remove gamemode <id:Name[:description]> [--project path]',
        );
        stdout.writeln(
          '  topiaforge mod add|remove <chronos|prompts|robotkit|ugc|worlds|interop-unity> [--project path]',
        );
        return sub == null ? 0 : 2;
    }
  }

  Future<int> _migrateManifest(List<String> args) async {
    final positional = args.where((arg) => !arg.startsWith('--')).toList();
    final projectPath =
        _option(args, '--project') ??
        positional.firstOrNull ??
        Directory.current.path;
    final file = File(p.join(projectPath, 'topiaforge.mod.json'));
    if (!file.existsSync()) {
      throw StateError('topiaforge.mod.json was not found in $projectPath.');
    }

    final map = readBoundedJsonObjectSync(
      file,
      maxBytes: CliFileLimits.manifest,
    );
    final schemaVersion = (map['schemaVersion'] as num?)?.toInt();
    if (schemaVersion == 4) {
      stdout.writeln('topiaforge.mod.json already uses schema V4.');
      return 0;
    }
    if (schemaVersion != 3) {
      throw StateError(
        'Only schema V3 manifests can be migrated; found ${schemaVersion ?? 'no schemaVersion'}.',
      );
    }

    final required = <String, Object?>{};
    final optional = <String, Object?>{};

    void readDependencyMap(Object? value, Map<String, Object?> destination) {
      if (value is! Map) return;
      for (final entry in value.entries) {
        destination[entry.key.toString()] = _migrateV3DependencyRange(
          entry.value?.toString() ?? '*',
        );
      }
    }

    void readDependencyList(
      Object? value,
      Map<String, Object?> defaultDestination,
    ) {
      if (value is! List) return;
      for (final raw in value.whereType<Map>()) {
        final item = raw.map((key, value) => MapEntry(key.toString(), value));
        final id = item['id']?.toString() ?? '';
        if (id.isEmpty) continue;
        final range =
            item['versionRange']?.toString() ??
            item['version']?.toString() ??
            '*';
        final destination = item['optional'] == true
            ? optional
            : defaultDestination;
        destination[id] = _migrateV3DependencyRange(range);
      }
    }

    readDependencyMap(map['vpmDependencies'], required);
    if (map['dependencies'] is Map) {
      readDependencyMap(map['dependencies'], required);
    } else {
      readDependencyList(map['dependencies'], required);
    }
    if (map['optionalDependencies'] is Map) {
      readDependencyMap(map['optionalDependencies'], optional);
    } else {
      readDependencyList(map['optionalDependencies'], optional);
    }
    for (final id in required.keys) {
      optional.remove(id);
    }

    final legacyPermissions = _jsonStringList(map['permissions']);
    map
      ..remove('vpmDependencies')
      ..remove('permissions')
      ..['dependencies'] = required
      ..['schemaVersion'] = 4
      ..[r'$schema'] = ModManifest.canonicalSchemaUrl;
    if (optional.isEmpty) {
      map.remove('optionalDependencies');
    } else {
      map['optionalDependencies'] = optional;
    }

    final capabilities = <String>{
      ..._jsonStringList(map['capabilities']),
      ...legacyPermissions,
    };
    if (capabilities.isNotEmpty) {
      map['capabilities'] = capabilities.toList()..sort();
    }

    final conflicts = _jsonMapList(map['conflicts']);
    for (final conflict in conflicts) {
      if (!conflict.containsKey('versionRange') &&
          conflict['version'] != null) {
        conflict['versionRange'] = conflict.remove('version');
      }
    }
    if (conflicts.isNotEmpty) map['conflicts'] = conflicts;

    for (final field in const [
      'supportedGameVersionRange',
      'supportedLoaderVersionRange',
      'supportedSdkVersionRange',
    ]) {
      map.putIfAbsent(field, () => '*');
    }

    final migrated = ModManifest.fromJson(map);
    final issues = migrated.validate();
    if (issues.any((issue) => issue.isBlocking)) {
      stderr.writeln('The V3 manifest could not be migrated automatically:');
      _printIssues(issues);
      return 1;
    }
    await developerRepository.updateModManifest(projectPath, migrated);
    stdout.writeln('Migrated topiaforge.mod.json from schema V3 to V4.');
    stdout.writeln(
      'Review any compatibility range defaulted to * before publishing.',
    );
    return 0;
  }

  /// Increments the manifest version through the same validated-write path
  /// as `mod set`, so a broken manifest can never be written.
  Future<int> _modBump(List<String> args) async {
    var part = 'patch';
    for (var index = 0; index < args.length; index++) {
      if (args[index].startsWith('--')) {
        index++; // Every bump flag takes one value.
        continue;
      }
      part = args[index];
      break;
    }
    if (!const {'major', 'minor', 'patch'}.contains(part)) {
      throw UsageError(
        'Usage: topiaforge mod bump [major|minor|patch] [--project path]',
      );
    }
    String? from;
    String? to;
    final code = await _mutateManifest(args, (map) {
      final current = (map['version'] as String?) ?? '';
      final parsed = SemanticVersion.tryParse(current);
      if (parsed == null) {
        throw StateError(
          'Current version "$current" is not a semantic version — fix it '
          'with `topiaforge mod set version <x.y.z>` first.',
        );
      }
      if (RegExp(r'[-+]').hasMatch(current)) {
        stdout.writeln(
          'Note: the pre-release/build suffix of "$current" is dropped.',
        );
      }
      final next = switch (part) {
        'major' => parsed.incrementMajor(),
        'minor' => parsed.incrementMinor(),
        _ => parsed.incrementPatch(),
      };
      from = current;
      to = next.toString();
      map['version'] = next.toString();
    });
    if (code == 0) {
      stdout.writeln('version: $from -> $to');
    }
    return code;
  }

  String _modProjectPath(List<String> args) =>
      _option(args, '--project') ?? Directory.current.path;

  Future<int> _modShow(List<String> args) async {
    final manifest = await developerRepository.readModManifest(
      _modProjectPath(args),
    );
    stdout.writeln(
      const JsonEncoder.withIndent('  ').convert(manifest.toJson()),
    );
    final issues = manifest.validate();
    if (issues.isNotEmpty) {
      stdout.writeln('');
      _printIssues(issues);
    }
    return issues.any((issue) => issue.isBlocking) ? 1 : 0;
  }

  Future<int> _modSet(List<String> args) async {
    final field = args.firstOrNull;
    final value = args.length > 1 ? args[1] : null;
    final jsonKey = _scalarFields[field];
    if (field == null || value == null || jsonKey == null) {
      throw UsageError(
        'Usage: topiaforge mod set <field> <value> [--project path]\n'
        'Fields: ${_scalarFields.keys.join(', ')}',
      );
    }
    if (field.endsWith('version-range')) {
      VersionRange.parse(value); // fail loudly on malformed ranges
    }
    return _mutateManifest(args, (map) {
      if (jsonKey.startsWith('author.')) {
        final author = Map<String, Object?>.of(
          map['author'] is Map
              ? (map['author'] as Map).cast<String, Object?>()
              : const <String, Object?>{},
        );
        author[jsonKey.substring('author.'.length)] = value;
        map['author'] = author;
      } else {
        map[jsonKey] = value;
      }
    });
  }

  Future<int> _modEditList(List<String> args, {required bool add}) async {
    var kind = args.firstOrNull;
    var value = args.length > 1 ? args[1] : null;
    if (kind != null &&
        _sdkModules.containsKey(kind) &&
        (value == null || value.startsWith('--'))) {
      value = kind;
      kind = 'module';
    }
    if (kind == null || value == null) {
      throw UsageError(
        'Usage: topiaforge mod ${add ? 'add' : 'remove'} <kind> <value> [--project path]\n'
        'Kinds: ${_listFields.keys.join(', ')}, dependency, optional-dependency, conflict, gamemode',
      );
    }

    final listKey = _listFields[kind];
    if (listKey != null) {
      return _mutateManifest(args, (map) {
        final items = [
          ..._jsonStringList(map[listKey]).where((item) => item != value),
          if (add) value,
        ];
        if (items.isEmpty) {
          map.remove(listKey);
        } else {
          map[listKey] = items;
        }
      });
    }

    switch (kind) {
      case 'module':
        final module = _sdkModules[value];
        if (module == null) {
          throw UsageError(
            'Unknown SDK module "$value". Available modules: '
            '${_sdkModules.keys.join(', ')}',
          );
        }
        return _mutateSdkModule(args, value, module, add: add);
      case 'dependency':
        final (id, range) = _splitSpec(value);
        return _mutateManifest(args, (map) {
          final deps = Map<String, Object?>.of(
            map['dependencies'] is Map
                ? (map['dependencies'] as Map).cast<String, Object?>()
                : const <String, Object?>{},
          );
          if (add) {
            deps[id] = range.toString();
          } else {
            deps.remove(id);
          }
          map['dependencies'] = deps;
        });
      case 'optional-dependency':
        final (id, range) = _splitSpec(value);
        return _mutateManifest(args, (map) {
          final items = Map<String, Object?>.of(
            map['optionalDependencies'] is Map
                ? (map['optionalDependencies'] as Map).cast<String, Object?>()
                : const <String, Object?>{},
          );
          if (add) {
            items[id] = range.toString();
          } else {
            items.remove(id);
          }
          if (items.isEmpty) {
            map.remove('optionalDependencies');
          } else {
            map['optionalDependencies'] = items;
          }
        });
      case 'conflict':
        final (id, range) = _splitSpec(value);
        return _mutateManifest(args, (map) {
          final items = _jsonMapList(
            map['conflicts'],
          ).where((item) => item['id'] != id).toList();
          if (add) {
            items.add(ModConflict(id: id, versionRange: range).toJson());
          }
          if (items.isEmpty) {
            map.remove('conflicts');
          } else {
            map['conflicts'] = items;
          }
        });
      case 'gamemode':
        final gamemode = _parseGamemodeSpec(value);
        return _mutateManifest(args, (map) {
          final items = _jsonMapList(
            map['worldGamemodes'],
          ).where((item) => item['id'] != gamemode.id).toList();
          if (add) {
            items.add(gamemode.toJson());
          }
          if (items.isEmpty) {
            map.remove('worldGamemodes');
          } else {
            map['worldGamemodes'] = items;
          }
        });
      default:
        throw UsageError('Unknown kind: $kind');
    }
  }

  /// Reads the manifest, applies [mutate] to its canonical JSON map, validates, and writes it back. Blocking
  /// validation issues abort the write.
  Future<int> _mutateManifest(
    List<String> args,
    void Function(Map<String, Object?> map) mutate,
  ) async {
    final projectPath = _modProjectPath(args);
    final manifest = await developerRepository.readModManifest(projectPath);
    final map = manifest.toJson();
    mutate(map);
    final updated = ModManifest.fromJson(map);
    final issues = updated.validate();
    if (issues.any((issue) => issue.isBlocking)) {
      stderr.writeln('Refusing to write an invalid manifest:');
      _printIssues(issues);
      return 1;
    }
    await developerRepository.updateModManifest(projectPath, updated);
    stdout.writeln('Updated topiaforge.mod.json.');
    _printIssues(issues);
    return 0;
  }

  (String, VersionRange) _splitSpec(String spec) {
    final at = spec.indexOf('@');
    if (at < 0) {
      return (spec, const VersionRange.any());
    }
    return (spec.substring(0, at), VersionRange.parse(spec.substring(at + 1)));
  }

  String _migrateV3DependencyRange(String value) {
    final range = value.trim();
    try {
      VersionRange.parse(range);
      return range.isEmpty ? '*' : range;
    } on FormatException {
      // V3's vpmDependencies name encouraged the VPM caret/tilde syntax.
      // Canonical V4 ranges use the framework's explicit comparator form.
      if (range.length < 2 || (range[0] != '^' && range[0] != '~')) {
        return range;
      }
      final minimum = SemanticVersion.tryParse(range.substring(1));
      if (minimum == null) return range;
      final maximum = range[0] == '~'
          ? minimum.incrementMinor()
          : minimum.majorNumber.isPositive
          ? minimum.incrementMajor()
          : minimum.minorNumber.isPositive
          ? minimum.incrementMinor()
          : minimum.incrementPatch();
      return '>=$minimum <$maximum';
    }
  }

  GamemodeDefinition _parseGamemodeSpec(String spec) {
    final parts = spec.split(':');
    if (parts.first.trim().isEmpty) {
      throw StateError('Gamemode spec must be <id:Name[:description]>.');
    }
    return GamemodeDefinition(
      id: parts[0].trim(),
      name: parts.length > 1 && parts[1].trim().isNotEmpty
          ? parts[1].trim()
          : parts[0].trim(),
      description: parts.length > 2 ? parts.sublist(2).join(':').trim() : '',
    );
  }

  List<String> _jsonStringList(Object? value) =>
      value is List ? value.map((item) => item.toString()).toList() : const [];

  List<Map<String, Object?>> _jsonMapList(Object? value) => value is List
      ? value
            .whereType<Map>()
            .map((item) => item.cast<String, Object?>())
            .toList()
      : const [];
}

final class _SdkModule {
  const _SdkModule({
    required this.packageId,
    this.runtimeDependency = '',
    this.capability = '',
  });

  final String packageId;
  final String runtimeDependency;
  final String capability;
}
