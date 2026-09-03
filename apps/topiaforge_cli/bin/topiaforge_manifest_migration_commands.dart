part of 'topiaforge.dart';

extension _TopiaForgeManifestMigrationCommands on _TopiaForgeCli {
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
    final rawSchemaVersion = map['schemaVersion'];
    final schemaVersion = rawSchemaVersion is int ? rawSchemaVersion : null;
    // Both supported contracts are readable, so neither is something to migrate
    // away from here. The V5 to V6 path is its own change, with its own refusals
    // for the parts no tool can derive.
    if (schemaVersion != null &&
        ModManifest.isSupportedSchemaVersion(schemaVersion)) {
      stdout.writeln(
        'topiaforge.mod.json already uses supported schema V$schemaVersion.',
      );
      return 0;
    }
    if (schemaVersion != 3 && schemaVersion != 4) {
      throw StateError(
        'Only schema V3 or retired V4 manifests can be migrated; found ${schemaVersion ?? 'no schemaVersion'}.',
      );
    }

    if (schemaVersion == 4) {
      _dropRetiredWorldGamemodes(map);
      map
        ..['schemaVersion'] = ModManifest.currentSchemaVersion
        ..[r'$schema'] = ModManifest.canonicalSchemaUrl;
      final migrated = ModManifest.fromJson(map);
      final issues = migrated.validate();
      if (issues.any((issue) => issue.isBlocking)) {
        stderr.writeln('The V4 manifest could not be migrated automatically:');
        _printIssues(issues);
        return 1;
      }
      await developerRepository.updateModManifest(projectPath, migrated);
      stdout.writeln(
        'Migrated topiaforge.mod.json from retired schema V4 to '
        'V${ModManifest.currentSchemaVersion}.',
      );
      return 0;
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

    _dropRetiredWorldGamemodes(map);
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
      ..['schemaVersion'] = ModManifest.currentSchemaVersion
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
    stdout.writeln(
      'Migrated topiaforge.mod.json from schema V3 to '
      'V${ModManifest.currentSchemaVersion}.',
    );
    stdout.writeln(
      'Review any compatibility range defaulted to * before publishing.',
    );
    return 0;
  }

  /// Removes the retired `worldGamemodes` list, reporting every entry it drops.
  ///
  /// A V5 entry was an id, a name and a description. A V6 gamemode declaration
  /// additionally names the type that implements it, the worlds it can run in and
  /// what a scene change means -- none of which is anywhere in the old document.
  /// The tool could produce a declaration that validates, but it would be one that
  /// binds to nothing and fails at first launch, so it says what it dropped and
  /// leaves the author to write the part only they know.
  void _dropRetiredWorldGamemodes(Map<String, Object?> map) {
    final retired = _jsonMapList(map.remove('worldGamemodes'));
    if (retired.isEmpty) {
      return;
    }

    stdout.writeln(
      'Dropped ${retired.length} worldGamemodes '
      '${retired.length == 1 ? 'entry' : 'entries'}: schema '
      'V${ModManifest.currentSchemaVersion} needs an implementation type, world '
      'requirements and a launch target, none of which the old manifest records. '
      'Declare each one under contributions.gamemodes:',
    );
    for (final gamemode in retired) {
      final id = gamemode['id']?.toString() ?? '(no id)';
      final name = gamemode['name']?.toString() ?? '';
      stdout.writeln('  - $id${name.isEmpty ? '' : ' ($name)'}');
    }
  }
}
