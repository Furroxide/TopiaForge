part of '../local_developer_repository.dart';

/// Native Dart port of the retired tools/pack-mod.ps1 so packing works the
/// same on every platform (and needs no PowerShell).
extension LocalDeveloperPackOperations on LocalDeveloperRepository {
  static const _contentDirs = ['ref', 'assets', 'AssetBundles', 'Resources'];
  static const _buildOutputContentDirs = ['third_party'];
  static const _excludedTreeDirs = ['bin', 'obj', 'dist', '.topiaforge'];
  static const _loaderOwnedSdkAssemblyNames = {
    'topiaforge.mods.abstractions',
    'topiaforge.mods.analyzers',
    'topiaforge.mods.chronos',
    'topiaforge.mods.interop.unity',
    'topiaforge.mods.prompts',
    'topiaforge.mods.robotkit',
    'topiaforge.mods.testing',
    'topiaforge.mods.ugc',
    'topiaforge.mods.unityui',
    'topiaforge.mods.worlds',
  };
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

  /// Packs a bare mod directory (a `topiaforge.mod.json` with no
  /// `topiaforge.project.json`), e.g. the first-party mods under `mods/`.
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
    final manifestFile = File(p.join(root.path, 'topiaforge.mod.json'));
    if (!manifestFile.existsSync()) {
      throw StateError('topiaforge.mod.json was not found in ${root.path}');
    }
    final manifest =
        jsonDecode(
              utf8.decode(
                _readDeveloperFileBoundedSync(
                  manifestFile,
                  maxBytes: _maxDeveloperManifestBytes,
                  label: 'topiaforge.mod.json',
                ),
              ),
            )
            as Map<String, Object?>;
    final manifestContract = ModManifest.fromJson(manifest);
    final blockingManifestIssues = manifestContract
        .validate()
        .where((issue) => issue.isBlocking)
        .toList();
    if (blockingManifestIssues.isNotEmpty) {
      throw StateError(
        'topiaforge.mod.json is invalid: '
        '${blockingManifestIssues.map((issue) => issue.message).join(' ')}',
      );
    }

    final archive = Archive();
    final added = <String>{};
    final exactAdded = <String>{};
    var expandedBytes = 0;
    void addBytes(String archivePath, List<int> bytes) {
      final name = _portableDeveloperArchivePath(
        p.posix.joinAll(p.split(archivePath)),
        label: 'TopiaForge package',
      );
      if (p.posix.basename(name) == '.gitkeep') {
        return;
      }
      if (!added.add(name.toLowerCase())) {
        throw StateError('TopiaForge package contains duplicate path: $name');
      }
      exactAdded.add(name);
      if (added.length > _maxDeveloperArchiveEntries) {
        throw StateError('TopiaForge package exceeds the 8192-entry limit.');
      }
      final length = bytes.length;
      if (length > _maxDeveloperArchiveEntryBytes) {
        throw StateError('$name exceeds the 1 GB expanded-file limit.');
      }
      if (expandedBytes > _maxDeveloperArchiveExpandedBytes - length) {
        throw StateError(
          'TopiaForge package exceeds the 2 GB expanded-size limit.',
        );
      }
      expandedBytes += length;
      archive.addFile(ArchiveFile.bytes(name, bytes));
    }

    void addFile(String archivePath, File source) {
      final name = _portableDeveloperArchivePath(
        p.posix.joinAll(p.split(archivePath)),
        label: 'TopiaForge package',
      );
      if (p.posix.basename(name) == '.gitkeep') return;
      final bytes = _readDeveloperFileBoundedSync(
        source,
        maxBytes: _maxDeveloperArchiveEntryBytes,
        label: name,
      );
      addBytes(name, bytes);
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

    final packedManifest = _manifestWithBuildMetadata(root, manifest);
    addBytes(
      'topiaforge.mod.json',
      utf8.encode('${_prettyJson(packedManifest.toJson())}\n'),
    );

    // Ship the mod's game-binding manifest (from the centralized repo-root
    // bindings/ dir) inside its package, so a game-compatibility check can
    // travel with the mod.
    final modName = manifestContract.id;
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
    final safeVersion = _sanitizePackageToken(manifestContract.version);
    final packagePath = p.join(
      output.path,
      '$safeId-$safeVersion.topiaforgemod',
    );
    final packageBytes = _encodeReproducibleZip(archive);
    if (packageBytes.length > _maxDeveloperArchiveBytes) {
      throw StateError('TopiaForge package exceeds the 512 MB archive limit.');
    }
    _writeDeveloperBytesAtomic(File(packagePath), packageBytes);
    return packagePath;
  }

  List<int> _encodeReproducibleZip(Archive archive) {
    final ordered = Archive();
    final files = archive.files.toList()
      ..sort((left, right) => left.name.compareTo(right.name));
    for (final file in files) {
      ordered.addFile(file);
    }
    return ZipEncoder().encode(ordered, modified: _reproducibleZipTimestamp);
  }

  Future<void> _buildAndStage(
    Directory root,
    File csproj,
    Map<String, Object?> manifest,
    String configuration,
    void Function(String archivePath, File source) addFile,
    bool Function(String archivePath) hasArchivePath,
  ) async {
    final buildRoot = File(p.join(root.path, 'global.json')).existsSync()
        ? root
        : _repositoryRoot;
    final dotnet = await _dotnetSdkResolver(buildRoot);
    final build = await runBoundedProcess(
      dotnet.executable,
      ['build', csproj.path, '-c', configuration],
      // The scaffold pins its own SDK, so builds remain reproducible after the
      // release archive or source checkout is moved or removed.
      workingDirectory: buildRoot.path,
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
      final assemblyName = p.basenameWithoutExtension(name).toLowerCase();
      if ((extension == '.dll' || extension == '.pdb') &&
          !_loaderOwnedSdkAssemblyNames.contains(assemblyName)) {
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
        label: 'TopiaForge API assembly',
      );
      // Project-referenced contract assemblies are normally copied beside the
      // entry assembly and staged by the build-output DLL pass above.
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
      if (segments.any(_excludedTreeDirs.contains) ||
          p.posix.joinAll(segments) == 'topiaforge.mod.json') {
        continue;
      }
      addFile(relative, file);
    }
  }

  ModManifest _manifestWithBuildMetadata(
    Directory root,
    Map<String, Object?> source,
  ) {
    final lock = _readSdkLock(root.path);
    final toolVersion = lock?.toolVersion.isNotEmpty == true
        ? lock!.toolVersion
        : _repositoryToolVersion();
    final gameVersion =
        lock?.gameVersion ?? TopiaForgeRuntimeVersions.gameVersion;
    return ModManifest.fromJson({
      ...source,
      'builtWith': {
        'sdkVersion': lock?.sdkVersion ?? TopiaForgeRuntimeVersions.sdkVersion,
        'loaderVersion': TopiaForgeRuntimeVersions.loaderVersion,
        'gameVersion': gameVersion,
        if (toolVersion.isNotEmpty) 'toolVersion': toolVersion,
      },
    });
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
