part of 'multiplayer_store.dart';

/// The store's files on disk, all in [directory]: bounded reads that put
/// back what an interrupted write moved aside, atomic writes, damaged files
/// set aside, and the sweep of temporary files that interrupted writes left.
///
/// Leftovers are known by the names this store gives them,
/// `<file>.<token>.bak` and `<file>.<token>.tmp` with a token of
/// `<process id>-<microseconds>-<random>`, and damaged copies by
/// `<stem>.damaged-<UTC time>.json`; anything else in the folder is never
/// put back, swept or dropped.
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
  /// first put back from the newest backup an interrupted write left, unless
  /// [restore] is false: that backup is then read where it stands, and
  /// nothing moves. A reader that moved a backup back could move it over the
  /// file a write in another process had just moved in, so where processes
  /// share a file, only a writer that holds the lock keeping their writes
  /// apart puts backups back.
  Future<_ReadResult> read(
    File file,
    int maximumBytes, {
    bool restore = true,
  }) async {
    var source = file;
    FileSystemEntityType type;
    try {
      type = await FileSystemEntity.type(file.path, followLinks: false);
      if (type == FileSystemEntityType.notFound && restore) {
        switch (await _restoreBackup(file)) {
          case _Restore.held:
            // What the backup or the file holds is unread, not missing.
            return const _Unreadable();
          case _Restore.restored:
            type = await FileSystemEntity.type(file.path, followLinks: false);
          case _Restore.none:
            break;
        }
      } else if (type == FileSystemEntityType.notFound) {
        final backups = await _backupsNewestFirst(file);
        if (backups == null) return const _Unreadable();
        if (backups.isNotEmpty) {
          source = backups.first;
          type = FileSystemEntityType.file;
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
      if (await source.length() > maximumBytes) return const _Damaged();
      final bytes = await source.readAsBytes();
      return bytes.length > maximumBytes ? const _Damaged() : _Read(bytes);
    } on FileSystemException {
      return const _Unreadable();
    }
  }

  /// The `<name>.<token>.bak` files of [file], which writes stopped between
  /// their two moves left behind, newest first. Only regular files count,
  /// never a link or a folder. Null while [file] may be there but cannot be
  /// looked at; throws when the folder is there but cannot be listed, since
  /// it may hold one.
  ///
  /// A folder that can be listed but not searched, as `chmod 444` leaves
  /// one, lists every name in it while each file looks missing. So the file
  /// listed under its own name, or a backup listed with nothing to look at,
  /// is unread, not gone.
  Future<List<File>?> _backupsNewestFirst(File file) async {
    final fileName = p.basename(file.path);
    final backups = <({File file, DateTime modified, int writtenAt})>[];
    try {
      await for (final entity in Directory(
        directory,
      ).list(followLinks: false)) {
        final name = p.basename(entity.path);
        if (name == fileName) return null;
        final writtenAt = _writtenAt(name, fileName, '.bak');
        if (entity is File && writtenAt != null) {
          final stat = await entity.stat();
          if (stat.type == FileSystemEntityType.notFound) return null;
          backups.add((
            file: entity,
            modified: stat.modified,
            writtenAt: writtenAt,
          ));
        }
      }
    } on FileSystemException catch (error) {
      // No folder holds no backup.
      if (_namesNoFolder(error)) return const [];
      rethrow;
    }
    // Newest first. A file system may keep whole seconds only, as Windows
    // does here, so between two backups of one second the later write's
    // wins: it moved aside the later version.
    backups.sort((left, right) {
      final byTime = right.modified.compareTo(left.modified);
      return byTime != 0 ? byTime : right.writtenAt.compareTo(left.writtenAt);
    });
    return [for (final backup in backups) backup.file];
  }

  /// Puts back the newest backup of [file] ([_backupsNewestFirst]) and drops
  /// older ones. A backup that is still there but cannot be moved just now,
  /// as when another program holds it, is [_Restore.held]: an older one
  /// never stands in for it, and nothing is dropped. So is [file] when it
  /// may be there but cannot be looked at.
  Future<_Restore> _restoreBackup(File file) async {
    final backups = await _backupsNewestFirst(file);
    if (backups == null) return _Restore.held;
    for (final backup in backups) {
      try {
        await backup.rename(file.path);
      } on FileSystemException {
        // One that vanished meanwhile is passed over; one still there is
        // held, and only it has the latest contents.
        if (await _exists(backup)) return _Restore.held;
        continue;
      }
      for (final stale in backups) {
        if (!identical(stale, backup)) await _deleteEntity(stale.path);
      }
      return _Restore.restored;
    }
    return _Restore.none;
  }

  /// Whether a damaged copy of `<stem>.json` that this store set aside is
  /// beside it, of any kind: a file, a link or a folder. Throws when the
  /// folder is there but cannot be listed.
  Future<bool> hasDamagedCopy(String stem) async {
    try {
      await for (final entity in Directory(
        directory,
      ).list(followLinks: false)) {
        if (_isDamagedCopy(p.basename(entity.path), stem)) return true;
      }
    } on FileSystemException catch (error) {
      if (_namesNoFolder(error)) return false;
      rethrow;
    }
    return false;
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
    final target = await _damagedCopyOf(file);
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
    await _pruneDamaged(p.basenameWithoutExtension(file.path));
    return true;
  }

  /// Writes [bytes], what was read from the damaged [file], to a damaged
  /// copy beside it and keeps the newest [MultiplayerStore.keptDamagedCopies];
  /// false when the copy could not be written. [file] stays where it is, so
  /// until what replaces it is written, a write that fails or stops part way
  /// leaves it there to be salvaged again.
  Future<bool> keepCopy(File file, List<int> bytes) async {
    final target = await _damagedCopyOf(file);
    try {
      await target.writeAsBytes(bytes, flush: true);
    } on FileSystemException {
      await _deleteEntity(target.path);
      return false;
    }
    await _pruneDamaged(p.basenameWithoutExtension(file.path));
    return true;
  }

  /// A free name for a damaged copy of [file]:
  /// `<stem>.damaged-<UTC time>.json`, or `-<attempt>` after the time.
  Future<File> _damagedCopyOf(File file) async {
    final stem = p.basenameWithoutExtension(file.path);
    final stamp = _clock().toUtc().toIso8601String().replaceAll(
      RegExp(r'[^0-9TZ]'),
      '',
    );
    var target = File(p.join(directory, '$stem.damaged-$stamp.json'));
    for (var attempt = 2; await _exists(target) && attempt < 100; attempt++) {
      target = File(p.join(directory, '$stem.damaged-$stamp-$attempt.json'));
    }
    return target;
  }

  /// The time, and any attempt, in the name of a damaged copy this store
  /// made.
  static final _damagedStamp = RegExp(r'^[0-9]+T[0-9]+Z(-[0-9]+)?$');

  /// Whether [name] is one this store gives a damaged copy of `<stem>.json`.
  static bool _isDamagedCopy(String name, String stem) {
    final prefix = '$stem.damaged-';
    return name.length > prefix.length + '.json'.length &&
        name.startsWith(prefix) &&
        name.endsWith('.json') &&
        _damagedStamp.hasMatch(
          name.substring(prefix.length, name.length - '.json'.length),
        );
  }

  /// Drops all but the newest [MultiplayerStore.keptDamagedCopies] damaged
  /// copies of `<stem>.json`. Only names this store gives them count, so a
  /// copy the player keeps beside them under another name is never dropped.
  Future<void> _pruneDamaged(String stem) async {
    try {
      final copies = <String>[];
      await for (final entity in Directory(
        directory,
      ).list(followLinks: false)) {
        if (_isDamagedCopy(p.basename(entity.path), stem)) {
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

  /// The newest backup could not be moved just now, or the file itself is
  /// there although it could not be looked at.
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
