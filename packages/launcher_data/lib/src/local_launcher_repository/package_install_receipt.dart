part of '../local_launcher_repository.dart';

const _packageInstallReceiptFileName = 'topiaforge.install.json';
const _packageInstallReceiptSchemaVersion = 2;
const _minimumPackageInstallReceiptSchemaVersion = 1;
const _packageInstallReceiptValidatorVersion = '1';
const _localUnverifiedPackageTrust = 'local-unverified';
const _sha256VerifiedPackageTrust = 'sha256-verified';
const _maxReceiptSourceLength = 160;
const _maxReceiptSourceFileLength = 255;
const _maxReceiptSourceIdentifierLength = 128;
const _maxReceiptFiles = 8192;
const _maxReceiptTotalBytes = 2 * 1024 * 1024 * 1024;

extension _PackageInstallReceiptWriter on LocalLauncherRepository {
  Future<void> _writePackageInstallReceipt(
    Directory packageRoot,
    _PackageReadResult package,
    String source,
  ) async {
    if (!_isValidPackageReceiptSource(source)) {
      throw StateError('Package receipt source provenance is invalid.');
    }
    if (package.trust != _localUnverifiedPackageTrust &&
        package.trust != _sha256VerifiedPackageTrust) {
      throw StateError('Package receipt trust result is invalid.');
    }
    final rootType = FileSystemEntity.typeSync(
      packageRoot.path,
      followLinks: false,
    );
    if (rootType != FileSystemEntityType.directory) {
      throw StateError('Package receipt root must be a regular directory.');
    }

    final receiptFile = File(
      p.join(packageRoot.path, _packageInstallReceiptFileName),
    );
    final receiptType = FileSystemEntity.typeSync(
      receiptFile.path,
      followLinks: false,
    );
    if (receiptType == FileSystemEntityType.link) {
      throw StateError('Package install receipt cannot be a symbolic link.');
    }
    if (receiptType != FileSystemEntityType.notFound) {
      if (receiptType != FileSystemEntityType.file) {
        throw StateError('Package install receipt path is not a file.');
      }
      receiptFile.deleteSync();
    }

    final criticalPaths = <String>{
      'topiaforge.mod.json',
      package.manifest.entryAssembly.replaceAll('\\', '/'),
      ...package.manifest.apiAssemblies.map(
        (path) => path.replaceAll('\\', '/'),
      ),
    };
    final files = await _receiptPayloadFiles(packageRoot, criticalPaths);
    final receipt = <String, Object?>{
      'schemaVersion': _packageInstallReceiptSchemaVersion,
      'modId': package.manifest.id,
      'version': package.manifest.version,
      'sourceFile': _packageReceiptSourceFile(
        package.reference,
        package.manifest,
      ),
      'source': source,
      'sourceSha256': package.sha256Hex.toLowerCase(),
      'installedAtUtc': DateTime.now().toUtc().toIso8601String(),
      'validatorVersion': _packageInstallReceiptValidatorVersion,
      'trust': package.trust,
      'files': files,
    };
    receiptFile.writeAsStringSync(
      '${const JsonEncoder.withIndent('  ').convert(receipt)}\n',
      flush: true,
    );
  }
}

Future<List<Map<String, Object?>>> _receiptPayloadFiles(
  Directory packageRoot,
  Set<String> criticalPaths,
) async {
  final root = packageRoot.absolute;
  final pending = <Directory>[root];
  final payload = <File>[];
  while (pending.isNotEmpty) {
    final directory = pending.removeLast();
    final type = FileSystemEntity.typeSync(directory.path, followLinks: false);
    if (type != FileSystemEntityType.directory) {
      throw StateError(
        'Installed package contains a linked or special directory.',
      );
    }

    final entries = directory.listSync(followLinks: false)
      ..sort((left, right) => left.path.compareTo(right.path));
    for (final entity in entries) {
      final entityType = FileSystemEntity.typeSync(
        entity.path,
        followLinks: false,
      );
      if (entityType == FileSystemEntityType.directory) {
        pending.add(Directory(entity.path));
      } else if (entityType == FileSystemEntityType.file) {
        final relative = _receiptRelativePath(root, entity.path);
        if (relative != _packageInstallReceiptFileName) {
          if (payload.length >= _maxReceiptFiles) {
            throw StateError(
              'Installed package exceeds the receipt inventory limit.',
            );
          }
          payload.add(File(entity.path));
        }
      } else {
        throw StateError('Installed package contains a link or special file.');
      }
    }
  }

  payload.sort(
    (left, right) => _receiptRelativePath(
      root,
      left.path,
    ).compareTo(_receiptRelativePath(root, right.path)),
  );
  var totalBytes = 0;
  final result = <Map<String, Object?>>[];
  for (final file in payload) {
    final relative = _receiptRelativePath(root, file.path);
    final length = file.lengthSync();
    if (length < 0 ||
        length > _maxReceiptTotalBytes ||
        totalBytes > _maxReceiptTotalBytes - length) {
      throw StateError('Installed package exceeds the receipt byte limit.');
    }
    totalBytes += length;
    final digest = await sha256.bind(file.openRead()).first;
    if (file.lengthSync() != length) {
      throw StateError(
        'Installed package file changed while its receipt was created: '
        '$relative.',
      );
    }
    result.add({
      'path': relative,
      'length': length,
      'sha256': digest.toString().toLowerCase(),
      'critical': criticalPaths.contains(relative),
    });
  }
  return result;
}

String _receiptRelativePath(Directory root, String path) {
  final relative = p.relative(path, from: root.path);
  final portable = p.posix.joinAll(p.split(relative));
  return portableArchivePath(portable, label: 'Installed package receipt');
}

String _packageReceiptSourceFile(String reference, ModManifest manifest) {
  final uri = Uri.tryParse(reference);
  String source;
  if (uri != null && uri.hasScheme) {
    source = p.posix.basename(uri.path);
  } else {
    source = p.basename(reference);
  }
  if (source.trim().isEmpty || source == '.' || source == '/') {
    return '${manifest.id}-${manifest.version}.topiaforgemod';
  }
  final sanitized = source
      .replaceAll(RegExp(r'[\\/]'), '_')
      .replaceAll(RegExp(r'[\x00-\x1f\x7f]'), '_')
      .trim();
  if (sanitized.isEmpty) {
    return '${manifest.id}-${manifest.version}.topiaforgemod';
  }
  if (sanitized.length <= _maxReceiptSourceFileLength) return sanitized;
  var end = _maxReceiptSourceFileLength;
  if (_isLeadingSurrogate(sanitized.codeUnitAt(end - 1)) &&
      _isTrailingSurrogate(sanitized.codeUnitAt(end))) {
    end--;
  }
  return sanitized.substring(0, end);
}

String _packageReceiptSource({
  required String reference,
  String sourceId = '',
  String sourceKind = '',
}) {
  final kind = sourceKind.trim().toLowerCase();
  if (kind == 'inbox' || kind == 'cache' || kind == 'local') return kind;

  final normalizedId = sourceId.trim().toLowerCase();
  if (normalizedId.isNotEmpty) {
    return _isSafeReceiptSourceIdentifier(normalizedId)
        ? 'registry:$normalizedId'
        : 'registry';
  }
  if (kind == 'registry') return 'registry';

  final uri = Uri.tryParse(reference);
  if (uri != null && uri.scheme.toLowerCase() == 'https') {
    final host = uri.host.trim().toLowerCase();
    return _isSafeReceiptRemoteHost(host) ? 'remote:$host' : 'remote';
  }
  if (kind == 'remote') return 'remote';
  return 'local';
}

bool _isValidPackageReceiptSource(Object? value) {
  if (value is! String ||
      value.isEmpty ||
      value.length > _maxReceiptSourceLength) {
    return false;
  }
  if (const {'local', 'inbox', 'cache', 'registry', 'remote'}.contains(value)) {
    return true;
  }
  if (value.startsWith('registry:')) {
    return _isSafeReceiptSourceIdentifier(value.substring('registry:'.length));
  }
  return value.startsWith('remote:') &&
      _isSafeReceiptRemoteHost(value.substring('remote:'.length));
}

bool _isSafeReceiptSourceIdentifier(String value) {
  return value.isNotEmpty &&
      value.length <= _maxReceiptSourceIdentifierLength &&
      !value.contains('..') &&
      RegExp(r'^[a-z0-9](?:[a-z0-9._-]*[a-z0-9])?$').hasMatch(value);
}

bool _isSafeReceiptRemoteHost(String value) {
  return value.isNotEmpty &&
      value.length <= _maxReceiptSourceIdentifierLength &&
      !value.contains('..') &&
      RegExp(r'^[a-z0-9](?:[a-z0-9.-]*[a-z0-9])?$').hasMatch(value);
}

bool _isValidPackageReceiptSourceFile(Object? value) {
  if (value is! String ||
      value.trim().isEmpty ||
      value.length > _maxReceiptSourceFileLength ||
      p.basename(value) != value ||
      value.contains('/') ||
      value.contains('\\')) {
    return false;
  }
  return !value.runes.any((value) => value < 0x20 || value == 0x7f);
}

bool _isLeadingSurrogate(int value) => value >= 0xd800 && value <= 0xdbff;

bool _isTrailingSurrogate(int value) => value >= 0xdc00 && value <= 0xdfff;
