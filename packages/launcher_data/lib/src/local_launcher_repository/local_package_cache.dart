part of '../local_launcher_repository.dart';

/// Only archives created by local installs are eligible for retirement.
/// Unknown (including older HTTPS/join) entries are never adopted. Ownership
/// holds hashes of package roots, never their paths, so two game installs
/// sharing a data root keep each other's originals. A remote use permanently
/// relinquishes local ownership; its retention policy is separate.
extension _LocalPackageCache on LocalLauncherRepository {
  Directory get _localCacheOwners =>
      Directory(p.join(_packageCache.path, 'local-installs'));

  File _localCacheOwnerFile(String digest) =>
      File(p.join(_localCacheOwners.path, '$digest.json'));

  String _cacheOwner(GameInstall install) {
    final path = p.canonicalize(_packagesRoot(install).absolute.path);
    return sha256.convert(utf8.encode(path)).toString();
  }

  Future<void> _cacheLocalPackage(
    _PackageReadResult package,
    GameInstall install,
  ) async {
    if (Uri.tryParse(package.reference)?.scheme == 'https') return;
    try {
      _requireCacheDirectory(_packageCache);
      _requireCacheDirectory(_localCacheOwners);
      final digest = package.sha256Hex;
      final cached = File(p.join(_packageCache.path, '$digest.topiaforgemod'));
      final owners = await _readLocalCacheOwners(digest);
      final isNew =
          FileSystemEntity.typeSync(cached.path, followLinks: false) ==
          FileSystemEntityType.notFound;
      // Reserve before writing or committing receipts, under the cache lease.
      // On rollback a sweep reconciles the reservation with durable receipts.
      if (owners != null || isNew) {
        await _saveLocalCacheOwners(digest, {...?owners, _cacheOwner(install)});
      }
      await _writePackageCacheAtomic(cached, package.bytes, digest);
    } on Object {
      throw StateError(
        'The mod archive could not be saved in the package cache, so the '
        'install was cancelled. Check free disk space and cache folder '
        'permissions, remove any linked or non-file cache entry, then '
        'reinstall the mod.',
      );
    }
  }

  Future<Set<String>?> _readLocalCacheOwners(String digest) async {
    final file = _localCacheOwnerFile(digest);
    final type = FileSystemEntity.typeSync(file.path, followLinks: false);
    if (type == FileSystemEntityType.notFound) return null;
    final decoded = await _readJsonFileBounded(
      file,
      maxBytes: 16 * 1024,
      label: 'Local archive ownership',
    );
    if (decoded is! Map ||
        decoded['schemaVersion'] != 1 ||
        decoded['owners'] is! List) {
      throw const FormatException('Local archive ownership is unreadable.');
    }
    final owners = decoded['owners'] as List;
    if (owners.any((value) => value is! String || !_isSha256(value))) {
      throw const FormatException('Local archive ownership is unreadable.');
    }
    return owners.cast<String>().toSet();
  }

  Future<void> _saveLocalCacheOwners(String digest, Set<String> owners) =>
      _writeJsonFileAtomic(
        _localCacheOwnerFile(digest),
        {'schemaVersion': 1, 'owners': owners.toList()..sort()},
        maxBytes: 16 * 1024,
        label: 'Local archive ownership',
      );

  /// Called while holding the cache lease, before any HTTPS or join use.
  /// Failure stops that use: leaving an entry eligible for pruning is unsafe.
  Future<void> _retainPackageCache(String digest) async {
    try {
      await _relinquishLocalCache(digest);
    } on Object {
      throw StateError(
        'The package cache could not retain this archive. Check cache folder '
        'permissions and close programs using it, then retry.',
      );
    }
  }

  Future<void> _relinquishLocalCache(String digest) async {
    if (FileSystemEntity.typeSync(_localCacheOwners.path, followLinks: false) ==
        FileSystemEntityType.notFound) {
      return;
    }
    _requireCacheDirectory(_packageCache);
    _requireCacheDirectory(_localCacheOwners);
    final file = _localCacheOwnerFile(digest);
    final type = FileSystemEntity.typeSync(file.path, followLinks: false);
    if (type == FileSystemEntityType.notFound) return;
    if (type != FileSystemEntityType.file) {
      throw StateError('Local archive ownership is not a regular file.');
    }
    await file.delete();
  }

  Future<void> _pruneLocalPackageCache(GameInstall install) async {
    try {
      if (!await _localCacheOwners.exists()) return;
      _requireCacheDirectory(_packageCache);
      _requireCacheDirectory(_localCacheOwners);
      final referenced = await _receiptArchiveHashes(install);
      final owner = _cacheOwner(install);
      for (final entry in _localCacheOwners.listSync(followLinks: false)) {
        final digest = p.basenameWithoutExtension(entry.path);
        if (!_isSha256(digest) || p.extension(entry.path) != '.json') continue;
        final owners = await _readLocalCacheOwners(digest);
        if (owners == null ||
            !owners.contains(owner) ||
            referenced.contains(digest)) {
          continue;
        }
        owners.remove(owner);
        if (owners.isNotEmpty) {
          await _saveLocalCacheOwners(digest, owners);
          continue;
        }
        final cached = File(
          p.join(_packageCache.path, '$digest.topiaforgemod'),
        );
        final type = FileSystemEntity.typeSync(cached.path, followLinks: false);
        if (type == FileSystemEntityType.file) {
          await cached.delete();
        } else if (type != FileSystemEntityType.notFound) {
          throw StateError('Retired archive is not a regular file.');
        }
        await _localCacheOwnerFile(digest).delete();
      }
    } on Object {
      // A successful commit stays successful. Ownership remains for a later
      // install/uninstall to retry; uncertain receipts always retain archives.
      await _appendLauncherLogBestEffort(
        'Unused local mod archives could not be removed. The installed mods '
        'are unchanged. Close programs using the package cache, check folder '
        'permissions and repair any mod the launcher reports as damaged; '
        'cleanup will retry on the next install or uninstall.',
      );
    }
  }

  /// Read every installed version, including disabled mods and receipts whose
  /// source is remote. A plain file among the mod folders (a desktop.ini, a
  /// note) holds no receipt, and a version folder without one, as a copied
  /// folder has, names no archive; the game loads neither, so both are passed
  /// over. Linked folders and receipts that are there but cannot be read stop
  /// retirement.
  Future<Set<String>> _receiptArchiveHashes(GameInstall install) async {
    final packages = _packagesRoot(install);
    if (!packages.existsSync()) return {};
    _requireCacheDirectory(packages);
    final hashes = <String>{};
    for (final mod in packages.listSync(followLinks: false)) {
      if (mod is File) continue;
      if (mod is! Directory) {
        throw StateError('Installed mod directory is unreadable.');
      }
      for (final version in mod.listSync(followLinks: false)) {
        if (version is File) continue;
        if (version is! Directory) {
          throw StateError('Installed mod version is unreadable.');
        }
        final file = File(p.join(version.path, _packageInstallReceiptFileName));
        if (FileSystemEntity.typeSync(file.path, followLinks: false) ==
            FileSystemEntityType.notFound) {
          continue;
        }
        final receipt = await _readJsonFileBounded(
          file,
          maxBytes: _maxInstalledReceiptBytes,
          label: 'Installed mod receipt',
        );
        if (receipt is! Map ||
            receipt['sourceSha256'] is! String ||
            !_isSha256(receipt['sourceSha256'] as String)) {
          throw StateError('Installed mod receipt is unreadable.');
        }
        hashes.add(receipt['sourceSha256'] as String);
      }
    }
    return hashes;
  }
}
