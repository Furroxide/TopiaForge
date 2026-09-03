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
    if (schemaVersion == ModManifest.manifestV5SchemaVersion) {
      return _migrateV5(args, file, projectPath, map);
    }
    if (schemaVersion != 3 && schemaVersion != 4) {
      throw StateError(
        'Only schema V3, V4 or V5 manifests can be migrated; found ${schemaVersion ?? 'no schemaVersion'}.',
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

  /// Migrates a V5 manifest, and refuses when the parts only an author knows are
  /// missing.
  ///
  /// Everything mechanical is carried: the version, the schema URL, every other
  /// field byte for byte, and `world-service` added to capabilities because a
  /// package that declares a launch surface owns world content at runtime.
  ///
  /// What it will not do is invent the rest. A V6 gamemode names the type that
  /// implements it, the worlds it can run in, and the target a player picks. A V5
  /// manifest records none of those: `entryType` is the mod class, not the
  /// factory; the world a gamemode started in lived in runtime configuration a
  /// player could edit; and the launch entry existed only in C#. A tool that
  /// guessed would produce a manifest that validates and then fails at first
  /// launch, which is worse than one that refuses and says what is missing.
  Future<int> _migrateV5(
    List<String> args,
    File file,
    String projectPath,
    Map<String, Object?> map,
  ) async {
    final retired = _jsonMapList(map['worldGamemodes']);
    final stub = args.contains('--stub');

    map
      ..remove('worldGamemodes')
      ..['schemaVersion'] = ModManifest.currentSchemaVersion
      ..[r'$schema'] = ModManifest.canonicalSchemaUrl;

    if (retired.isEmpty) {
      final migrated = ModManifest.fromJson(map);
      final issues = migrated.validate();
      if (issues.any((issue) => issue.isBlocking)) {
        stderr.writeln('The V5 manifest could not be migrated automatically:');
        _printIssues(issues);
        return 1;
      }
      await developerRepository.updateModManifest(projectPath, migrated);
      stdout.writeln(
        'Migrated topiaforge.mod.json from schema V5 to '
        'V${ModManifest.currentSchemaVersion}. It declared no gamemodes, so '
        'there was nothing that needed a human decision.',
      );
      return 0;
    }

    final capabilities = <String>{
      ..._jsonStringList(map['capabilities']),
      'world-service',
    };
    map['capabilities'] = capabilities.toList()..sort();

    stderr.writeln(
      'topiaforge.mod.json declares ${retired.length} '
      '${retired.length == 1 ? 'gamemode' : 'gamemodes'} that cannot be '
      'migrated automatically. Schema V${ModManifest.currentSchemaVersion} '
      'needs facts the V5 manifest never recorded:',
    );
    for (final gamemode in retired) {
      final id = gamemode['id']?.toString() ?? '(no id)';
      stderr.writeln('  $id');
      stderr.writeln(
        '    - implementation.type: the class implementing IGamemodeFactory. '
        'entryType is the mod, not the gamemode.',
      );
      stderr.writeln(
        '    - a launch target: its id, title, and the world it starts in. The '
        'world your mod launched into was runtime configuration, not a '
        'declaration, and TopiaForge will not promote a config default into a '
        'manifest.',
      );
      stderr.writeln(
        '    - worldRequirements.transitions: which of scene-replacement and '
        'additive-arena this gamemode can run under.',
      );
    }

    if (!stub) {
      stderr.writeln(
        'Nothing was written. Re-run with --stub to write the mechanical half '
        'plus an x-migration-todo block; the result still fails validation on '
        'purpose, so a half-migrated project cannot be packed by accident.',
      );
      return 1;
    }

    // The skeleton carries the three fields V5 recorded and omits the one it
    // never had. That is what makes the result genuinely unpublishable rather
    // than merely incomplete: the reader rejects a gamemode with no
    // implementation, naming the field, so the normal validation path tells the
    // author what to write next.
    map['contributions'] = {
      'gamemodes': [
        for (final gamemode in retired)
          {
            'id': gamemode['id']?.toString() ?? '',
            'name': gamemode['name']?.toString() ?? '',
            if (gamemode['description'] != null)
              'description': gamemode['description'].toString(),
          },
      ],
    };
    map['x-migration-todo'] = {
      'from': 'schemaVersion 5',
      'gamemodes': [
        for (final gamemode in retired)
          {
            'id': gamemode['id']?.toString() ?? '',
            'name': gamemode['name']?.toString() ?? '',
            if (gamemode['description'] != null)
              'description': gamemode['description'].toString(),
            'needs': const [
              'implementation.type',
              'launchTarget',
              'worldRequirements.transitions',
            ],
          },
      ],
    };
    await file.writeAsString(
      '${const JsonEncoder.withIndent('  ').convert(map)}\n',
    );
    stdout.writeln(
      'Wrote the mechanical half and an x-migration-todo block. The manifest '
      'does not validate yet, by design: fill in the entries above under '
      'contributions.gamemodes and contributions.launchTargets.',
    );
    return 1;
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
