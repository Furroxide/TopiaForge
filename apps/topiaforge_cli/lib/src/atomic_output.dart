import 'dart:io';

import 'package:path/path.dart' as p;

Directory createAtomicStagingDirectory(String destinationPath) {
  final destination = Directory(destinationPath).absolute;
  destination.parent.createSync(recursive: true);
  final staging = Directory(
    p.join(
      destination.parent.path,
      '.${p.basename(destination.path)}.staging-$pid-'
      '${DateTime.now().microsecondsSinceEpoch}',
    ),
  );
  staging.createSync();
  return staging;
}

void publishAtomicDirectory(Directory staging, String destinationPath) {
  final destination = Directory(destinationPath).absolute;
  final destinationType = FileSystemEntity.typeSync(
    destination.path,
    followLinks: false,
  );
  if (destinationType != FileSystemEntityType.notFound &&
      destinationType != FileSystemEntityType.directory) {
    throw StateError(
      'Refusing to replace non-directory output path: ${destination.path}',
    );
  }
  final backup = Directory('${destination.path}.backup-$pid');
  if (backup.existsSync()) {
    throw StateError('Atomic output backup already exists: ${backup.path}');
  }
  var movedExisting = false;
  try {
    if (destination.existsSync()) {
      destination.renameSync(backup.path);
      movedExisting = true;
    }
    staging.renameSync(destination.path);
    if (movedExisting) backup.deleteSync(recursive: true);
  } on Object {
    if (!destination.existsSync() && movedExisting && backup.existsSync()) {
      backup.renameSync(destination.path);
    }
    rethrow;
  }
}

void deleteAtomicStagingDirectory(Directory staging) {
  if (staging.existsSync()) staging.deleteSync(recursive: true);
}
