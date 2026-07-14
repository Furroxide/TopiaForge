part of '../local_developer_repository.dart';

extension _LocalDeveloperLegacyMigration on LocalDeveloperRepository {
  Future<String> _migrateLegacyDll(File dll, String outputRoot) async {
    final bytes = await _readDeveloperFileBounded(
      dll,
      maxBytes: _maxLegacyMigrationFileBytes,
      label: 'Legacy mod DLL',
    );
    final id = 'legacy.${p.basenameWithoutExtension(dll.path).toLowerCase()}';
    final workspace = await createModProject(
      parentDirectory: outputRoot,
      id: id,
      name: p.basenameWithoutExtension(dll.path),
    );
    final root = Directory(workspace.projectRoot);
    await File(
      p.join(root.path, p.basename(dll.path)),
    ).writeAsBytes(bytes, flush: true);
    return root.path;
  }

  Future<LegacyMigrationResult> _migrateLegacyMods(
    String gamePath,
    String outputRoot,
  ) async {
    final legacyRoot = Directory(p.join(gamePath, 'Mods'));
    final created = <String>[];
    final issues = <LauncherIssue>[];
    final output = Directory(outputRoot);
    final outputType = FileSystemEntity.typeSync(
      output.path,
      followLinks: false,
    );
    if (outputType == FileSystemEntityType.link) {
      throw StateError('Legacy migration output cannot be a symbolic link.');
    }
    if (outputType != FileSystemEntityType.notFound &&
        outputType != FileSystemEntityType.directory) {
      throw StateError('Legacy migration output must be a directory.');
    }
    output.createSync(recursive: true);
    final legacyType = FileSystemEntity.typeSync(
      legacyRoot.path,
      followLinks: false,
    );
    if (legacyType == FileSystemEntityType.notFound) {
      return LegacyMigrationResult(
        outputRoot: outputRoot,
        createdProjects: created,
        issues: const [
          LauncherIssue(
            severity: IssueSeverity.warning,
            message: 'Robotopia/Mods folder was not found.',
          ),
        ],
      );
    }
    if (legacyType != FileSystemEntityType.directory) {
      throw StateError('Robotopia/Mods must be a regular directory.');
    }

    final entries = legacyRoot.listSync(followLinks: false)
      ..sort((left, right) => left.path.compareTo(right.path));
    if (entries.length > _maxLegacyMigrationEntries) {
      throw StateError('Legacy Mods folder exceeds the migration entry limit.');
    }
    for (final entity in entries) {
      final type = FileSystemEntity.typeSync(entity.path, followLinks: false);
      if (type == FileSystemEntityType.file &&
          entity.path.toLowerCase().endsWith('.dll')) {
        created.add(await _migrateLegacyDll(File(entity.path), outputRoot));
      } else if (type == FileSystemEntityType.directory) {
        final manifest = File(p.join(entity.path, 'robotopia.mod.json'));
        final manifestType = FileSystemEntity.typeSync(
          manifest.path,
          followLinks: false,
        );
        if (manifestType == FileSystemEntityType.file) {
          created.add(
            await _migrateLegacyFolder(Directory(entity.path), outputRoot),
          );
        } else if (manifestType == FileSystemEntityType.notFound) {
          issues.add(
            LauncherIssue(
              severity: IssueSeverity.warning,
              message:
                  '${p.basename(entity.path)} has no robotopia.mod.json and needs manual migration.',
            ),
          );
        } else {
          throw StateError('Legacy robotopia.mod.json must be a regular file.');
        }
      } else if (type == FileSystemEntityType.link) {
        throw StateError('Legacy Mods folder contains a symbolic link.');
      } else if (type != FileSystemEntityType.file) {
        throw StateError('Legacy Mods folder contains a special file.');
      }
    }
    return LegacyMigrationResult(
      outputRoot: outputRoot,
      createdProjects: created,
      issues: issues,
    );
  }

  Future<String> _migrateLegacyFolder(
    Directory source,
    String outputRoot,
  ) async {
    final sourcePath = source.absolute.path;
    final output = Directory(outputRoot).absolute;
    if (p.isWithin(sourcePath, output.path)) {
      throw StateError('Legacy migration output cannot be inside its source.');
    }
    final token = '$pid-${DateTime.now().microsecondsSinceEpoch}';
    final transaction = Directory(
      p.join(output.path, '.robotopia-legacy-migration-$token'),
    )..createSync();
    final staging = Directory(p.join(transaction.path, 'staging'));
    final backup = Directory(p.join(transaction.path, 'backup'));
    _StagedDeveloperDirectorySwap? swap;
    var committed = false;
    try {
      await _copyLegacyDirectoryBounded(source, staging);
      final manifest = ModManifest.fromJson(
        jsonDecode(
              utf8.decode(
                await _readDeveloperFileBounded(
                  File(p.join(staging.path, 'robotopia.mod.json')),
                  maxBytes: _maxDeveloperManifestBytes,
                  label: 'Legacy robotopia.mod.json',
                ),
              ),
            )
            as Map<String, Object?>,
      );
      await _writeProject(
        staging.path,
        DeveloperProject(
          schemaVersion: 1,
          id: manifest.id,
          name: manifest.name,
        ),
      );
      await _ensureProjectGitignore(staging.path);
      final target = Directory(p.join(output.path, _safeName(manifest.id)));
      swap = _StagedDeveloperDirectorySwap(
        target: target,
        backup: backup,
        staging: staging,
      );
      swap.commit();
      committed = true;
      if (backup.existsSync()) {
        backup.deleteSync(recursive: true);
      }
      return target.path;
    } finally {
      if (!committed) {
        swap?.rollback();
      }
      if (transaction.existsSync()) {
        transaction.deleteSync(recursive: true);
      }
    }
  }

  Future<void> _copyLegacyDirectoryBounded(
    Directory source,
    Directory destination,
  ) async {
    if (FileSystemEntity.typeSync(source.path, followLinks: false) !=
        FileSystemEntityType.directory) {
      throw StateError('Legacy mod source must be a regular directory.');
    }
    final entities = source.listSync(recursive: true, followLinks: false)
      ..sort((left, right) => left.path.compareTo(right.path));
    if (entities.length > _maxLegacyMigrationEntries) {
      throw StateError('Legacy mod exceeds the migration entry limit.');
    }
    destination.createSync();
    var totalBytes = 0;
    final paths = <String>{};
    for (final entity in entities) {
      final type = FileSystemEntity.typeSync(entity.path, followLinks: false);
      if (type != FileSystemEntityType.file &&
          type != FileSystemEntityType.directory) {
        throw StateError(
          'Legacy mod contains a symbolic link or special file.',
        );
      }
      final relative = _portableDeveloperArchivePath(
        p.relative(entity.path, from: source.path),
        label: 'Legacy mod',
      );
      if (!paths.add(
        portableArchiveCollisionKey(relative, label: 'Legacy mod'),
      )) {
        throw StateError('Legacy mod contains a colliding path: $relative');
      }
      final target = p.joinAll([destination.path, ...p.posix.split(relative)]);
      if (type == FileSystemEntityType.directory) {
        Directory(target).createSync(recursive: true);
        continue;
      }
      final file = File(entity.path);
      final before = file.statSync();
      if (before.size < 0 ||
          before.size > _maxLegacyMigrationFileBytes ||
          totalBytes > _maxLegacyMigrationBytes - before.size) {
        throw StateError('Legacy mod exceeds the migration size limit.');
      }
      final bytes = await _readDeveloperFileBounded(
        file,
        maxBytes: _maxLegacyMigrationFileBytes,
        label: 'Legacy mod file',
      );
      final afterType = FileSystemEntity.typeSync(
        file.path,
        followLinks: false,
      );
      final after = file.statSync();
      if (afterType != FileSystemEntityType.file ||
          before.size != after.size ||
          before.modified != after.modified) {
        throw StateError('Legacy mod changed while it was being migrated.');
      }
      totalBytes += bytes.length;
      final output = File(target);
      output.parent.createSync(recursive: true);
      await output.writeAsBytes(bytes, flush: true);
    }
  }
}
