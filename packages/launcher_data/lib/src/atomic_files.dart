/// What a save the launcher or its developer tools make leaves behind when it
/// stops part way, and how a file is put back from it.
///
/// A save writes the new bytes to `<name>.<token>.tmp` beside the file and
/// moves that over the file in one step. Where the file cannot be replaced in
/// one step, as on Windows while another program reads it, the save first
/// moves the file aside as `<name>.<token>.bak`, moves the new one in, then
/// deletes the backup. The token is `<process id>-<microseconds>`, the clock
/// reading when the save began ([newAtomicSaveToken]). A save stopped part way
/// leaves its `.tmp`, or its `.bak` and no file.
///
/// Only names of exactly that form count ([atomicLeftoverWrittenAt]). A copy
/// the player keeps beside a file, such as `<name>.bak`, `<name>.tmp` or a
/// Windows "- Copy", is never put back, swept or deleted.
library;

import 'dart:io';

import 'package:path/path.dart' as p;

import 'windows_file_move.dart';

/// Whether a file saved through this naming rule is in place.
enum AtomicFileState {
  /// The file is there, or the newest backup of it was put back.
  present,

  /// Neither the file nor a backup a save made of it is there.
  missing,
}

/// A file the launcher saves could not be read just now. Nothing may then be
/// written over it, no older backup may stand in for it, and nothing may be
/// deleted. The message names neither the file's path nor its content.
final class LauncherFileUnreadable extends FileSystemException {
  LauncherFileUnreadable.heldBackup(String label)
    : closedFolder = false,
      super(
        '$label could not be read just now, so nothing was changed. A save '
        'of it was interrupted, and the backup that save left could not be '
        'put back: another program may be using it, or its folder may not '
        'allow changes. Close that program or restore access, then try again.',
      );

  LauncherFileUnreadable.closedFolder(String label)
    : closedFolder = true,
      super(
        '$label could not be read just now, so nothing was changed. The '
        'folder that holds it cannot be listed or searched. Restore access to '
        'that folder, then try again.',
      );

  /// Whether the folder could not be listed or searched, rather than a
  /// backup in it held.
  final bool closedFolder;

  @override
  String toString() => message;
}

/// A token for a save that begins now: `<process id>-<microseconds>`.
String newAtomicSaveToken() => '$pid-${DateTime.now().microsecondsSinceEpoch}';

/// A token a save made; its second part is the clock reading, in
/// microseconds, when that save began.
final _token = RegExp(r'^[0-9]+-(-?[0-9]+)$');

/// The clock reading in [entry] when it names a leftover that a save of the
/// file [name] made, `<name>.<token><suffix>` with [suffix] `.bak` or `.tmp`;
/// null for any other name.
int? atomicLeftoverWrittenAt(String entry, String name, String suffix) {
  final prefix = '$name.';
  if (entry.length <= prefix.length + suffix.length ||
      !entry.startsWith(prefix) ||
      !entry.endsWith(suffix)) {
    return null;
  }
  final match = _token.firstMatch(
    entry.substring(prefix.length, entry.length - suffix.length),
  );
  return match == null ? null : int.tryParse(match.group(1)!);
}

/// [recoverAtomicBackupIfMissingSync], for callers that await.
Future<AtomicFileState> recoverAtomicBackupIfMissing(
  File file, {
  required String label,
}) async => recoverAtomicBackupIfMissingSync(file, label: label);

/// Puts [file] back in place before it is read or replaced, from the newest
/// backup that an interrupted save left, and says whether it is there.
///
/// The newest backup by modification time wins. Windows reports those in
/// whole seconds here, so between backups of one second the later save's
/// token wins: that save moved the later version aside. Once one is back,
/// the older ones are deleted.
///
/// With [readLegacyBackup], a `<name>.bak` counts too, the one name the
/// developer tools gave every backup before they shared this rule, but only
/// when nothing else is there to put back and a temporary file of an
/// interrupted save, `<name>.<token>.tmp`, lies beside it. Such a save always
/// leaves one, since it was stopped before it moved that file in; a
/// `<name>.bak` the player keeps has none. It is put back as the old saves'
/// own recovery did.
///
/// Throws [LauncherFileUnreadable] while the file may be there but cannot be
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
AtomicFileState recoverAtomicBackupIfMissingSync(
  File file, {
  required String label,
  bool readLegacyBackup = false,
}) {
  if (_isThere(file)) return AtomicFileState.present;
  final _Leftovers? leftovers;
  try {
    leftovers = _leftoversOf(file, readLegacyBackup: readLegacyBackup);
  } on _ClosedFolder {
    if (_isThere(file)) return AtomicFileState.present;
    throw LauncherFileUnreadable.closedFolder(label);
  }
  if (leftovers == null) return AtomicFileState.missing;
  final backups = leftovers.backups
    ..sort((left, right) {
      final byTime = right.modified.compareTo(left.modified);
      if (byTime != 0) return byTime;
      final byWrite = right.writtenAt.compareTo(left.writtenAt);
      return byWrite != 0 ? byWrite : right.file.path.compareTo(left.file.path);
    });
  final legacy = leftovers.legacyBackup;
  if (backups.isEmpty && legacy != null && leftovers.hasTemporary) {
    backups.add(_AtomicBackup(legacy, DateTime(0), 0));
  }
  for (final backup in backups) {
    if (_isThere(file)) return AtomicFileState.present;
    if (!_putBack(backup.file, file)) {
      // A file that came back meanwhile is read as it is, and a backup that
      // vanished meanwhile is passed over. One still there holds the latest
      // version, and nothing older may stand in for it.
      if (_isThere(file)) return AtomicFileState.present;
      if (_isThere(backup.file)) {
        throw LauncherFileUnreadable.heldBackup(label);
      }
      continue;
    }
    for (final stale in backups) {
      if (stale != backup) _deleteFileBestEffort(stale.file);
    }
    return AtomicFileState.present;
  }
  return AtomicFileState.missing;
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
bool _putBack(File backup, File file) {
  if (Platform.isWindows) {
    return moveFileWithoutReplacingOnWindows(backup.path, file.path);
  }
  try {
    backup.renameSync(file.path);
    return true;
  } on FileSystemException {
    return false;
  }
}

bool _isThere(File file) =>
    FileSystemEntity.typeSync(file.path, followLinks: false) !=
    FileSystemEntityType.notFound;

void _deleteFileBestEffort(File file) {
  try {
    if (FileSystemEntity.typeSync(file.path, followLinks: false) ==
        FileSystemEntityType.file) {
      file.deleteSync();
    }
  } on FileSystemException {
    // A stale backup is harmless, and the file is already back.
  }
}

/// What interrupted saves left beside [file]; null when there is no folder
/// there at all. A `<name>.bak` is looked at only with [readLegacyBackup].
/// Throws [_ClosedFolder] when the folder cannot be listed, or lists [file]
/// or a backup but cannot show it.
_Leftovers? _leftoversOf(File file, {required bool readLegacyBackup}) {
  final name = p.basename(file.path);
  final backups = <_AtomicBackup>[];
  File? legacyBackup;
  var hasTemporary = false;
  try {
    for (final entity in Directory(
      p.dirname(file.path),
    ).listSync(followLinks: false)) {
      final entry = p.basename(entity.path);
      // Listed under its own name, yet missing to look at: the folder lists
      // its names but cannot be searched.
      if (entry == name) throw const _ClosedFolder();
      if (entity is! File) continue;
      if (atomicLeftoverWrittenAt(entry, name, '.tmp') != null) {
        hasTemporary = true;
        continue;
      }
      final legacy = readLegacyBackup && entry == '$name.bak';
      final writtenAt = legacy
          ? 0
          : atomicLeftoverWrittenAt(entry, name, '.bak');
      if (writtenAt == null) continue;
      final stat = entity.statSync();
      if (stat.type == FileSystemEntityType.notFound) {
        throw const _ClosedFolder();
      }
      if (legacy) {
        legacyBackup = entity;
      } else {
        backups.add(_AtomicBackup(entity, stat.modified, writtenAt));
      }
    }
  } on FileSystemException catch (error) {
    if (_namesNoFolder(error)) return null;
    throw const _ClosedFolder();
  }
  return _Leftovers(backups, legacyBackup, hasTemporary);
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

final class _Leftovers {
  _Leftovers(this.backups, this.legacyBackup, this.hasTemporary);

  final List<_AtomicBackup> backups;

  /// A regular file named `<name>.bak`.
  final File? legacyBackup;

  /// Whether a temporary file a save of this file made lies beside it.
  final bool hasTemporary;
}

final class _AtomicBackup {
  _AtomicBackup(this.file, this.modified, this.writtenAt);

  final File file;
  final DateTime modified;
  final int writtenAt;
}
