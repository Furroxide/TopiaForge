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
  'interop-unity': _SdkModule(
    packageId: 'TopiaForge.Mods.Interop.Unity',
    capability: 'unsafe-native',
  ),
};

extension _TopiaForgeModModuleCommands on _TopiaForgeCli {
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
    if (module.runtimeDependency.isNotEmpty) {
      final dependencies = Map<String, Object?>.of(
        map['dependencies'] is Map
            ? (map['dependencies'] as Map).cast<String, Object?>()
            : const <String, Object?>{},
      );
      if (add) {
        dependencies[module.runtimeDependency] =
            '>=${TopiaForgeRuntimeVersions.sdkVersion} <2.0.0';
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
    final updatedProject = _editSdkPackageReference(
      originalProject,
      packageId: module.packageId,
      version: TopiaForgeRuntimeVersions.sdkVersion,
      add: add,
    );
    await _writeModulePairAtomically(
      manifestFile,
      '${const JsonEncoder.withIndent('  ').convert(updatedManifest.toJson())}\n',
      projectFile,
      updatedProject,
    );
    stdout.writeln(
      '${add ? 'Added' : 'Removed'} SDK module $moduleName '
      '(${module.packageId}). Run `topiaforge restore` to refresh the lock file.',
    );
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
      return project.replaceRange(match.start, match.end, item);
    }
    final close = project.lastIndexOf('</Project>');
    if (close < 0) throw StateError('The C# project is malformed.');
    return '${project.substring(0, close)}  <ItemGroup>\n'
        '    <PackageReference Include="$packageId" Version="$version" />\n'
        '  </ItemGroup>\n${project.substring(close)}';
  }

  Future<void> _writeModulePairAtomically(
    File manifest,
    String manifestText,
    File project,
    String projectText,
  ) async {
    for (final file in [manifest, project]) {
      if (FileSystemEntity.typeSync(file.path, followLinks: false) !=
          FileSystemEntityType.file) {
        throw StateError('Refusing to replace a linked or missing file.');
      }
    }
    final nonce = '$pid-${DateTime.now().microsecondsSinceEpoch}';
    final manifestTemp = File('${manifest.path}.$nonce.tmp');
    final projectTemp = File('${project.path}.$nonce.tmp');
    final manifestBackup = File('${manifest.path}.$nonce.bak');
    final projectBackup = File('${project.path}.$nonce.bak');
    try {
      await manifestTemp.writeAsString(manifestText, flush: true);
      await projectTemp.writeAsString(projectText, flush: true);
      manifest.renameSync(manifestBackup.path);
      try {
        project.renameSync(projectBackup.path);
        manifestTemp.renameSync(manifest.path);
        projectTemp.renameSync(project.path);
      } on Object {
        if (manifestBackup.existsSync()) {
          if (manifest.existsSync()) manifest.deleteSync();
          manifestBackup.renameSync(manifest.path);
        }
        if (projectBackup.existsSync()) {
          if (project.existsSync()) project.deleteSync();
          projectBackup.renameSync(project.path);
        }
        rethrow;
      }
      if (manifestBackup.existsSync()) manifestBackup.deleteSync();
      if (projectBackup.existsSync()) projectBackup.deleteSync();
    } finally {
      if (manifestTemp.existsSync()) manifestTemp.deleteSync();
      if (projectTemp.existsSync()) projectTemp.deleteSync();
      if (manifestBackup.existsSync() && manifest.existsSync()) {
        manifestBackup.deleteSync();
      }
      if (projectBackup.existsSync() && project.existsSync()) {
        projectBackup.deleteSync();
      }
    }
  }
}
