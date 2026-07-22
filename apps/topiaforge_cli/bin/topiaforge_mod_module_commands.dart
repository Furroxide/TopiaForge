part of 'topiaforge.dart';

const _sdkModules = <String, _SdkModule>{
  'chronos': _SdkModule(
    packageId: 'TopiaForge.Mods.Chronos',
    runtimeDependency: 'io.github.furroxide.topiaforge.chronos',
  ),
  'prompts': _SdkModule(
    packageId: 'TopiaForge.Mods.Prompts',
    runtimeDependency: 'io.github.furroxide.topiaforge.prompts',
  ),
  'robotkit': _SdkModule(
    packageId: 'TopiaForge.Mods.RobotKit',
    runtimeDependency: 'io.github.furroxide.topiaforge.robotkit',
  ),
  'ugc': _SdkModule(
    packageId: 'TopiaForge.Mods.Ugc',
    runtimeDependency: 'io.github.furroxide.topiaforge.ugc.livesync',
  ),
  'worlds': _SdkModule(
    packageId: 'TopiaForge.Mods.Worlds',
    runtimeDependency: 'io.github.furroxide.topiaforge.worlds',
  ),
  'multiplayer': _SdkModule(
    packageId: 'TopiaForge.Mods.Multiplayer',
    additionalPackageIds: <String>['TopiaForge.Mods.Multiplayer.Generators'],
    runtimeDependency: 'io.github.furroxide.topiaforge.multiplayer',
    exactRuntimeDependency: true,
    managesMultiplayerManifest: true,
  ),
  'interop-unity': _SdkModule(
    packageId: 'TopiaForge.Mods.Interop.Unity',
    capability: 'unsafe-native',
  ),
};

extension _TopiaForgeModModuleCommands on _TopiaForgeCli {
  Future<int> _modSync(List<String> args) async {
    if (args.firstOrNull != 'multiplayer') {
      throw UsageError(
        'Usage: topiaforge mod sync multiplayer '
        '[--project path] [--configuration name]',
      );
    }
    final root = _findModRoot(_modProjectPath(args));
    final configuration = _option(args, '--configuration') ?? 'Release';
    if (!RegExp(r'^[A-Za-z0-9_.-]{1,64}$').hasMatch(configuration)) {
      throw UsageError('Invalid --configuration value.');
    }
    final path = await developerRepository.synchronizeMultiplayerContractLock(
      root.path,
      configuration: configuration,
    );
    stdout.writeln(
      'Synchronized ${p.basename(path)} from generated multiplayer contracts.',
    );
    return 0;
  }

  Future<int> _mutateSdkModule(
    List<String> args,
    String moduleName,
    _SdkModule module, {
    required bool add,
  }) async {
    final requestedPath = _modProjectPath(args);
    final root = _findModRoot(requestedPath);
    final manifestFile = File(p.join(root.path, 'topiaforge.mod.json'));
    final manifest = await developerRepository.readModManifest(root.path);
    final map = manifest.toJson();
    if (module.managesMultiplayerManifest) {
      if (add) {
        map.putIfAbsent(
          'multiplayer',
          () => <String, Object?>{
            'mode': 'session',
            'presence': 'required',
            'protocol': <String, Object?>{
              'version': '1.0.0',
              'peerVersionRange': '>=1.0.0 <2.0.0',
            },
          },
        );
      } else {
        map.remove('multiplayer');
      }
    }
    if (module.runtimeDependency.isNotEmpty) {
      final dependencies = Map<String, Object?>.of(
        map['dependencies'] is Map
            ? (map['dependencies'] as Map).cast<String, Object?>()
            : const <String, Object?>{},
      );
      if (add) {
        dependencies[module.runtimeDependency] = module.exactRuntimeDependency
            ? TopiaForgeRuntimeVersions.sdkVersion
            : '>=${TopiaForgeRuntimeVersions.sdkVersion} <2.0.0';
      } else {
        dependencies.remove(module.runtimeDependency);
      }
      map['dependencies'] = dependencies;
    }
    if (module.capability.isNotEmpty) {
      final capabilities = <String>{..._jsonStringList(map['capabilities'])};
      if (add) {
        capabilities.add(module.capability);
      } else {
        capabilities.remove(module.capability);
      }
      if (capabilities.isEmpty) {
        map.remove('capabilities');
      } else {
        map['capabilities'] = capabilities.toList()..sort();
      }
    }
    final updatedManifest = ModManifest.fromJson(map);
    final issues = updatedManifest.validate();
    if (issues.any((issue) => issue.isBlocking)) {
      stderr.writeln('Refusing to write an invalid manifest:');
      _printIssues(issues);
      return 1;
    }

    final projectFile = _findEntryProject(root, updatedManifest);
    final originalProject = readBoundedTextFileSync(
      projectFile,
      maxBytes: CliFileLimits.metadata,
    );
    var updatedProject = originalProject;
    for (final packageId in <String>[
      module.packageId,
      ...module.additionalPackageIds,
    ]) {
      updatedProject = _editSdkPackageReference(
        updatedProject,
        packageId: packageId,
        version: TopiaForgeRuntimeVersions.sdkVersion,
        add: add,
      );
    }
    final updates = <File, String?>{
      manifestFile:
          '${const JsonEncoder.withIndent('  ').convert(updatedManifest.toJson())}\n',
      projectFile: updatedProject,
    };
    if (module.managesMultiplayerManifest) {
      final contractLock = File(
        p.join(root.path, 'topiaforge.multiplayer.lock.json'),
      );
      final protocolVersion =
          updatedManifest.multiplayer?.protocol?.version ?? '';
      final initialContractLock = <String, Object?>{
        'schemaVersion': 2,
        if (protocolVersion.isNotEmpty) 'protocolVersion': protocolVersion,
        'contracts': <Object?>[],
      };
      updates[contractLock] = add
          ? (contractLock.existsSync()
                ? readBoundedTextFileSync(
                    contractLock,
                    maxBytes: CliFileLimits.metadata,
                  )
                : '${const JsonEncoder.withIndent('  ').convert(initialContractLock)}\n')
          : null;
    }
    await _writeModuleFilesAtomically(updates);
    if (add && module.managesMultiplayerManifest) {
      stdout.writeln(
        'Added SDK module $moduleName (${module.packageId}). '
        'Run `topiaforge restore`, then `topiaforge mod sync multiplayer`, '
        'and commit packages.lock.json, topiaforge.sdk.lock.json, and '
        'topiaforge.multiplayer.lock.json.',
      );
    } else {
      stdout.writeln(
        '${add ? 'Added' : 'Removed'} SDK module $moduleName '
        '(${module.packageId}). Run `topiaforge restore` to refresh the lock file.',
      );
    }
    _printIssues(issues);
    return 0;
  }

  Directory _findModRoot(String startPath) {
    var current = FileSystemEntity.isDirectorySync(startPath)
        ? Directory(startPath).absolute
        : File(startPath).absolute.parent;
    while (true) {
      if (File(p.join(current.path, 'topiaforge.mod.json')).existsSync()) {
        return current;
      }
      if (current.parent.path == current.path) {
        throw StateError('topiaforge.mod.json was not found from $startPath.');
      }
      current = current.parent;
    }
  }

  File _findEntryProject(Directory root, ModManifest manifest) {
    final assembly = p.basenameWithoutExtension(manifest.entryAssembly);
    final exact = File(p.join(root.path, '$assembly.csproj'));
    if (exact.existsSync()) return exact;
    final projects = root
        .listSync(recursive: true, followLinks: false)
        .whereType<File>()
        .where((file) => p.extension(file.path) == '.csproj')
        .where((file) => !p.basename(file.path).contains('.Tests.'))
        .where((file) => !p.split(file.path).contains('unity-companion'))
        .toList();
    if (projects.length != 1) {
      throw StateError(
        'Could not identify the entry C# project for ${manifest.entryAssembly}.',
      );
    }
    return projects.single;
  }

  String _editSdkPackageReference(
    String project, {
    required String packageId,
    required String version,
    required bool add,
  }) {
    final pattern = RegExp(
      '<PackageReference\\s+Include="$packageId"[^>]*(?:/>|>.*?</PackageReference>)',
      dotAll: true,
    );
    final match = pattern.firstMatch(project);
    if (!add) {
      if (match == null) return project;
      return project.replaceRange(match.start, match.end, '');
    }
    if (match != null) {
      var item = match.group(0)!;
      final versionPattern = RegExp(r'\sVersion="[^"]*"');
      item = versionPattern.hasMatch(item)
          ? item.replaceFirst(versionPattern, ' Version="$version"')
          : item.replaceFirst(
              '<PackageReference ',
              '<PackageReference Version="$version" ',
            );
      if (topiaForgeAnalyzerPackageIds.contains(packageId) &&
          !item.contains('PrivateAssets=')) {
        item = item.replaceFirst(
          '<PackageReference ',
          '<PackageReference PrivateAssets="all" ',
        );
      }
      return project.replaceRange(match.start, match.end, item);
    }
    final close = project.lastIndexOf('</Project>');
    if (close < 0) throw StateError('The C# project is malformed.');
    final privateAssets = topiaForgeAnalyzerPackageIds.contains(packageId)
        ? ' PrivateAssets="all"'
        : '';
    return '${project.substring(0, close)}  <ItemGroup>\n'
        '    <PackageReference Include="$packageId" Version="$version"$privateAssets />\n'
        '  </ItemGroup>\n${project.substring(close)}';
  }

  Future<void> _writeModuleFilesAtomically(Map<File, String?> updates) async {
    final originalTypes = <File, FileSystemEntityType>{};
    for (final file in updates.keys) {
      final type = FileSystemEntity.typeSync(file.path, followLinks: false);
      if (type != FileSystemEntityType.file &&
          type != FileSystemEntityType.notFound) {
        throw StateError('Refusing to replace a linked or special file.');
      }
      originalTypes[file] = type;
    }
    final nonce = '$pid-${DateTime.now().microsecondsSinceEpoch}';
    final temps = <File, File>{};
    final backups = <File, File>{};
    final installed = <File>{};
    var committed = false;
    try {
      for (final entry in updates.entries) {
        if (entry.value != null) {
          final temp = File('${entry.key.path}.$nonce.tmp');
          await temp.writeAsString(entry.value!, flush: true);
          temps[entry.key] = temp;
        }
      }
      for (final file in updates.keys) {
        if (originalTypes[file] == FileSystemEntityType.file) {
          final backup = File('${file.path}.$nonce.bak');
          file.renameSync(backup.path);
          backups[file] = backup;
        }
      }
      for (final entry in temps.entries) {
        entry.value.renameSync(entry.key.path);
        installed.add(entry.key);
      }
      committed = true;
    } on Object {
      for (final file in installed) {
        if (file.existsSync()) file.deleteSync();
      }
      for (final entry in backups.entries) {
        if (entry.value.existsSync()) {
          if (entry.key.existsSync()) entry.key.deleteSync();
          entry.value.renameSync(entry.key.path);
        }
      }
      rethrow;
    } finally {
      for (final temp in temps.values) {
        if (temp.existsSync()) temp.deleteSync();
      }
    }
    for (final backup in backups.values) {
      if (!committed || !backup.existsSync()) continue;
      try {
        backup.deleteSync();
      } on FileSystemException {
        // The transaction committed; a stale nonce backup is recoverable and
        // safer than reporting a failed module change after files were saved.
      }
    }
  }
}
