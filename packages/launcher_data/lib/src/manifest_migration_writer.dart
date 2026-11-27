import 'dart:convert';
import 'dart:io';
import 'dart:math';
import 'package:crypto/crypto.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;

final class ManifestMigrationSnapshot {
  ManifestMigrationSnapshot._(this.path, this.sourceText, List<int> bytes)
    : _bytes = List.unmodifiable(bytes);
  final String path, sourceText;
  final List<int> _bytes;
}

final class ManifestMigrationCommitResult {
  const ManifestMigrationCommitResult({
    required this.path,
    required this.disposition,
    required this.beforeSha256,
    required this.afterSha256,
  });
  final String path, beforeSha256, afterSha256;
  final ManifestMigrationDisposition disposition;
}

/// A single-file transaction. Source bytes are rechecked under a cooperative
/// lease; this is not an OS compare-and-swap against arbitrary external editors.
final class ManifestMigrationWriter {
  const ManifestMigrationWriter({this.replaceFile});

  /// The replacement boundary, injectable for failure tests. The default uses
  /// one same-directory native overwrite rename and never moves the old file.
  final Future<void> Function(File staging, File destination)? replaceFile;
  static final _activePaths = <String>{};

  Future<ManifestMigrationSnapshot> prepare(String manifestPath) async {
    final requested = p.normalize(p.absolute(manifestPath));
    // Windows TEMP and callers may use legitimate 8.3 spellings. Reject
    // linked input first, then freeze one filesystem name for snapshot/lease.
    _unlinkedFile(requested);
    final path = File(requested).resolveSymbolicLinksSync();
    _unlinkedFile(requested);
    if (!p.equals(File(requested).resolveSymbolicLinksSync(), path)) {
      throw StateError('Manifest path changed while preparing migration.');
    }
    final bytes = _readSource(path);
    return ManifestMigrationSnapshot._(path, utf8.decode(bytes), bytes);
  }

  Future<ManifestMigrationCommitResult> commit(
    ManifestMigrationSnapshot snapshot,
    ManifestMigrationPlan plan,
  ) async {
    final output = plan.outputText;
    if (plan.sourceLabel != snapshot.path ||
        plan.sourceText != snapshot.sourceText ||
        output == null ||
        (plan.disposition != ManifestMigrationDisposition.migrated &&
            !(plan.disposition == ManifestMigrationDisposition.invalidStub &&
                plan.mode == ManifestMigrationMode.stub))) {
      throw StateError(
        'Only a writable migration plan bound to this exact source snapshot can commit.',
      );
    }
    final bytes = utf8.encode(output);
    if (bytes.length > 1024 * 1024) {
      throw StateError('Migrated manifest exceeds the 1 MiB limit.');
    }
    _requireUnchanged(snapshot);
    final key = Platform.isWindows
        ? snapshot.path.toLowerCase()
        : snapshot.path;
    if (!_activePaths.add(key)) {
      throw StateError('Another migration owns this manifest.');
    }
    RandomAccessFile? lease;
    File? staging;
    var committed = false;
    try {
      _ordinaryAncestors(Directory.systemTemp.path);
      final temporaryRoot = Directory.systemTemp.resolveSymbolicLinksSync();
      final directory = Directory(
        p.join(temporaryRoot, 'topiaforge-manifest-migration-locks'),
      );
      _ordinaryAncestors(directory.parent.path);
      if (!directory.existsSync()) {
        directory.createSync();
      }
      _ordinaryAncestors(directory.path);
      final lock = File(
        p.join(directory.path, '${sha256.convert(utf8.encode(key))}.lock'),
      );
      final lockType = FileSystemEntity.typeSync(lock.path, followLinks: false);
      if (lockType == FileSystemEntityType.notFound) {
        try {
          lock.createSync(exclusive: true);
        } on FileSystemException {
          if (FileSystemEntity.typeSync(lock.path, followLinks: false) !=
              FileSystemEntityType.file) {
            rethrow;
          }
        }
      }
      _ordinaryFile(lock.path);
      lease = lock.openSync(mode: FileMode.append);
      try {
        lease.lockSync(FileLock.exclusive);
      } on FileSystemException {
        throw StateError('Another process owns this manifest migration lease.');
      }
      _requireUnchanged(snapshot);
      final random = Random.secure();
      final nonce = List.generate(
        16,
        (_) => random.nextInt(256).toRadixString(16).padLeft(2, '0'),
      ).join();
      staging = File('${snapshot.path}.migration-$nonce.tmp');
      staging.createSync(exclusive: true);
      _ordinaryFile(staging.path);
      final file = staging.openSync(mode: FileMode.write);
      try {
        file.writeFromSync(bytes);
        file.flushSync();
      } finally {
        file.closeSync();
      }
      _ordinaryFile(staging.path);
      _requireUnchanged(snapshot);
      if (replaceFile != null) {
        await replaceFile!(staging, File(snapshot.path));
      } else {
        // Dart3.12.2 uses MoveFileExW(REPLACE_EXISTING|WRITE_THROUGH) on
        // Windows and rename on POSIX. Same-directory staging forbids a copy
        // fallback across volumes. No backup rename or target deletion occurs.
        staging.renameSync(snapshot.path);
      }
      committed = true;
      return ManifestMigrationCommitResult(
        path: snapshot.path,
        disposition: plan.disposition,
        beforeSha256: sha256.convert(snapshot._bytes).toString(),
        afterSha256: sha256.convert(bytes).toString(),
      );
    } finally {
      if (!committed && staging != null) {
        // A replaced ancestor is never followed to delete a foreign file.
        try {
          _ordinaryAncestors(staging.parent.path);
          if (FileSystemEntity.typeSync(staging.path, followLinks: false) ==
              FileSystemEntityType.file) {
            staging.deleteSync();
          }
        } on FileSystemException {
          /* Preserve the original failure. */
        } on StateError {
          /* Refuse cleanup through changed paths. */
        }
      }
      try {
        lease?.closeSync();
      } finally {
        _activePaths.remove(key);
      }
      // Stable lease files intentionally remain outside the project: deleting
      // and recreating a lock path could admit two independently locked files.
    }
  }
}

void _requireUnchanged(ManifestMigrationSnapshot snapshot) {
  final current = _readSource(snapshot.path);
  var unchanged = current.length == snapshot._bytes.length;
  for (var index = 0; unchanged && index < current.length; index++) {
    unchanged = current[index] == snapshot._bytes[index];
  }
  if (!unchanged) {
    throw StateError(
      'The manifest changed after migration preparation; no replacement was written. Read it again before retrying.',
    );
  }
}

List<int> _readSource(String path) {
  _ordinaryFile(path);
  final before = File(path).statSync();
  final file = File(path).openSync();
  List<int> bytes;
  try {
    final size = file.lengthSync();
    if (size <= 0 || size > 1024 * 1024) {
      throw StateError('Manifest must contain 1–1048576 bytes.');
    }
    bytes = file.readSync(size + 1);
    if (bytes.length != size || file.lengthSync() != size) {
      throw StateError('Manifest changed while reading.');
    }
  } finally {
    file.closeSync();
  }
  _ordinaryFile(path);
  final after = File(path).statSync();
  if (before.size != after.size ||
      before.modified != after.modified ||
      before.changed != after.changed) {
    throw StateError('Manifest changed while reading.');
  }
  return bytes;
}

void _ordinaryFile(String path) {
  _unlinkedFile(path);
  if (!p.equals(
    File(path).resolveSymbolicLinksSync(),
    p.normalize(p.absolute(path)),
  )) {
    throw StateError('Expected a regular unlinked manifest file: $path');
  }
}

void _unlinkedFile(String path) {
  _ordinaryAncestors(p.dirname(path));
  if (FileSystemEntity.typeSync(path, followLinks: false) !=
      FileSystemEntityType.file) {
    throw StateError('Expected a regular unlinked manifest file: $path');
  }
}

void _ordinaryAncestors(String path) {
  var current = p.normalize(p.absolute(path));
  while (true) {
    if (FileSystemEntity.typeSync(current, followLinks: false) !=
        FileSystemEntityType.directory) {
      throw StateError(
        'Expected an ordinary directory without links: $current',
      );
    }
    final parent = p.dirname(current);
    if (parent == current) break;
    current = parent;
  }
}
