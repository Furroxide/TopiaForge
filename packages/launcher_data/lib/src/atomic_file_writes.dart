/// Saves that a reader never sees half done, under the naming rule of
/// `atomic_files.dart`, and the sweep of what saves stopped part way left.
///
/// The launcher's files and the developer tools' files are saved the same
/// way, so one recovery ([recoverAtomicBackupIfMissingSync]) and one sweep
/// ([sweepAtomicLeftovers]) serve both.
library;

import 'dart:io';

import 'package:path/path.dart' as p;

import 'atomic_files.dart';

/// How long ago a save must have begun, and a leftover of it last changed,
/// before a sweep may delete that leftover: far longer than any save takes,
/// so a save still running in another process keeps its own.
const atomicLeftoverAge = Duration(hours: 1);

/// The temporary files saves in this isolate are writing, by canonical path.
/// No sweep deletes one of them, whatever its age.
final _inUse = <String>{};

/// Writes [contents] to [file] so that a reader sees the old file or the new
/// one, never a mix. Old leftovers of earlier saves of [file] are swept
/// first ([sweepAtomicLeftovers]).
///
/// The new bytes go to `<name>.<token>.tmp`, which then replaces the file in
/// one move. Only where that move fails while the file is there, as on
/// Windows while another program reads it, is the file moved aside as
/// `<name>.<token>.bak` first, and that backup deleted once the new file is
/// in. Refuses to replace a link or anything but a regular file.
Future<void> writeFileBytesAtomic(File file, List<int> contents) async {
  await file.parent.create(recursive: true);
  _requireReplaceable(file);
  sweepAtomicLeftovers(file);
  final token = newAtomicSaveToken();
  final temp = File('${file.path}.$token.tmp');
  final backup = File('${file.path}.$token.bak');
  final inUse = p.canonicalize(temp.path);
  _inUse.add(inUse);
  var committed = false;
  try {
    await temp.writeAsBytes(contents, flush: true);
    try {
      await temp.rename(file.path);
      committed = true;
      return;
    } on FileSystemException {
      if (!await file.exists()) rethrow;
    }

    await file.rename(backup.path);
    try {
      await temp.rename(file.path);
      committed = true;
    } on Object {
      if (!await file.exists() && await backup.exists()) {
        await backup.rename(file.path);
      }
      rethrow;
    }
  } finally {
    // Once the file has been replaced, cleanup must not make callers treat
    // the committed save as a failure. On Windows antivirus or file indexing
    // can briefly hold the old file.
    _deleteBestEffort(temp);
    if (committed) _deleteBestEffort(backup);
    _inUse.remove(inUse);
  }
}

/// [writeFileBytesAtomic], for callers that cannot await: the same moves in
/// the same order, under the same names.
void writeFileBytesAtomicSync(File file, List<int> contents) {
  file.parent.createSync(recursive: true);
  _requireReplaceable(file);
  sweepAtomicLeftovers(file);
  final token = newAtomicSaveToken();
  final temp = File('${file.path}.$token.tmp');
  final backup = File('${file.path}.$token.bak');
  final inUse = p.canonicalize(temp.path);
  _inUse.add(inUse);
  var committed = false;
  try {
    temp.writeAsBytesSync(contents, flush: true);
    try {
      temp.renameSync(file.path);
      committed = true;
      return;
    } on FileSystemException {
      if (!file.existsSync()) rethrow;
    }

    file.renameSync(backup.path);
    try {
      temp.renameSync(file.path);
      committed = true;
    } on Object {
      if (!file.existsSync() && backup.existsSync()) {
        backup.renameSync(file.path);
      }
      rethrow;
    }
  } finally {
    _deleteBestEffort(temp);
    if (committed) _deleteBestEffort(backup);
    _inUse.remove(inUse);
  }
}

/// Deletes what saves of [file] that stopped part way left beside it: its
/// temporary files, and with [backups] its backups too, which only a file
/// holding no player state may lose. A backup of a file that is missing is
/// put back instead ([recoverAtomicBackupIfMissingSync]), so call that first.
///
/// Only the names a save gives them count, `<name>.<token>.tmp` and
/// `<name>.<token>.bak`, and only regular files: a link is never followed or
/// deleted, nor a folder. A temporary file a save in this isolate is writing
/// is never deleted. Anything else is deleted only once the save that made it
/// began [age] ago, by its token, and it has not changed for as long, so a
/// save still running in another process keeps its own. Tidying only: a
/// leftover that cannot be deleted just now stays, and this never throws.
void sweepAtomicLeftovers(
  File file, {
  bool backups = false,
  Duration age = atomicLeftoverAge,
}) {
  final name = p.basename(file.path);
  final List<FileSystemEntity> entries;
  try {
    entries = Directory(p.dirname(file.path)).listSync(followLinks: false);
  } on FileSystemException {
    return;
  }
  final now = DateTime.now();
  for (final entity in entries) {
    final entry = p.basename(entity.path);
    final writtenAt =
        atomicLeftoverWrittenAt(entry, name, '.tmp') ??
        (backups ? atomicLeftoverWrittenAt(entry, name, '.bak') : null);
    if (writtenAt == null ||
        now.microsecondsSinceEpoch - writtenAt < age.inMicroseconds) {
      continue;
    }
    try {
      if (_inUse.contains(p.canonicalize(entity.path)) ||
          FileSystemEntity.typeSync(entity.path, followLinks: false) !=
              FileSystemEntityType.file) {
        continue;
      }
      final leftover = File(entity.path);
      if (now.difference(leftover.statSync().modified) < age) continue;
      leftover.deleteSync();
    } on FileSystemException {
      // Held by another program, or gone meanwhile: a later sweep tries again.
    }
  }
}

void _requireReplaceable(File file) {
  final type = FileSystemEntity.typeSync(file.path, followLinks: false);
  if (type == FileSystemEntityType.link) {
    throw StateError('Refusing to replace symbolic link: ${file.path}');
  }
  if (type != FileSystemEntityType.notFound &&
      type != FileSystemEntityType.file) {
    throw StateError('Expected a regular file: ${file.path}');
  }
}

/// Deletes [file] when it is a regular file; a leftover that cannot be
/// deleted is recoverable, or swept later, and never fails a save.
void _deleteBestEffort(File file) {
  try {
    if (FileSystemEntity.typeSync(file.path, followLinks: false) ==
        FileSystemEntityType.file) {
      file.deleteSync();
    }
  } on FileSystemException {
    // Swept or put back later.
  }
}
