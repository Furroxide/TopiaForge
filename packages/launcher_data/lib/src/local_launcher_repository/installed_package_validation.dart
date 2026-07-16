part of '../local_launcher_repository.dart';

const _maxInstalledReceiptBytes = 4 * 1024 * 1024;

class _InstalledPackageValidation {
  const _InstalledPackageValidation({
    required this.errors,
    required this.sourceSha256,
    required this.trust,
  });

  final List<String> errors;
  final String sourceSha256;
  final String trust;
}

extension _InstalledPackageValidationHelpers on LocalLauncherRepository {
  Future<_InstalledPackageValidation> _validateInstalledPackage(
    Directory packageRoot,
    ModManifest manifest,
  ) async {
    final receiptFile = File(
      p.join(packageRoot.path, _packageInstallReceiptFileName),
    );
    final receiptType = FileSystemEntity.typeSync(
      receiptFile.path,
      followLinks: false,
    );
    if (receiptType != FileSystemEntityType.file) {
      return const _InstalledPackageValidation(
        errors: [
          'Install receipt is missing or not a regular file. Reinstall or repair this mod.',
        ],
        sourceSha256: '',
        trust: '',
      );
    }

    final errors = <String>[];
    String sourceSha256 = '';
    String trust = '';
    String receiptDigest = '';
    Map<String, Object?> receipt;
    try {
      final bytes = await _readLauncherFileBounded(
        receiptFile,
        _maxInstalledReceiptBytes,
      );
      receiptDigest = sha256.convert(bytes).toString();
      final decoded = jsonDecode(utf8.decode(bytes));
      if (decoded is! Map) {
        throw const FormatException('receipt root must be an object');
      }
      receipt = decoded.map((key, value) => MapEntry(key.toString(), value));
    } on Object catch (error) {
      return _InstalledPackageValidation(
        errors: [
          'Install receipt is unreadable: $error. Reinstall or repair this mod.',
        ],
        sourceSha256: '',
        trust: '',
      );
    }

    final schemaVersion = receipt['schemaVersion'];
    if (schemaVersion is! int ||
        schemaVersion < _minimumPackageInstallReceiptSchemaVersion ||
        schemaVersion > _packageInstallReceiptSchemaVersion) {
      errors.add('Install receipt schemaVersion is unsupported.');
    }
    if (receipt['validatorVersion'] != _packageInstallReceiptValidatorVersion) {
      errors.add('Install receipt validatorVersion is unsupported.');
    }
    if (receipt['modId'] != manifest.id ||
        receipt['version'] != manifest.version) {
      errors.add('Install receipt identity does not match the manifest.');
    }

    final rawSourceSha256 = receipt['sourceSha256'];
    sourceSha256 = rawSourceSha256 is String ? rawSourceSha256 : '';
    if (!RegExp(r'^[0-9a-f]{64}$').hasMatch(sourceSha256)) {
      errors.add('Install receipt source SHA-256 is invalid.');
    }
    final rawTrust = receipt['trust'];
    trust = rawTrust is String ? rawTrust : '';
    if (trust != _localUnverifiedPackageTrust &&
        trust != _sha256VerifiedPackageTrust) {
      errors.add('Install receipt trust result is invalid.');
    }
    if (!_isValidPackageReceiptSourceFile(receipt['sourceFile'])) {
      errors.add('Install receipt source file is invalid.');
    }
    if (schemaVersion is int &&
        schemaVersion >= 2 &&
        !_isValidPackageReceiptSource(receipt['source'])) {
      errors.add('Install receipt source provenance is invalid.');
    }
    final rawInstalledAt = receipt['installedAtUtc'];
    final installedAt = rawInstalledAt is String ? rawInstalledAt : '';
    if (DateTime.tryParse(installedAt) == null) {
      errors.add('Install receipt timestamp is invalid.');
    }

    final criticalPaths = <String>{
      'topiaforge.mod.json',
      manifest.entryAssembly.replaceAll('\\', '/'),
      ...manifest.apiAssemblies.map((path) => path.replaceAll('\\', '/')),
    };
    final expected = <String, Map<String, Object?>>{};
    final rawFiles = receipt['files'];
    if (rawFiles is! List || rawFiles.length > _maxReceiptFiles) {
      errors.add('Install receipt file inventory exceeds the supported limit.');
    } else {
      var totalBytes = 0;
      for (final raw in rawFiles) {
        if (raw is! Map) {
          errors.add('Install receipt contains a malformed file record.');
          continue;
        }
        final item = raw.map((key, value) => MapEntry(key.toString(), value));
        final rawPath = item['path'];
        String path;
        try {
          if (rawPath is! String) throw const FormatException();
          path = portableArchivePath(rawPath, label: 'Install receipt path');
          if (path != rawPath || path == _packageInstallReceiptFileName) {
            throw const FormatException();
          }
        } on Object {
          errors.add('Install receipt contains an invalid file path.');
          continue;
        }
        if (expected.containsKey(path)) {
          errors.add('Install receipt contains duplicate file path $path.');
          continue;
        }
        final length = item['length'];
        if (length is! int ||
            length < 0 ||
            length > _maxReceiptTotalBytes ||
            totalBytes > _maxReceiptTotalBytes - length) {
          errors.add('Install receipt contains an invalid length for $path.');
          continue;
        }
        totalBytes += length;
        if (item['sha256'] is! String ||
            !RegExp(r'^[0-9a-f]{64}$').hasMatch(item['sha256']! as String)) {
          errors.add('Install receipt contains an invalid SHA-256 for $path.');
        }
        if (item['critical'] is! bool ||
            item['critical'] != criticalPaths.contains(path)) {
          errors.add(
            'Install receipt critical classification changed for $path.',
          );
        }
        expected[path] = item;
      }
    }

    if (errors.isEmpty) {
      try {
        final actual = await _receiptPayloadFiles(packageRoot, criticalPaths);
        for (final item in actual) {
          final path = item['path']! as String;
          final recorded = expected.remove(path);
          if (recorded == null) {
            errors.add('Installed package contains unreceipted file $path.');
          } else if (recorded['length'] != item['length'] ||
              recorded['sha256'] != item['sha256']) {
            errors.add('Installed package file changed: $path.');
          }
        }
        for (final missing in expected.keys) {
          errors.add('Installed package file is missing: $missing.');
        }
      } on Object catch (error) {
        errors.add(
          'Installed package files could not be verified safely: $error',
        );
      }
    }

    if (errors.isEmpty) {
      errors.addAll(
        await _cachedInstalledMetadataErrors(packageRoot, receiptDigest),
      );
    }
    return _InstalledPackageValidation(
      errors: errors
          .map((error) => '$error Reinstall or repair this mod.')
          .toList(growable: false),
      sourceSha256: sourceSha256,
      trust: trust,
    );
  }

  Future<List<String>> _cachedInstalledMetadataErrors(
    Directory packageRoot,
    String receiptDigest,
  ) {
    final prefix = '${packageRoot.absolute.path}|';
    final key = '$prefix$receiptDigest';
    _installedMetadataCache.removeWhere(
      (candidate, _) => candidate.startsWith(prefix) && candidate != key,
    );
    return _installedMetadataCache.putIfAbsent(key, () async {
      try {
        return _packageMetadataValidator != null
            ? await _packageMetadataValidator(packageRoot)
            : await _runManagedPackageValidator(packageRoot);
      } on Object catch (error) {
        return ['Managed package metadata could not be verified: $error'];
      }
    });
  }
}

String? _gameArchitecture(GameInstall install) =>
    install.architecture.isEmpty ? null : install.architecture;

String _gamePlatform(GameInstall install) => switch (install.layout) {
  GameInstallLayout.macAppBundle => 'macos',
  GameInstallLayout.windowsNative || GameInstallLayout.linuxProton => 'windows',
};

List<String> _gameContentTargets(GameInstall install) =>
    switch (install.layout) {
      GameInstallLayout.macAppBundle => const ['code', 'standaloneosx'],
      GameInstallLayout.windowsNative ||
      GameInstallLayout.linuxProton => const ['code', 'standalonewindows64'],
    };
