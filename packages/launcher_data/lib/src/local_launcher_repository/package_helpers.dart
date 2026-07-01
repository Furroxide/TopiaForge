part of '../local_launcher_repository.dart';

extension _PackageHelpers on LocalLauncherRepository {
  Future<_PackageReadResult> _readPackage(
    String packageReference, {
    String expectedSha256 = '',
  }) async {
    final reference = await _resolvePackageReference(
      packageReference,
      expectedSha256: expectedSha256,
    );
    final bytes = reference.bytes;
    if (bytes.length > 512 * 1024 * 1024) {
      throw StateError('Package is larger than the 512 MB launcher limit.');
    }
    final actualSha = sha256.convert(bytes).toString();
    if (expectedSha256.trim().isNotEmpty &&
        actualSha.toLowerCase() != expectedSha256.trim().toLowerCase()) {
      throw StateError(
        'Package SHA-256 mismatch for $packageReference. Expected $expectedSha256 but got $actualSha.',
      );
    }

    final archive = ZipDecoder().decodeBytes(bytes);
    for (final file in archive.files) {
      _safeArchivePath(file.name);
    }

    final manifestFile = archive.files.firstWhereOrNull(
      (file) => file.name.replaceAll('\\', '/') == 'robotopia.mod.json',
    );
    if (manifestFile == null || !manifestFile.isFile) {
      throw StateError('Package is missing robotopia.mod.json.');
    }

    final manifest = ModManifest.fromJson(
      jsonDecode(utf8.decode(manifestFile.content as List<int>))
          as Map<String, Object?>,
    );
    final entryAssembly = manifest.entryAssembly.replaceAll('\\', '/');
    final hasEntryAssembly = archive.files.any(
      (file) => file.isFile && file.name.replaceAll('\\', '/') == entryAssembly,
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
    );
  }

  Future<_PackageReferenceBytes> _resolvePackageReference(
    String packageReference, {
    required String expectedSha256,
  }) async {
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
      if (expectedSha256.trim().isEmpty) {
        throw StateError(
          'Remote packages require a SHA-256 hash before install or preview.',
        );
      }
      final cached = File(
        p.join(
          _packageCache.path,
          '${expectedSha256.toLowerCase()}.robotopiamod',
        ),
      );
      if (cached.existsSync()) {
        return _readPackageFile(cached.path, packageReference);
      }

      final bytes = await _downloadBytes(uri);
      await cached.create(recursive: true);
      await cached.writeAsBytes(bytes);
      return _PackageReferenceBytes(reference: packageReference, bytes: bytes);
    }

    if (uri != null && uri.hasScheme) {
      throw StateError('Unsupported package URL scheme: ${uri.scheme}');
    }

    return _readPackageFile(packageReference, packageReference);
  }

  Future<_PackageReferenceBytes> _readPackageFile(
    String path,
    String reference,
  ) async {
    final file = File(path);
    if (!file.existsSync()) {
      throw StateError('Package file does not exist: $path');
    }
    return _PackageReferenceBytes(
      reference: reference,
      bytes: await file.readAsBytes(),
    );
  }

  Future<List<int>> _downloadBytes(Uri uri) async {
    final client = HttpClient();
    try {
      final request = await client.getUrl(uri);
      final response = await request.close();
      if (response.statusCode < 200 || response.statusCode >= 300) {
        throw StateError(
          'Download failed for $uri with HTTP ${response.statusCode}.',
        );
      }
      final bytes = <int>[];
      await for (final chunk in response) {
        bytes.addAll(chunk);
        if (bytes.length > 512 * 1024 * 1024) {
          throw StateError('Package is larger than the 512 MB launcher limit.');
        }
      }
      return bytes;
    } finally {
      client.close(force: true);
    }
  }

  String _safeArchivePath(String rawPath) {
    final normalized = rawPath.replaceAll('\\', '/');
    final parts = normalized.split('/');
    if (normalized.startsWith('/') ||
        RegExp(r'^[A-Za-z]:/').hasMatch(normalized) ||
        parts.any((part) => part == '..')) {
      throw StateError(
        'Package contains a path outside the install directory: $rawPath',
      );
    }
    return normalized;
  }
}

class _PackageReadResult {
  const _PackageReadResult({
    required this.archive,
    required this.manifest,
    required this.sha256Hex,
    required this.reference,
  });

  final Archive archive;
  final ModManifest manifest;
  final String sha256Hex;
  final String reference;
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

  T? firstWhereOrNull(bool Function(T item) test) {
    for (final item in this) {
      if (test(item)) {
        return item;
      }
    }
    return null;
  }
}
