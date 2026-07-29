part of '../local_developer_repository.dart';

extension LocalDeveloperVpmRestore on LocalDeveloperRepository {
  Future<void> _restoreVpmPackages(
    String root,
    VpmManifest previousManifest,
    List<VpmResolvedPackage> packages,
    VpmManifest updatedManifest,
  ) async {
    final packagesRoot = Directory(p.join(root, 'Packages'))
      ..createSync(recursive: true);
    final transaction = packagesRoot.createTempSync('.topiaforge-vpm-');
    final manifestFile = File(p.join(packagesRoot.path, 'vpm-manifest.json'));
    final reposFile = File(
      p.join(packagesRoot.path, 'vpm-resolver-repos.json'),
    );
    final manifestSnapshot = _DeveloperFileSnapshot.capture(
      manifestFile,
      maxBytes: _maxDeveloperManifestBytes,
    );
    final reposSnapshot = _DeveloperFileSnapshot.capture(
      reposFile,
      maxBytes: _maxDeveloperCatalogBytes,
    );
    final swaps = <_StagedDeveloperDirectorySwap>[];
    try {
      final resolvedIds = <String>{};
      for (var index = 0; index < packages.length; index++) {
        final package = packages[index];
        final id = _requireDeveloperPackageSegment(
          package.id,
          label: 'VPM package id',
        );
        if (!resolvedIds.add(id.toLowerCase())) {
          throw StateError('VPM resolution contains duplicate package $id.');
        }
        final target = Directory(p.join(packagesRoot.path, id));
        final targetType = FileSystemEntity.typeSync(
          target.path,
          followLinks: false,
        );
        if (targetType == FileSystemEntityType.link) {
          throw StateError('VPM package target is a symbolic link: $id.');
        }
        if (targetType != FileSystemEntityType.notFound &&
            targetType != FileSystemEntityType.directory) {
          throw StateError('VPM package target is not a directory: $id.');
        }
        if (_installedVpmVersion(root, id) == package.version) {
          continue;
        }
        if (package.url.trim().isEmpty) {
          throw StateError(
            'VPM package $id ${package.version} has no download URL.',
          );
        }

        final expectedSha = package.zipSha256.trim().toLowerCase();
        final packageUri = Uri.tryParse(package.url.trim());
        if (packageUri?.scheme == 'https' && expectedSha.isEmpty) {
          throw StateError('Remote VPM package $id requires a SHA-256 hash.');
        }
        final bytes = await _fetchVpmBytes(package.url);
        if (expectedSha.isNotEmpty) {
          if (!RegExp(r'^[0-9a-f]{64}$').hasMatch(expectedSha)) {
            throw StateError('VPM package $id has an invalid SHA-256 hash.');
          }
          final actual = sha256.convert(bytes).toString();
          if (actual != expectedSha) {
            throw StateError('SHA-256 mismatch for $id ${package.version}.');
          }
        }

        final archive = _decodeDeveloperArchive(bytes, label: 'VPM package');
        _validateVpmPackageIdentity(archive, package);
        final staging = Directory(p.join(transaction.path, 'staging-$index'));
        _extractDeveloperArchive(archive, staging, label: 'VPM package');
        swaps.add(
          _StagedDeveloperDirectorySwap(
            target: target,
            staging: staging,
            backup: Directory(p.join(transaction.path, 'backup-$index')),
          ),
        );
      }

      var removalIndex = 0;
      for (final lockedId in previousManifest.locked.keys) {
        final id = _requireDeveloperPackageSegment(
          lockedId,
          label: 'Locked VPM package id',
        );
        if (resolvedIds.contains(id.toLowerCase())) {
          continue;
        }
        swaps.add(
          _StagedDeveloperDirectorySwap(
            target: Directory(p.join(packagesRoot.path, id)),
            backup: Directory(
              p.join(transaction.path, 'removed-${removalIndex++}'),
            ),
          ),
        );
      }

      final repos = (await _loadVpmSources())
          .where((source) => source.enabled)
          .map((source) => source.url)
          .toList();
      for (final swap in swaps) {
        swap.commit();
      }
      _writeDeveloperTextAtomic(reposFile, _prettyJson(repos));
      _writeVpmManifest(root, updatedManifest);
    } on Object catch (error, stackTrace) {
      Object? rollbackError;
      for (final swap in swaps.reversed) {
        try {
          swap.rollback();
        } on Object catch (current) {
          rollbackError ??= current;
        }
      }
      for (final snapshot in [reposSnapshot, manifestSnapshot]) {
        try {
          snapshot.restore();
        } on Object catch (current) {
          rollbackError ??= current;
        }
      }
      if (rollbackError != null) {
        Error.throwWithStackTrace(
          StateError(
            'VPM restore failed ($error), and rollback was incomplete '
            '($rollbackError).',
          ),
          stackTrace,
        );
      }
      rethrow;
    } finally {
      if (transaction.existsSync()) {
        try {
          transaction.deleteSync(recursive: true);
        } on FileSystemException {
          // The installed project is valid; a future cleanup may remove this.
        }
      }
    }
  }

  void _validateVpmPackageIdentity(
    SafeZipArchive archive,
    VpmResolvedPackage expected,
  ) {
    final packageJson = archive.entries.where((file) {
      return file.isFile && file.name.replaceAll('\\', '/') == 'package.json';
    }).firstOrNull;
    if (packageJson == null) {
      throw StateError('VPM package is missing package.json.');
    }
    final bytes = _readDeveloperArchiveEntryBounded(
      packageJson,
      maxBytes: _maxDeveloperManifestBytes,
      label: 'VPM package.json',
    );
    final decoded = jsonDecode(utf8.decode(bytes));
    if (decoded is! Map ||
        decoded['name'] != expected.id ||
        decoded['version'] != expected.version) {
      throw StateError(
        'VPM package for ${expected.id} ${expected.version} has mismatched '
        'package.json identity.',
      );
    }
  }
}

void _writeDeveloperTextAtomic(File target, String content) {
  _writeDeveloperBytesAtomic(target, utf8.encode(content));
}

void _recoverDeveloperAtomicBackupIfMissing(File target) {
  final backup = File('${target.path}.bak');
  final backupType = FileSystemEntity.typeSync(backup.path, followLinks: false);
  if (backupType == FileSystemEntityType.notFound) {
    return;
  }
  if (backupType != FileSystemEntityType.file) {
    throw StateError('Invalid atomic-write backup: ${backup.path}');
  }

  final targetType = FileSystemEntity.typeSync(target.path, followLinks: false);
  if (targetType == FileSystemEntityType.notFound) {
    backup.renameSync(target.path);
    return;
  }
  if (targetType != FileSystemEntityType.file) {
    throw StateError('Expected a regular file: ${target.path}');
  }
  backup.deleteSync();
}

void _writeDeveloperBytesAtomic(File target, List<int> bytes) {
  target.parent.createSync(recursive: true);
  _recoverDeveloperAtomicBackupIfMissing(target);
  final targetType = FileSystemEntity.typeSync(target.path, followLinks: false);
  if (targetType == FileSystemEntityType.link) {
    throw StateError('Refusing to replace symbolic link: ${target.path}');
  }
  if (targetType != FileSystemEntityType.notFound &&
      targetType != FileSystemEntityType.file) {
    throw StateError('Expected a regular file: ${target.path}');
  }
  final token = '$pid-${DateTime.now().microsecondsSinceEpoch}';
  final temp = File('${target.path}.$token.tmp');
  final backup = File('${target.path}.bak');
  var backedUp = false;
  try {
    temp.writeAsBytesSync(bytes, flush: true);
    if (targetType == FileSystemEntityType.file) {
      target.renameSync(backup.path);
      backedUp = true;
    }
    temp.renameSync(target.path);
    if (backedUp && backup.existsSync()) {
      try {
        backup.deleteSync();
      } on FileSystemException {
        // The target was committed; a stale backup is safe to clean later.
      }
    }
  } on Object {
    if (!target.existsSync() && backedUp && backup.existsSync()) {
      backup.renameSync(target.path);
    }
    rethrow;
  } finally {
    if (temp.existsSync()) {
      temp.deleteSync();
    }
    if (backup.existsSync() && target.existsSync()) {
      try {
        backup.deleteSync();
      } on FileSystemException {
        // The target remains complete even if backup cleanup is deferred.
      }
    }
  }
}

class _DeveloperFileSnapshot {
  const _DeveloperFileSnapshot._(this.file, this.bytes);

  factory _DeveloperFileSnapshot.capture(
    File file, {
    int maxBytes = _maxDeveloperCatalogBytes,
  }) {
    final type = FileSystemEntity.typeSync(file.path, followLinks: false);
    if (type == FileSystemEntityType.link) {
      throw StateError('Refusing to replace symbolic link: ${file.path}');
    }
    if (type != FileSystemEntityType.notFound &&
        type != FileSystemEntityType.file) {
      throw StateError('Expected a regular file: ${file.path}');
    }
    if (type == FileSystemEntityType.file && file.lengthSync() > maxBytes) {
      throw StateError(
        '${file.path} is larger than the ${_byteLimitLabel(maxBytes)}.',
      );
    }
    return _DeveloperFileSnapshot._(
      file,
      type == FileSystemEntityType.file
          ? _readDeveloperFileBoundedSync(
              file,
              maxBytes: maxBytes,
              label: file.path,
            )
          : null,
    );
  }

  final File file;
  final List<int>? bytes;

  void restore() {
    final original = bytes;
    if (original == null) {
      if (file.existsSync()) {
        file.deleteSync();
      }
      return;
    }
    _writeDeveloperBytesAtomic(file, original);
  }
}

extension _DeveloperFirstOrNull<T> on Iterable<T> {
  T? get firstOrNull {
    final iterator = this.iterator;
    return iterator.moveNext() ? iterator.current : null;
  }
}
