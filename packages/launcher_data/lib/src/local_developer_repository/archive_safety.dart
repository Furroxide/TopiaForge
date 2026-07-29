part of '../local_developer_repository.dart';

const _maxDeveloperArchiveBytes = 512 * 1024 * 1024;
const _maxDeveloperArchiveEntries = 8192;
const _maxDeveloperArchiveEntryBytes = 1024 * 1024 * 1024;
const _maxDeveloperArchiveExpandedBytes = 2 * 1024 * 1024 * 1024;
const _maxDeveloperManifestBytes = 1024 * 1024;
const _maxDeveloperCatalogBytes = 16 * 1024 * 1024;
const _maxTemplateCopyEntries = 8192;
const _maxTemplateCopyFileBytes = 512 * 1024 * 1024;
const _maxTemplateCopyBytes = 2 * 1024 * 1024 * 1024;

SafeZipArchive _decodeDeveloperArchive(
  List<int> bytes, {
  required String label,
}) => SafeZipArchive.decode(bytes, label: label);

String _portableDeveloperArchivePath(String rawPath, {required String label}) =>
    portableArchivePath(rawPath, label: label);

String _requireDeveloperPackageSegment(String value, {required String label}) {
  final safe = _portableDeveloperArchivePath(value, label: label);
  if (safe.contains('/')) {
    throw StateError('$label must be a single portable path segment: $value');
  }
  return safe;
}

List<int> _readDeveloperArchiveEntryBounded(
  SafeZipEntry file, {
  required int maxBytes,
  required String label,
}) {
  return file.readBytes(maxBytes: maxBytes, label: label);
}

void _extractDeveloperArchive(
  SafeZipArchive archive,
  Directory target, {
  required String label,
}) {
  archive.extractTo(target);
}

Future<List<int>> _readDeveloperFileBounded(
  File file, {
  required int maxBytes,
  required String label,
}) async {
  _recoverDeveloperAtomicBackupIfMissing(file);
  final initial = _requireStableDeveloperFile(file, label);
  final first = await _readDeveloperFileOnce(
    file,
    maxBytes: maxBytes,
    label: label,
  );
  _requireStableDeveloperFile(file, label, expected: initial);
  final middle = file.statSync();
  final second = await _readDeveloperFileOnce(
    file,
    maxBytes: maxBytes,
    label: label,
  );
  _requireStableDeveloperFile(file, label, expected: middle);
  if (sha256.convert(first).toString() != sha256.convert(second).toString()) {
    throw StateError('$label changed while it was being read.');
  }
  return second;
}

List<int> _readDeveloperFileBoundedSync(
  File file, {
  required int maxBytes,
  required String label,
}) {
  _recoverDeveloperAtomicBackupIfMissing(file);
  final initial = _requireStableDeveloperFile(file, label);
  final first = _readDeveloperFileOnceSync(
    file,
    maxBytes: maxBytes,
    label: label,
  );
  _requireStableDeveloperFile(file, label, expected: initial);
  final middle = file.statSync();
  final second = _readDeveloperFileOnceSync(
    file,
    maxBytes: maxBytes,
    label: label,
  );
  _requireStableDeveloperFile(file, label, expected: middle);
  if (sha256.convert(first).toString() != sha256.convert(second).toString()) {
    throw StateError('$label changed while it was being read.');
  }
  return second;
}

Future<List<int>> _readDeveloperFileOnce(
  File file, {
  required int maxBytes,
  required String label,
}) async {
  final input = await file.open();
  try {
    if (await input.length() > maxBytes) {
      throw StateError(
        '$label is larger than the ${_byteLimitLabel(maxBytes)}.',
      );
    }
    final output = BytesBuilder(copy: false);
    while (output.length <= maxBytes) {
      final remaining = maxBytes + 1 - output.length;
      final chunk = await input.read(remaining < 65536 ? remaining : 65536);
      if (chunk.isEmpty) {
        break;
      }
      output.add(chunk);
    }
    if (output.length > maxBytes) {
      throw StateError(
        '$label is larger than the ${_byteLimitLabel(maxBytes)}.',
      );
    }
    return output.takeBytes();
  } finally {
    await input.close();
  }
}

List<int> _readDeveloperFileOnceSync(
  File file, {
  required int maxBytes,
  required String label,
}) {
  final input = file.openSync();
  try {
    if (input.lengthSync() > maxBytes) {
      throw StateError(
        '$label is larger than the ${_byteLimitLabel(maxBytes)}.',
      );
    }
    final output = BytesBuilder(copy: false);
    while (output.length <= maxBytes) {
      final remaining = maxBytes + 1 - output.length;
      final chunk = input.readSync(remaining < 65536 ? remaining : 65536);
      if (chunk.isEmpty) {
        break;
      }
      output.add(chunk);
    }
    if (output.length > maxBytes) {
      throw StateError(
        '$label is larger than the ${_byteLimitLabel(maxBytes)}.',
      );
    }
    return output.takeBytes();
  } finally {
    input.closeSync();
  }
}

FileStat _requireStableDeveloperFile(
  File file,
  String label, {
  FileStat? expected,
}) {
  final type = FileSystemEntity.typeSync(file.path, followLinks: false);
  if (type == FileSystemEntityType.link) {
    throw StateError('$label cannot be read through a symbolic link.');
  }
  if (type != FileSystemEntityType.file) {
    throw StateError('$label is not a regular file: ${file.path}');
  }
  final actual = file.statSync();
  if (expected != null &&
      (actual.size != expected.size || actual.modified != expected.modified)) {
    throw StateError('$label changed while it was being read.');
  }
  return actual;
}

String _byteLimitLabel(int maxBytes) {
  if (maxBytes % (1024 * 1024) == 0) {
    return '${maxBytes ~/ (1024 * 1024)} MB limit';
  }
  return '$maxBytes-byte limit';
}

class _StagedDeveloperDirectorySwap {
  _StagedDeveloperDirectorySwap({
    required this.target,
    required this.backup,
    this.staging,
  });

  final Directory target;
  final Directory backup;
  final Directory? staging;
  bool _backedUp = false;
  bool _installed = false;

  void commit() {
    final targetType = FileSystemEntity.typeSync(
      target.path,
      followLinks: false,
    );
    if (targetType == FileSystemEntityType.link) {
      throw StateError('Refusing to replace symbolic link: ${target.path}');
    }
    if (targetType != FileSystemEntityType.notFound &&
        targetType != FileSystemEntityType.directory) {
      throw StateError('Package target is not a directory: ${target.path}');
    }
    target.parent.createSync(recursive: true);
    if (targetType == FileSystemEntityType.directory) {
      target.renameSync(backup.path);
      _backedUp = true;
    }
    try {
      final candidate = staging;
      if (candidate != null) {
        candidate.renameSync(target.path);
        _installed = true;
      }
    } on Object {
      rollback();
      rethrow;
    }
  }

  void rollback() {
    if (_installed && target.existsSync()) {
      target.deleteSync(recursive: true);
      _installed = false;
    }
    if (_backedUp && backup.existsSync()) {
      backup.renameSync(target.path);
      _backedUp = false;
    }
  }
}
