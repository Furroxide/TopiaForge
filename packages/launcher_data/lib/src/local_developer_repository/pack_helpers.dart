part of '../local_developer_repository.dart';

/// Native Dart port of the retired tools/pack-mod.ps1 so packing works the
/// same on every platform (and needs no PowerShell).
extension _PackHelpers on LocalDeveloperRepository {
  static const _contentDirs = ['ref', 'assets', 'AssetBundles', 'Resources'];
  static const _excludedTreeDirs = ['bin', 'obj', 'dist', '.robotopia'];

  Future<String> _packModProject(
    Directory root, {
    String outputDir = '',
    String configuration = 'Release',
  }) async {
    final manifestFile = File(p.join(root.path, 'robotopia.mod.json'));
    if (!manifestFile.existsSync()) {
      throw StateError('robotopia.mod.json was not found in ${root.path}');
    }
    final manifest =
        jsonDecode(manifestFile.readAsStringSync()) as Map<String, Object?>;
    for (final field in [
      'name',
      'displayName',
      'version',
      'entryAssembly',
      'entryType',
    ]) {
      final value = manifest[field];
      if (value is! String || value.trim().isEmpty) {
        throw StateError(
          'Manifest must include name, displayName, version, entryAssembly, '
          'and entryType.',
        );
      }
    }

    final archive = Archive();
    final added = <String>{};
    void addFile(String archivePath, File source) {
      final name = p.posix.joinAll(p.split(archivePath));
      if (!added.add(name)) {
        return;
      }
      archive.addFile(ArchiveFile.bytes(name, source.readAsBytesSync()));
    }

    final csproj = root
        .listSync()
        .whereType<File>()
        .where((file) => file.path.toLowerCase().endsWith('.csproj'))
        .firstOrNull;
    if (csproj != null) {
      await _buildAndStage(root, csproj, manifest, configuration, addFile);
    } else {
      _stageProjectTree(root, addFile);
    }

    // Ship the mod's game-binding manifest (from the centralized repo-root
    // bindings/ dir) inside its package, so a game-compatibility check can
    // travel with the mod.
    final modName = manifest['name'] as String;
    final bindingFile = File(
      p.join(_repositoryRoot.path, 'bindings', '$modName.gamebindings.json'),
    );
    if (bindingFile.existsSync()) {
      addFile(p.join('bindings', '$modName.gamebindings.json'), bindingFile);
    }

    final output = Directory(
      outputDir.isEmpty ? p.join(root.path, 'dist') : outputDir,
    )..createSync(recursive: true);
    final safeId = _sanitizePackageToken(modName);
    final safeVersion = _sanitizePackageToken(manifest['version'] as String);
    final packagePath = p.join(output.path, '$safeId-$safeVersion.robotopiamod');
    File(packagePath).writeAsBytesSync(ZipEncoder().encode(archive));
    return packagePath;
  }

  Future<void> _buildAndStage(
    Directory root,
    File csproj,
    Map<String, Object?> manifest,
    String configuration,
    void Function(String archivePath, File source) addFile,
  ) async {
    final build = await Process.run('dotnet', [
      'build',
      csproj.path,
      '-c',
      configuration,
    ], workingDirectory: root.path);
    if (build.exitCode != 0) {
      throw StateError('${build.stdout}\n${build.stderr}'.trim());
    }

    final bin = Directory(p.join(root.path, 'bin', configuration));
    final tfmDir = bin.existsSync()
        ? bin.listSync().whereType<Directory>().firstOrNull
        : null;
    if (tfmDir == null) {
      throw StateError('Could not find build output under ${bin.path}');
    }

    final entryAssembly = manifest['entryAssembly'] as String;
    if (!File(p.join(tfmDir.path, entryAssembly)).existsSync()) {
      throw StateError(
        'entryAssembly was not found in build output: '
        '${p.join(tfmDir.path, entryAssembly)}',
      );
    }

    addFile('robotopia.mod.json', File(p.join(root.path, 'robotopia.mod.json')));
    for (final file in tfmDir.listSync().whereType<File>()) {
      final name = p.basename(file.path);
      final extension = p.extension(name).toLowerCase();
      if ((extension == '.dll' || extension == '.pdb') &&
          !name.startsWith('Robotopia.Mods.Abstractions.')) {
        addFile(name, file);
      }
    }

    for (final dirName in _contentDirs) {
      final contentDir = Directory(p.join(root.path, dirName));
      if (!contentDir.existsSync()) {
        continue;
      }
      for (final file in contentDir.listSync(recursive: true).whereType<File>()) {
        addFile(p.relative(file.path, from: root.path), file);
      }
    }

    final apiAssemblies = (manifest['apiAssemblies'] as List<Object?>?) ?? [];
    for (final entry in apiAssemblies.whereType<String>()) {
      if (entry.trim().isEmpty) {
        continue;
      }
      var source = File(p.join(root.path, entry));
      if (!source.existsSync()) {
        source = File(p.join(tfmDir.path, entry));
      }
      if (!source.existsSync()) {
        throw StateError('apiAssemblies entry was not found: $entry');
      }
      addFile(entry, source);
    }
  }

  /// Manifest-only mods have no build step: the whole project tree ships,
  /// minus build/output/tool directories.
  void _stageProjectTree(
    Directory root,
    void Function(String archivePath, File source) addFile,
  ) {
    for (final file in root.listSync(recursive: true).whereType<File>()) {
      final relative = p.relative(file.path, from: root.path);
      final segments = p.split(relative);
      if (segments.any(_excludedTreeDirs.contains)) {
        continue;
      }
      addFile(relative, file);
    }
  }

  String _sanitizePackageToken(String value) =>
      value.replaceAll(RegExp('[^A-Za-z0-9_.-]'), '_');
}
