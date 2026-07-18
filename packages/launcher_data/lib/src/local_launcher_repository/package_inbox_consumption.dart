part of '../local_launcher_repository.dart';

Future<String?> _consumeInboxCandidate(_InboxCandidate candidate) async {
  final source = candidate.file;
  if (FileSystemEntity.typeSync(source.path, followLinks: false) !=
      FileSystemEntityType.file) {
    return 'The preflighted path is no longer a regular file.';
  }

  Directory? quarantine;
  File? moved;
  try {
    quarantine = Directory(
      p.dirname(source.path),
    ).createTempSync('.topiaforge-consume-');
    moved = await source.rename(p.join(quarantine.path, candidate.fileName));
  } on Object catch (error) {
    _deleteEmptyInboxQuarantine(quarantine);
    return 'The package could not be moved atomically out of the inbox '
        'pattern before consumption: $error';
  }

  if (FileSystemEntity.typeSync(moved.path, followLinks: false) !=
      FileSystemEntityType.file) {
    return 'The atomically moved package is not a regular file; it was '
        'retained in the inbox quarantine.';
  }
  try {
    final bytes = await _readLauncherFileBounded(moved, _maxPackageBytes);
    if (sha256.convert(bytes).toString() != candidate.sha256) {
      return 'The package bytes changed after preflight; replacement bytes '
          'were retained in the inbox quarantine.';
    }
  } on Object catch (error) {
    return 'The package could not be reverified after its atomic move and '
        'was retained in the inbox quarantine: $error';
  }

  try {
    await moved.delete();
    await quarantine.delete();
    return null;
  } on Object catch (error) {
    return 'The verified package could not be removed from the inbox '
        'quarantine and was retained: $error';
  }
}

void _deleteEmptyInboxQuarantine(Directory? directory) {
  if (directory == null) return;
  try {
    if (directory.existsSync()) directory.deleteSync();
  } on Object {
    // Best effort: an empty, non-package-pattern directory is harmless and
    // can be inspected or removed on the next maintenance pass.
  }
}
