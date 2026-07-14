part of '../local_developer_repository.dart';

/// Builds deterministic VPM package archives and their integrity-pinned
/// listing. With no explicit roots this preserves the first-party repository
/// build; community authors can pass one or more scaffolded package roots.
extension LocalDeveloperUnityVpmPackaging on LocalDeveloperRepository {
  static final _vpmPackageIdPattern = RegExp(r'^[a-z0-9][a-z0-9._-]{1,127}$');
  static const _excludedVpmSourceDirectories = {
    '.dart_tool',
    '.git',
    'Library',
    'Logs',
    'Temp',
    'bin',
    'build',
    'dist',
    'obj',
  };

  Future<List<String>> packUnityPackages({
    String outputDir = '',
    List<String> packageDirectories = const [],
    String repositoryId = '',
    String repositoryName = '',
    String repositoryAuthor = '',
  }) async {
    final explicitPackages = packageDirectories.isNotEmpty;
    final effectiveRepositoryId = explicitPackages
        ? repositoryId.trim()
        : 'com.robotopia.repos.local';
    final effectiveRepositoryName = explicitPackages
        ? repositoryName.trim()
        : 'QuantumWorks Local';
    final effectiveRepositoryAuthor = explicitPackages
        ? repositoryAuthor.trim()
        : 'QuantumWorks';
    if (!_vpmPackageIdPattern.hasMatch(effectiveRepositoryId) ||
        effectiveRepositoryName.isEmpty ||
        effectiveRepositoryAuthor.isEmpty) {
      throw StateError(
        'Explicit VPM package builds require a valid lowercase repository '
        'id, name, and author.',
      );
    }

    final outputPath = p.normalize(
      p.absolute(
        outputDir.isEmpty
            ? p.join(_repositoryRoot.path, 'dist', 'vpm')
            : outputDir,
      ),
    );
    final outputType = FileSystemEntity.typeSync(
      outputPath,
      followLinks: false,
    );
    if (outputType != FileSystemEntityType.notFound &&
        outputType != FileSystemEntityType.directory) {
      throw StateError('VPM output must be a regular directory: $outputPath');
    }
    final output = Directory(outputPath)..createSync(recursive: true);
    final packageJsons = _selectVpmPackageManifests(
      packageDirectories,
      explicitPackages: explicitPackages,
    );
    final records = <MapEntry<File, Map<String, Object?>>>[];
    final packageIds = <String>{};
    for (final packageJson in packageJsons) {
      final manifest = _readVpmPackageManifest(packageJson);
      final id = manifest['name'];
      if (!explicitPackages &&
          (id is! String || !id.startsWith('com.robotopia.'))) {
        continue;
      }
      _validateVpmPackageManifest(manifest, packageJson.path);
      final packageId = id as String;
      if (!packageIds.add(packageId.toLowerCase())) {
        throw StateError('Duplicate VPM package id: $packageId');
      }
      records.add(MapEntry(packageJson, manifest));
    }
    records.sort(
      (a, b) =>
          (a.value['name'] as String).compareTo(b.value['name'] as String),
    );

    final summary = <String>[];
    final packages = <String, Object?>{};
    for (final record in records) {
      final packageJson = record.key;
      final manifest = record.value;
      final id = manifest['name'] as String;
      final version = manifest['version'] as String;
      final packageDir = packageJson.parent;
      if (p.equals(packageDir.path, output.path)) {
        throw StateError(
          'VPM output cannot be the package root: ${output.path}',
        );
      }

      final safeVersion = version.replaceAll(RegExp('[^A-Za-z0-9_.-]'), '_');
      final zipFileName = _portableDeveloperArchivePath(
        '$id-$safeVersion.zip',
        label: '$id VPM output',
      );
      final archive = Archive();
      final archivePaths = <String>{};
      var expandedBytes = 0;
      final packageFiles = _collectVpmPackageFiles(packageDir, output);
      for (final file in packageFiles) {
        final relative = p.relative(file.path, from: packageDir.path);
        final archivePath = _portableDeveloperArchivePath(
          p.posix.joinAll(p.split(relative)),
          label: '$id VPM package',
        );
        if (!archivePaths.add(archivePath.toLowerCase())) {
          throw StateError('$id contains duplicate path: $archivePath');
        }
        if (archivePaths.length > _maxDeveloperArchiveEntries) {
          throw StateError('$id exceeds the 8192-entry limit.');
        }
        final bytes = _readDeveloperFileBoundedSync(
          file,
          maxBytes: _maxDeveloperArchiveEntryBytes,
          label: '$id/$archivePath',
        );
        final length = bytes.length;
        if (length > _maxDeveloperArchiveEntryBytes) {
          throw StateError(
            '$id/$archivePath exceeds the 1 GB expanded-file limit.',
          );
        }
        if (expandedBytes > _maxDeveloperArchiveExpandedBytes - length) {
          throw StateError('$id exceeds the 2 GB expanded-size limit.');
        }
        expandedBytes += length;
        archive.addFile(ArchiveFile.bytes(archivePath, bytes));
      }
      final zipBytes = _encodeReproducibleZip(archive);
      if (zipBytes.length > _maxDeveloperArchiveBytes) {
        throw StateError('$id exceeds the 512 MB VPM archive limit.');
      }
      final zipFile = File(p.join(output.path, zipFileName));
      _writeDeveloperBytesAtomic(zipFile, zipBytes);
      _removeStaleVpmPackages(output, id, zipFile);
      final sha = sha256.convert(zipBytes).toString();
      packages[id] = {
        'versions': {
          version: {...manifest, 'url': zipFileName, 'zipSHA256': sha},
        },
      };
      summary.add('Packed $id $version -> ${p.join(output.path, zipFileName)}');
    }

    final indexFile = File(p.join(output.path, 'index.json'));
    final indexText = _prettyJson({
      'name': effectiveRepositoryName,
      'id': effectiveRepositoryId,
      'author': effectiveRepositoryAuthor,
      'url': 'index.json',
      'packages': packages,
    });
    if (utf8.encode(indexText).length > _maxDeveloperCatalogBytes) {
      throw StateError('VPM index exceeds the 16 MB catalog limit.');
    }
    _writeDeveloperTextAtomic(indexFile, indexText);
    summary.add('Wrote ${indexFile.path} (${packages.length} package(s)).');
    return summary;
  }

  List<File> _selectVpmPackageManifests(
    List<String> packageDirectories, {
    required bool explicitPackages,
  }) {
    if (!explicitPackages) {
      final templatesDir = Directory(p.join(_repositoryRoot.path, 'templates'));
      if (!templatesDir.existsSync()) {
        return const [];
      }
      return templatesDir
          .listSync(recursive: true, followLinks: false)
          .whereType<File>()
          .where((file) => p.basename(file.path) == 'package.json')
          .where(
            (file) =>
                !file.path.contains('Samples~') &&
                !file.path.contains('Robotopia.UnityPackageTemplate'),
          )
          .toList();
    }

    final result = <File>[];
    for (final rawPath in packageDirectories) {
      final rootPath = p.normalize(p.absolute(rawPath));
      if (FileSystemEntity.typeSync(rootPath, followLinks: false) !=
          FileSystemEntityType.directory) {
        throw StateError(
          'VPM package root must be a regular directory: $rawPath',
        );
      }
      final manifest = File(p.join(rootPath, 'package.json'));
      if (FileSystemEntity.typeSync(manifest.path, followLinks: false) !=
          FileSystemEntityType.file) {
        throw StateError(
          'VPM package root has no regular package.json: $rawPath',
        );
      }
      result.add(manifest);
    }
    return result;
  }

  Map<String, Object?> _readVpmPackageManifest(File packageJson) {
    final decoded = jsonDecode(
      utf8.decode(
        _readDeveloperFileBoundedSync(
          packageJson,
          maxBytes: _maxDeveloperManifestBytes,
          label: 'Unity package.json',
        ),
      ),
    );
    if (decoded is! Map<String, Object?>) {
      throw StateError('${packageJson.path} must contain a JSON object.');
    }
    return decoded;
  }

  void _validateVpmPackageManifest(
    Map<String, Object?> manifest,
    String source,
  ) {
    final id = manifest['name'];
    final version = manifest['version'];
    final displayName = manifest['displayName'];
    if (id is! String || !_vpmPackageIdPattern.hasMatch(id)) {
      throw StateError('$source has an invalid lowercase VPM package name.');
    }
    if (version is! String ||
        version.length > 128 ||
        SemanticVersion.tryParse(version) == null) {
      throw StateError('$source has an invalid semantic version.');
    }
    if (displayName is! String || displayName.trim().isEmpty) {
      throw StateError('$source has no displayName.');
    }
    final dependencies = manifest['vpmDependencies'];
    if (dependencies != null && dependencies is! Map) {
      throw StateError('$source has invalid vpmDependencies.');
    }
    if (dependencies is Map) {
      for (final entry in dependencies.entries) {
        if (entry.key is! String ||
            !_vpmPackageIdPattern.hasMatch(entry.key as String) ||
            entry.value is! String ||
            !_isValidVpmRange(entry.value as String)) {
          throw StateError('$source has an invalid VPM dependency.');
        }
      }
    }
  }

  bool _isValidVpmRange(String value) {
    final text = value.trim();
    if (text.startsWith('^') || text.startsWith('~')) {
      return SemanticVersion.tryParse(text.substring(1)) != null;
    }
    try {
      VersionRange.parse(text);
      return true;
    } on FormatException {
      return false;
    }
  }

  List<File> _collectVpmPackageFiles(Directory packageDir, Directory output) {
    final files = <File>[];
    void visit(Directory directory) {
      final entities = directory.listSync(followLinks: false)
        ..sort((left, right) => left.path.compareTo(right.path));
      for (final entity in entities) {
        final relative = p.relative(entity.path, from: packageDir.path);
        final segments = p.split(relative);
        if (segments.any(_excludedVpmSourceDirectories.contains) ||
            p.equals(entity.path, output.path) ||
            p.isWithin(output.path, entity.path)) {
          continue;
        }
        final type = FileSystemEntity.typeSync(entity.path, followLinks: false);
        if (type == FileSystemEntityType.directory) {
          visit(Directory(entity.path));
        } else if (type == FileSystemEntityType.file) {
          if (p.basename(entity.path) != '.gitkeep') {
            files.add(File(entity.path));
          }
        } else {
          throw StateError(
            'VPM package contains a symlink or special entry: $relative',
          );
        }
      }
    }

    visit(packageDir);
    files.sort((a, b) => a.path.compareTo(b.path));
    return files;
  }

  void _removeStaleVpmPackages(Directory output, String id, File replacement) {
    final staleOutputs = output.listSync().whereType<File>().toList()
      ..sort((a, b) => a.path.compareTo(b.path));
    for (final stale in staleOutputs) {
      final name = p.basename(stale.path);
      if (stale.path != replacement.path &&
          name.startsWith('$id-') &&
          name.endsWith('.zip')) {
        stale.deleteSync();
      }
    }
  }
}
