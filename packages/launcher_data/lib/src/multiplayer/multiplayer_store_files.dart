part of 'multiplayer_store.dart';

/// The store's files on disk, all in [directory]: bounded reads that put
/// back what an interrupted write moved aside, atomic writes, damaged files
/// set aside, and the sweep of temporary files that interrupted writes left.
///
/// Leftovers are known by the names this store gives them,
/// `<file>.<token>.bak` and `<file>.<token>.tmp` with a token of
/// `<process id>-<microseconds>-<random>`; anything else in the folder is
/// never put back, swept or dropped.
final class _StoreFiles {
  _StoreFiles(this.directory, this._clock);

  final String directory;
  final DateTime Function() _clock;
  final _random = Random();

  /// The temporary files writes in this isolate are using. No sweep deletes
  /// one of them, whatever its age.
  static final _inUse = <String>{};

  /// A token of this store's own; its middle part is the store's clock, in
  /// microseconds, when the write began.
  static final _token = RegExp(r'^[0-9]+-(-?[0-9]+)-[0-9]+$');

  /// Reads [file], refusing more than [maximumBytes]. A missing file is
  /// first put back from the newest backup an interrupted write left.
  Future<_ReadResult> read(File file, int maximumBytes) async {
    FileSystemEntityType type;
    try {
      type = await FileSystemEntity.type(file.path, followLinks: false);
      if (type == FileSystemEntityType.notFound) {
        switch (await _restoreBackup(file)) {
          case _Restore.held:
            // What the backup holds is unread, not missing.
            return const _Unreadable();
          case _Restore.restored:
            type = await FileSystemEntity.type(file.path, followLinks: false);
          case _Restore.none:
            break;
        }
      }
    } on FileSystemException {
      // A folder that cannot be searched for backups may hold one, so the
      // file is unread rather than missing.
      return const _Unreadable();
    }
    if (type == FileSystemEntityType.notFound) return const _Missing();
    // A link or a folder in a file's place is set aside, never followed.
    if (type != FileSystemEntityType.file) return const _Damaged();
    try {
      if (await file.length() > maximumBytes) return const _Damaged();
      final bytes = await file.readAsBytes();
      return bytes.length > maximumBytes ? const _Damaged() : _Read(bytes);
    } on FileSystemException {
      return const _Unreadable();
    }
  }

  /// Puts back the newest `<name>.<token>.bak` of [file], which a write
  /// stopped between its two moves left behind, and drops older ones. Only
  /// regular files count, never a link or a folder. A backup that is still
  /// there but cannot be moved just now, as when another program holds it,
  /// is [_Restore.held]: an older one never stands in for it, and nothing is
  /// dropped. A folder that is there but cannot be listed throws, since it
  /// may hold one.
  Future<_Restore> _restoreBackup(File file) async {
    final fileName = p.basename(file.path);
    final backups = <({File file, DateTime modified, int writtenAt})>[];
    try {
      await for (final entity in Directory(
        directory,
      ).list(followLinks: false)) {
        final writtenAt = _writtenAt(p.basename(entity.path), fileName, '.bak');
        if (entity is File && writtenAt != null) {
          final modified = (await entity.stat()).modified;
          backups.add((file: entity, modified: modified, writtenAt: writtenAt));
        }
      }
    } on FileSystemException catch (error) {
      // No folder holds no backup. A folder that cannot be listed may hold
      // one, so that failure reaches [read].
      if (_namesNoFolder(error)) return _Restore.none;
      rethrow;
    }
    // Newest first. A file system may keep whole seconds only, as Windows
    // does here, so between two backups of one second the later write's
    // wins: it moved aside the later version.
    backups.sort((left, right) {
      final byTime = right.modified.compareTo(left.modified);
      return byTime != 0 ? byTime : right.writtenAt.compareTo(left.writtenAt);
    });
    for (final backup in backups) {
      try {
        await backup.file.rename(file.path);
      } on FileSystemException {
        // One that vanished meanwhile is passed over; one still there is
        // held, and only it has the latest contents.
        if (await _exists(backup.file)) return _Restore.held;
        continue;
      }
      for (final stale in backups) {
        if (!identical(stale.file, backup.file)) {
          await _deleteEntity(stale.file.path);
        }
      }
      return _Restore.restored;
    }
    return _Restore.none;
  }

  /// Deletes the temporary files of [fileNames] that writes stopped part way
  /// left: this store's own names only, regular files only, never one a write
  /// in this isolate is using, and only once [age] old, so a write still
  /// running in another process keeps its own. Tidying only; never throws.
  Future<void> sweepTemporaries(List<String> fileNames, Duration age) async {
    final candidates = <File>[];
    try {
      await for (final entity in Directory(
        directory,
      ).list(followLinks: false)) {
        final name = p.basename(entity.path);
        if (entity is File &&
            fileNames.any((file) => _writtenAt(name, file, '.tmp') != null)) {
          candidates.add(File(p.join(directory, name)));
        }
      }
    } on FileSystemException {
      return;
    }
    final now = _clock();
    for (final temporary in candidates) {
      final stat = await temporary.stat();
      if (stat.type != FileSystemEntityType.file ||
          now.difference(stat.modified) < age ||
          _inUse.contains(temporary.path)) {
        continue;
      }
      await _deleteEntity(temporary.path);
    }
  }

  /// Whether listing failed because there is no folder there at all: the
  /// path is missing, or it or a parent is not a folder (ENOTDIR on Linux and
  /// macOS, ERROR_DIRECTORY on Windows).
  static bool _namesNoFolder(FileSystemException error) =>
      error is PathNotFoundException ||
      error.osError?.errorCode == (Platform.isWindows ? 267 : 20);

  /// The clock reading in a name this store gave a leftover of [fileName],
  /// `<fileName>.<token><suffix>`; null for any other name.
  static int? _writtenAt(String name, String fileName, String suffix) {
    final prefix = '$fileName.';
    if (name.length <= prefix.length + suffix.length ||
        !name.startsWith(prefix) ||
        !name.endsWith(suffix)) {
      return null;
    }
    final match = _token.firstMatch(
      name.substring(prefix.length, name.length - suffix.length),
    );
    return match == null ? null : int.tryParse(match.group(1)!);
  }

  /// Renames [file] to a damaged copy beside it and keeps the newest
  /// [MultiplayerStore.keptDamagedCopies]; false when it could not be moved.
  Future<bool> setAside(File file) async {
    final stem = p.basenameWithoutExtension(file.path);
    final stamp = _clock().toUtc().toIso8601String().replaceAll(
      RegExp(r'[^0-9TZ]'),
      '',
    );
    var target = File(p.join(directory, '$stem.damaged-$stamp.json'));
    for (var attempt = 2; await _exists(target) && attempt < 100; attempt++) {
      target = File(p.join(directory, '$stem.damaged-$stamp-$attempt.json'));
    }
    try {
      final type = await FileSystemEntity.type(file.path, followLinks: false);
      if (type == FileSystemEntityType.link) {
        await Link(file.path).rename(target.path);
      } else if (type == FileSystemEntityType.directory) {
        await Directory(file.path).rename(target.path);
      } else {
        await file.rename(target.path);
      }
    } on FileSystemException {
      return false;
    }
    await _pruneDamaged(stem);
    return true;
  }

  Future<void> _pruneDamaged(String stem) async {
    try {
      final copies = <String>[];
      await for (final entity in Directory(
        directory,
      ).list(followLinks: false)) {
        final name = p.basename(entity.path);
        if (name.startsWith('$stem.damaged-') && name.endsWith('.json')) {
          copies.add(entity.path);
        }
      }
      copies.sort();
      for (final path in copies.take(
        max(0, copies.length - MultiplayerStore.keptDamagedCopies),
      )) {
        await _deleteEntity(path);
      }
    } on FileSystemException {
      // Old copies are only tidied; leaving one costs nothing.
    }
  }

  /// Writes [bytes] to a temporary file beside [file] and moves it into
  /// place, so a reader sees the old file or the new one, never a mix.
  Future<void> write(File file, List<int> bytes, String label) async {
    final token =
        '$pid-${_clock().microsecondsSinceEpoch}-${_random.nextInt(1 << 30)}';
    final temporary = File('${file.path}.$token.tmp');
    _inUse.add(temporary.path);
    try {
      await Directory(directory).create(recursive: true);
      final type = await FileSystemEntity.type(file.path, followLinks: false);
      if (type != FileSystemEntityType.notFound &&
          type != FileSystemEntityType.file &&
          !await setAside(file)) {
        throw const FileSystemException('Not a regular file.');
      }
      await temporary.writeAsBytes(bytes, flush: true);
      await _moveIntoPlace(temporary, file, token);
    } on FileSystemException {
      throw MultiplayerStoreException('The $label could not be saved.');
    } finally {
      await _deleteEntity(temporary.path);
      _inUse.remove(temporary.path);
    }
  }

  Future<void> _moveIntoPlace(File temporary, File file, String token) async {
    try {
      await temporary.rename(file.path);
      return;
    } on FileSystemException {
      if (!await _exists(file)) rethrow;
    }
    // Windows can refuse to replace a file another program holds for a
    // moment: move it out of the way, put the new one in, then drop it.
    final backup = File('${file.path}.$token.bak');
    await file.rename(backup.path);
    try {
      await temporary.rename(file.path);
    } on FileSystemException {
      if (!await _exists(file)) await backup.rename(file.path);
      rethrow;
    }
    await _deleteEntity(backup.path);
  }

  static Future<bool> _exists(File file) async =>
      await FileSystemEntity.type(file.path, followLinks: false) !=
      FileSystemEntityType.notFound;

  /// Deletes a file or a link itself. A folder is left alone: the store
  /// never deletes anything it did not write.
  static Future<void> _deleteEntity(String path) async {
    try {
      final type = await FileSystemEntity.type(path, followLinks: false);
      if (type == FileSystemEntityType.link) {
        await Link(path).delete();
      } else if (type == FileSystemEntityType.file) {
        await File(path).delete();
      }
    } on FileSystemException {
      // Best effort: a leftover temporary file or old copy is harmless.
    }
  }
}

bool _sameBytes(List<int> left, List<int> right) {
  if (left.length != right.length) return false;
  for (var index = 0; index < left.length; index++) {
    if (left[index] != right[index]) return false;
  }
  return true;
}

/// What [_StoreFiles._restoreBackup] did.
enum _Restore {
  /// There was no backup to put back.
  none,

  /// The newest backup is back in the file's place.
  restored,

  /// The newest backup could not be moved just now.
  held,
}

sealed class _ReadResult {
  const _ReadResult();
}

final class _Missing extends _ReadResult {
  const _Missing();
}

final class _Unreadable extends _ReadResult {
  const _Unreadable();
}

final class _Damaged extends _ReadResult {
  const _Damaged();
}

final class _Read extends _ReadResult {
  const _Read(this.bytes);

  final List<int> bytes;
}
