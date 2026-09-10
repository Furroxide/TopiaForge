part of '../local_launcher_repository.dart';

extension _PackageHelpers on LocalLauncherRepository {
  Future<_PackageReadResult> _readPackage(
    String packageReference, {
    String expectedSha256 = '',
  }) async {
    requireCanonicalTopiaForgePackageReference(packageReference);
    final reference = await _resolvePackageReference(
      packageReference,
      expectedSha256: expectedSha256,
    );
    final bytes = reference.bytes;
    if (bytes.length > _maxPackageBytes) {
      throw StateError('Package is larger than the 512 MB launcher limit.');
    }
    final actualSha = sha256.convert(bytes).toString();
    if (expectedSha256.trim().isNotEmpty &&
        actualSha.toLowerCase() != expectedSha256.trim().toLowerCase()) {
      throw StateError(
        'Package SHA-256 mismatch for $packageReference. Expected $expectedSha256 but got $actualSha.',
      );
    }

    final archive = SafeZipArchive.decode(bytes, label: 'Package');

    final manifestFile = archive.entryNamed('topiaforge.mod.json');
    if (manifestFile == null || !manifestFile.isFile) {
      throw StateError('Package is missing topiaforge.mod.json.');
    }
    if (manifestFile.size > _maxManifestBytes) {
      throw StateError('topiaforge.mod.json exceeds the 1 MB manifest limit.');
    }

    final manifest = ModManifest.fromJson(
      jsonDecode(
            utf8.decode(
              manifestFile.readBytes(
                maxBytes: _maxManifestBytes,
                label: 'topiaforge.mod.json',
              ),
            ),
          )
          as Map<String, Object?>,
    );
    final entryAssembly = portableArchivePath(
      manifest.entryAssembly,
      label: 'Package entryAssembly',
    );
    final hasEntryAssembly = archive.entries.any(
      (file) => file.isFile && file.name == entryAssembly,
    );
    if (!hasEntryAssembly) {
      throw StateError(
        'entryAssembly was not found in package: ${manifest.entryAssembly}',
      );
    }

    return _PackageReadResult(
      archive: archive,
      manifest: manifest,
      sha256Hex: actualSha,
      reference: reference.reference,
      trust: expectedSha256.trim().isEmpty
          ? _localUnverifiedPackageTrust
          : _sha256VerifiedPackageTrust,
    );
  }

  Future<_PackageReferenceBytes> _resolvePackageReference(
    String packageReference, {
    required String expectedSha256,
  }) async {
    final normalizedSha = expectedSha256.trim().toLowerCase();
    if (normalizedSha.isNotEmpty &&
        !RegExp(r'^[0-9a-f]{64}$').hasMatch(normalizedSha)) {
      throw StateError(
        'Expected package SHA-256 must be exactly 64 hex digits.',
      );
    }
    if (RegExp(r'^[A-Za-z]:[\\/]').hasMatch(packageReference) ||
        packageReference.startsWith(r'\\')) {
      return _readPackageFile(packageReference, packageReference);
    }

    final uri = Uri.tryParse(packageReference);
    if (uri != null && uri.scheme == 'file') {
      final path = uri.toFilePath(windows: Platform.isWindows);
      return _readPackageFile(path, packageReference);
    }

    if (uri != null && uri.scheme == 'https') {
      if (normalizedSha.isEmpty) {
        throw StateError(
          'Remote packages require a SHA-256 hash before install or preview.',
        );
      }
      final cached = File(
        p.join(_packageCache.path, '$normalizedSha.topiaforgemod'),
      );
      if (cached.existsSync()) {
        try {
          final cachedBytes = await _readLauncherFileBounded(
            cached,
            _maxPackageBytes,
          );
          if (sha256.convert(cachedBytes).toString() == normalizedSha) {
            return _PackageReferenceBytes(
              reference: packageReference,
              bytes: cachedBytes,
            );
          }
        } on StateError {
          // Invalid cache entries are removed below and fetched afresh.
        }
        await cached.delete();
      }

      final bytes = await _downloadBytes(uri);
      final downloadedSha = sha256.convert(bytes).toString();
      if (downloadedSha != normalizedSha) {
        throw StateError(
          'Package SHA-256 mismatch for ${_safePackageReference(packageReference)}. '
          'Expected $normalizedSha but got $downloadedSha.',
        );
      }
      await _writePackageCacheAtomic(cached, bytes, normalizedSha);
      return _PackageReferenceBytes(reference: packageReference, bytes: bytes);
    }

    if (uri != null && uri.hasScheme) {
      throw StateError('Unsupported package URL scheme: ${uri.scheme}');
    }

    return _readPackageFile(packageReference, packageReference);
  }

  Future<void> _writePackageCacheAtomic(
    File cached,
    List<int> bytes,
    String expectedSha,
  ) async {
    final targetType = FileSystemEntity.typeSync(
      cached.path,
      followLinks: false,
    );
    if (targetType == FileSystemEntityType.link) {
      throw StateError('Package cache entry cannot be a symbolic link.');
    }
    if (targetType != FileSystemEntityType.notFound &&
        targetType != FileSystemEntityType.file) {
      throw StateError('Package cache entry is not a regular file.');
    }
    final token = '$pid-${DateTime.now().microsecondsSinceEpoch}';
    final temp = File('${cached.path}.$token.tmp');
    await temp.create(recursive: true);
    try {
      await temp.writeAsBytes(bytes, flush: true);
      try {
        await temp.rename(cached.path);
      } on FileSystemException {
        if (!await cached.exists()) {
          rethrow;
        }
        final existing = await _readLauncherFileBounded(
          cached,
          _maxPackageBytes,
        );
        if (sha256.convert(existing).toString() == expectedSha) {
          return;
        }
        await cached.delete();
        await temp.rename(cached.path);
      }
    } finally {
      await _deleteFileBestEffort(temp);
    }
  }

  Future<_PackageReferenceBytes> _readPackageFile(
    String path,
    String reference,
  ) async {
    final file = File(path);
    if (FileSystemEntity.typeSync(file.path, followLinks: false) ==
        FileSystemEntityType.notFound) {
      throw StateError('Package file does not exist: $path');
    }
    return _PackageReferenceBytes(
      reference: reference,
      bytes: await _readLauncherFileBounded(file, _maxPackageBytes),
    );
  }

  String _safePackageReference(String reference) {
    final uri = Uri.tryParse(reference);
    if (uri == null || !uri.hasScheme) {
      return reference.replaceAll(RegExp(r'[\r\n]+'), ' ');
    }
    return uri.replace(query: '', fragment: '', userInfo: '').toString();
  }

  Future<List<int>> _downloadBytes(Uri uri) async {
    final result = await fetchHttpsBytes(
      uri,
      maxBytes: _maxPackageBytes,
      label: 'Package download',
      totalTimeout: const Duration(minutes: 10),
    );
    return result.bytes;
  }
}

const _maxPackageBytes = 512 * 1024 * 1024;
const _maxManifestBytes = 1024 * 1024;

class _PackageReadResult {
  const _PackageReadResult({
    required this.archive,
    required this.manifest,
    required this.sha256Hex,
    required this.reference,
    required this.trust,
  });

  final SafeZipArchive archive;
  final ModManifest manifest;
  final String sha256Hex;
  final String reference;
  final String trust;
}

class _PackageReferenceBytes {
  const _PackageReferenceBytes({required this.reference, required this.bytes});

  final String reference;
  final List<int> bytes;
}

extension _FirstOrNull<T> on Iterable<T> {
  T? get firstOrNull {
    final iterator = this.iterator;
    if (iterator.moveNext()) {
      return iterator.current;
    }
    return null;
  }
}
