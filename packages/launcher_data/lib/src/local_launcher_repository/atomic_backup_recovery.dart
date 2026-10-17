part of '../local_launcher_repository.dart';

/// Whether a file the launcher saves with [_writeFileBytesAtomic] is in place.
enum _AtomicFileState {
  /// The file is there, or the newest backup of it was put back.
  present,

  /// Neither the file nor a backup the launcher made of it is there.
  missing,
}

/// A file the launcher saves could not be read just now. Nothing may then be
/// written over it, no older backup may stand in for it, and nothing may be
/// deleted. The message names neither the file's path nor its content.
final class _LauncherFileUnreadable extends FileSystemException {
  _LauncherFileUnreadable.heldBackup(String label)
    : super(
        '$label could not be read just now, so nothing was changed. A save '
        'of it was interrupted, and the backup that save left could not be '
        'put back: another program may be using it, or its folder may not '
        'allow changes. Close that program or restore access, then try again.',
      );

  _LauncherFileUnreadable.closedFolder(String label)
    : super(
        '$label could not be read just now, so nothing was changed. The '
        'folder that holds it cannot be listed or searched. Restore access to '
        'that folder, then try again.',
      );

  @override
  String toString() => message;
}

/// Puts [file] back in place before it is read or replaced, from the newest
/// backup that an interrupted [_writeFileBytesAtomic] left, and says whether
/// it is there.
///
/// That write moves the file aside as `<name>.<process id>-<microseconds>.bak`
/// before it moves the new one in, so a write stopped between its two moves
/// leaves that backup and no file. Only names of that form count: a copy the
/// player keeps, such as `<name>.bak` or a Windows "- Copy", is never put back
/// or deleted. The newest backup by modification time wins. Windows reports
/// those in whole seconds here, so between backups of one second the later
/// write's token wins: that write moved the later version aside. Once one is
/// back, the older ones are deleted.
///
/// Throws [_LauncherFileUnreadable] while the file may be there but cannot be
/// read: the newest backup cannot be moved, as when another program holds
/// it, or the folder cannot be listed, or it lists names it cannot search,
/// as `chmod 444` leaves one. Nothing is then moved or deleted, and no older
/// backup stands in. Only a missing path, or a file where the folder or one
/// of its parents should be, means there is no folder and so no backup.
///
/// The desktop launcher and the command line share these files, so the file
/// can come back while this looks, when a save in the other process ends or
/// the other process puts the same backup back. A file that is there is read
/// as it is, and no backup is moved over it: see [_putBack].
Future<_AtomicFileState> _recoverAtomicBackupIfMissing(
  File file, {
  required String label,
}) async {
  if (_isThere(file)) return _AtomicFileState.present;
  final List<_AtomicBackup>? backups;
  try {
    backups = await _atomicBackupsOf(file);
  } on _ClosedFolder {
    if (_isThere(file)) return _AtomicFileState.present;
    throw _LauncherFileUnreadable.closedFolder(label);
  }
  if (backups == null) return _AtomicFileState.missing;
  backups.sort((left, right) {
    final byTime = right.modified.compareTo(left.modified);
    if (byTime != 0) return byTime;
    final byWrite = right.writtenAt.compareTo(left.writtenAt);
    return byWrite != 0 ? byWrite : right.file.path.compareTo(left.file.path);
  });
  for (final backup in backups) {
    if (_isThere(file)) return _AtomicFileState.present;
    if (!await _putBack(backup.file, file)) {
      // A file that came back meanwhile is read as it is, and a backup that
      // vanished meanwhile is passed over. One still there holds the latest
      // version, and nothing older may stand in for it.
      if (_isThere(file)) return _AtomicFileState.present;
      if (_isThere(backup.file)) {
        throw _LauncherFileUnreadable.heldBackup(label);
      }
      continue;
    }
    for (final stale in backups) {
      if (stale != backup) await _deleteFileBestEffort(stale.file);
    }
    return _AtomicFileState.present;
  }
  return _AtomicFileState.missing;
}

/// Moves [backup] to [file]'s name, and says whether it moved.
///
/// On Windows a save moves the file aside whenever it cannot replace it in
/// one move, as while another program reads it, so another process can find
/// the file missing and that save's backup beside it just before the save
/// moves the new file in. There the move refuses, in the same step, to
/// replace a file that came back. `rename(2)` cannot refuse, but there a save
/// never leaves the file missing: its first move replaces the file in one
/// step, and whatever stops that move stops the move aside as well.
Future<bool> _putBack(File backup, File file) async {
  if (Platform.isWindows) {
    return moveFileWithoutReplacingOnWindows(backup.path, file.path);
  }
  try {
    await backup.rename(file.path);
    return true;
  } on FileSystemException {
    return false;
  }
}

bool _isThere(File file) =>
    FileSystemEntity.typeSync(file.path, followLinks: false) !=
    FileSystemEntityType.notFound;

/// The backups the launcher made of [file] beside it, in no order; null when
/// there is no folder there at all. Throws [_ClosedFolder] when the folder
/// cannot be listed, or lists [file] or one of its backups but cannot show
/// it.
Future<List<_AtomicBackup>?> _atomicBackupsOf(File file) async {
  final name = p.basename(file.path);
  final backups = <_AtomicBackup>[];
  try {
    await for (final entity in Directory(
      p.dirname(file.path),
    ).list(followLinks: false)) {
      final entry = p.basename(entity.path);
      // Listed under its own name, yet missing to look at: the folder lists
      // its names but cannot be searched.
      if (entry == name) throw const _ClosedFolder();
      final writtenAt = _atomicBackupWrittenAt(entry, name);
      if (writtenAt == null || entity is! File) continue;
      final stat = entity.statSync();
      if (stat.type == FileSystemEntityType.notFound) {
        throw const _ClosedFolder();
      }
      backups.add(_AtomicBackup(entity, stat.modified, writtenAt));
    }
  } on FileSystemException catch (error) {
    if (_namesNoFolder(error)) return null;
    throw const _ClosedFolder();
  }
  return backups;
}

/// A folder that cannot be listed, or that lists names it cannot show.
final class _ClosedFolder implements Exception {
  const _ClosedFolder();
}

/// Whether listing failed because there is no folder there at all: the path
/// is missing, or it or a parent is a file (ENOTDIR on Linux and macOS,
/// ERROR_DIRECTORY on Windows).
bool _namesNoFolder(FileSystemException error) =>
    error is PathNotFoundException ||
    error.osError?.errorCode == (Platform.isWindows ? 267 : 20);

/// The token of a backup that [_writeFileBytesAtomic] names
/// `<process id>-<microseconds>`; the microseconds are the clock reading when
/// that write began.
final _atomicBackupToken = RegExp(r'^[0-9]+-(-?[0-9]+)$');

/// The clock reading in [entry] when it names a backup the launcher made of
/// the file [name], `<name>.<token>.bak`; null for any other name.
int? _atomicBackupWrittenAt(String entry, String name) {
  final prefix = '$name.';
  const suffix = '.bak';
  if (entry.length <= prefix.length + suffix.length ||
      !entry.startsWith(prefix) ||
      !entry.endsWith(suffix)) {
    return null;
  }
  final match = _atomicBackupToken.firstMatch(
    entry.substring(prefix.length, entry.length - suffix.length),
  );
  return match == null ? null : int.tryParse(match.group(1)!);
}

final class _AtomicBackup {
  _AtomicBackup(this.file, this.modified, this.writtenAt);

  final File file;
  final DateTime modified;
  final int writtenAt;
}
