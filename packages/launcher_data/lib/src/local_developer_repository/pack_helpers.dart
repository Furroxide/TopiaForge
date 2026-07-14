part of '../local_developer_repository.dart';

/// Native Dart port of the retired tools/pack-mod.ps1 so packing works the
/// same on every platform (and needs no PowerShell).
extension LocalDeveloperPackOperations on LocalDeveloperRepository {
  static const _contentDirs = ['ref', 'assets', 'AssetBundles', 'Resources'];
  static const _buildOutputContentDirs = ['third_party'];
  static const _excludedTreeDirs = ['bin', 'obj', 'dist', '.robotopia'];
  static const _rootNoticeNames = {
    'license',
    'license.txt',
    'license.md',
    'copying',
    'notice',
    'notice.txt',
    'notice.md',
    'third_party_notices.md',
  };
  static final _reproducibleZipTimestamp = DateTime(1980, 1, 1);

  /// Packs a bare mod directory (a `robotopia.mod.json` with no
  /// `robotopia.project.json`), e.g. the first-party mods under `mods/`.
  Future<String> packModDirectory(
    String projectDir, {
    String outputDir = '',
    String configuration = 'Release',
  }) => _packModProject(
    Directory(projectDir).absolute,
    outputDir: outputDir,
    configuration: configuration,
  );

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
        jsonDecode(
              utf8.decode(
                _readDeveloperFileBoundedSync(
                  manifestFile,
                  maxBytes: _maxDeveloperManifestBytes,
                  label: 'robotopia.mod.json',
                ),
              ),
            )
            as Map<String, Object?>;
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
    final exactAdded = <String>{};
    var expandedBytes = 0;
    void addFile(String archivePath, File source) {
      final name = _portableDeveloperArchivePath(
        p.posix.joinAll(p.split(archivePath)),
        label: 'Robotopia package',
      );
      if (p.posix.basename(name) == '.gitkeep') {
        return;
      }
      if (!added.add(name.toLowerCase())) {
        throw StateError('Robotopia package contains duplicate path: $name');
      }
      exactAdded.add(name);
      if (added.length > _maxDeveloperArchiveEntries) {
        throw StateError('Robotopia package exceeds the 8192-entry limit.');
      }
      final bytes = _readDeveloperFileBoundedSync(
        source,
        maxBytes: _maxDeveloperArchiveEntryBytes,
        label: name,
      );
      final length = bytes.length;
      if (length > _maxDeveloperArchiveEntryBytes) {
        throw StateError('$name exceeds the 1 GB expanded-file limit.');
      }
      if (expandedBytes > _maxDeveloperArchiveExpandedBytes - length) {
        throw StateError(
          'Robotopia package exceeds the 2 GB expanded-size limit.',
        );
      }
      expandedBytes += length;
      archive.addFile(ArchiveFile.bytes(name, bytes));
    }

    final csprojCandidates =
        root
            .listSync()
            .whereType<File>()
            .where((file) => file.path.toLowerCase().endsWith('.csproj'))
            .toList()
          ..sort((a, b) => a.path.compareTo(b.path));
    final csproj = csprojCandidates.firstOrNull;
    if (csproj != null) {
      await _buildAndStage(
        root,
        csproj,
        manifest,
        configuration,
        addFile,
        exactAdded.contains,
      );
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
    final packagePath = p.join(
      output.path,
      '$safeId-$safeVersion.robotopiamod',
    );
    final packageBytes = _encodeReproducibleZip(archive);
    if (packageBytes.length > _maxDeveloperArchiveBytes) {
      throw StateError('Robotopia package exceeds the 512 MB archive limit.');
    }
    _writeDeveloperBytesAtomic(File(packagePath), packageBytes);
    return packagePath;
  }

  List<int> _encodeReproducibleZip(Archive archive) =>
      ZipEncoder().encode(archive, modified: _reproducibleZipTimestamp);

  Future<void> _buildAndStage(
    Directory root,
    File csproj,
    Map<String, Object?> manifest,
    String configuration,
    void Function(String archivePath, File source) addFile,
    bool Function(String archivePath) hasArchivePath,
  ) async {
    final dotnet = await _dotnetSdkResolver(_repositoryRoot);
    final build = await runBoundedProcess(
      dotnet.executable,
      ['build', csproj.path, '-c', configuration],
      // Anchor SDK resolution at the repository global.json even when the mod
      // project itself lives outside the repository tree.
      workingDirectory: _repositoryRoot.path,
      timeout: const Duration(minutes: 10),
      maxStdoutBytes: 16 * 1024 * 1024,
      maxStderrBytes: 16 * 1024 * 1024,
    );
    if (build.exitCode != 0) {
      throw StateError('${build.stdout}\n${build.stderr}'.trim());
    }

    final bin = Directory(p.join(root.path, 'bin', configuration));
    final tfmDirs = bin.existsSync()
        ? (bin.listSync().whereType<Directory>().toList()
            ..sort((a, b) => a.path.compareTo(b.path)))
        : const <Directory>[];
    final tfmDir = tfmDirs.firstOrNull;
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

    addFile(
      'robotopia.mod.json',
      File(p.join(root.path, 'robotopia.mod.json')),
    );
    final rootNotices =
        root
            .listSync(followLinks: false)
            .whereType<File>()
            .where(
              (file) => _rootNoticeNames.contains(
                p.basename(file.path).toLowerCase(),
              ),
            )
            .toList()
          ..sort((left, right) => left.path.compareTo(right.path));
    for (final notice in rootNotices) {
      addFile(p.basename(notice.path), notice);
    }
    final buildFiles = tfmDir.listSync().whereType<File>().toList()
      ..sort((a, b) => a.path.compareTo(b.path));
    for (final file in buildFiles) {
      final name = p.basename(file.path);
      final extension = p.extension(name).toLowerCase();
      if ((extension == '.dll' || extension == '.pdb') &&
          !name.startsWith('Robotopia.Mods.Abstractions.')) {
        addFile(name, file);
      }
    }

    // Referenced SDK assemblies can carry redistributable assets internally.
    // Preserve their build-provided license/notice bundle with the standalone
    // mod package instead of relying on a surrounding launcher archive.
    for (final dirName in _buildOutputContentDirs) {
      final contentDir = Directory(p.join(tfmDir.path, dirName));
      if (!contentDir.existsSync()) {
        continue;
      }
      final contentFiles = _collectRegularPackFiles(contentDir);
      for (final file in contentFiles) {
        addFile(p.relative(file.path, from: tfmDir.path), file);
      }
    }

    for (final dirName in _contentDirs) {
      final contentDir = Directory(p.join(root.path, dirName));
      if (!contentDir.existsSync()) {
        continue;
      }
      final contentFiles = _collectRegularPackFiles(contentDir);
      for (final file in contentFiles) {
        addFile(p.relative(file.path, from: root.path), file);
      }
    }

    final apiAssemblies = (manifest['apiAssemblies'] as List<Object?>?) ?? [];
    for (final entry in apiAssemblies.whereType<String>()) {
      if (entry.trim().isEmpty) {
        continue;
      }
      final archivePath = _portableDeveloperArchivePath(
        p.posix.joinAll(p.split(entry)),
        label: 'Robotopia API assembly',
      );
      // Framework/service mods often expose their entry assembly as their API
      // assembly. It is already staged by the build-output DLL pass above.
      if (hasArchivePath(archivePath)) {
        continue;
      }
      var source = File(p.join(root.path, entry));
      if (!source.existsSync()) {
        source = File(p.join(tfmDir.path, entry));
      }
      if (!source.existsSync()) {
        throw StateError('apiAssemblies entry was not found: $entry');
      }
      addFile(archivePath, source);
    }
  }

  /// Manifest-only mods have no build step: the whole project tree ships,
  /// minus build/output/tool directories.
  void _stageProjectTree(
    Directory root,
    void Function(String archivePath, File source) addFile,
  ) {
    final projectFiles = _collectRegularPackFiles(
      root,
      excludedDirectories: _excludedTreeDirs.toSet(),
    );
    for (final file in projectFiles) {
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

List<File> _collectRegularPackFiles(
  Directory root, {
  Set<String> excludedDirectories = const {},
}) {
  if (FileSystemEntity.typeSync(root.path, followLinks: false) !=
      FileSystemEntityType.directory) {
    throw StateError(
      'Package content root is not a regular directory: ${root.path}',
    );
  }
  final files = <File>[];
  void visit(Directory directory) {
    final entries = directory.listSync(followLinks: false)
      ..sort((left, right) => left.path.compareTo(right.path));
    for (final entity in entries) {
      final type = FileSystemEntity.typeSync(entity.path, followLinks: false);
      if (type == FileSystemEntityType.directory) {
        if (!excludedDirectories.contains(p.basename(entity.path))) {
          visit(Directory(entity.path));
        }
      } else if (type == FileSystemEntityType.file) {
        files.add(File(entity.path));
      } else {
        throw StateError(
          'Package content contains a symlink or special entry: ${entity.path}',
        );
      }
    }
  }

  visit(root);
  files.sort((a, b) => a.path.compareTo(b.path));
  return files;
}
