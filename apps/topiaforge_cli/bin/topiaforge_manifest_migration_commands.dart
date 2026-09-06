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
    if (schemaVersion == ModManifest.currentSchemaVersion) {
      final current = ModManifest.fromJson(map);
      final issues = current.validate();
      if (issues.any((issue) => issue.isBlocking)) {
        stderr.writeln('The V6 manifest is invalid; no files were changed:');
        _printIssues(issues);
        return 1;
      }
      stdout.writeln(
        'topiaforge.mod.json already uses supported schema V$schemaVersion.',
      );
      return 0;
    }
    // Slice 8 replaces this legacy mechanical migration with preservation/refusal
    // rules. Keep its existing V5 output explicit while new scaffolds use V6.
    if (schemaVersion == ModManifest.manifestV5SchemaVersion) {
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
      map
        ..['schemaVersion'] = ModManifest.manifestV5SchemaVersion
        ..[r'$schema'] = ModManifest.canonicalSchemaUrl.replaceFirst(
          'topiaforge.mod.schema.json',
          'topiaforge.mod.v5.schema.json',
        );
      final migrated = ModManifest.fromJson(map);
      final issues = migrated.validate();
      if (issues.any((issue) => issue.isBlocking)) {
        stderr.writeln('The V4 manifest could not be migrated automatically:');
        _printIssues(issues);
        return 1;
      }
      await developerRepository.updateModManifest(projectPath, migrated);
      stdout.writeln(
        'Migrated topiaforge.mod.json from retired schema V4 to V5.',
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
      ..['schemaVersion'] = ModManifest.manifestV5SchemaVersion
      ..[r'$schema'] = ModManifest.canonicalSchemaUrl.replaceFirst(
        'topiaforge.mod.schema.json',
        'topiaforge.mod.v5.schema.json',
      );
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
    stdout.writeln('Migrated topiaforge.mod.json from schema V3 to V5.');
    stdout.writeln(
      'Review any compatibility range defaulted to * before publishing.',
    );
    return 0;
  }
}
