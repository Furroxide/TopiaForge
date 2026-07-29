part of 'launcher_update_transaction.dart';

final class _UpdateJournal {
  const _UpdateJournal({
    required this.phase,
    required this.launchedPid,
    required this.error,
  });

  final String phase;
  final int launchedPid;
  final String error;

  static _UpdateJournal read(LauncherUpdateTransactionPlan plan) {
    const phases = {
      'planned',
      'waiting',
      'backing-up',
      'backup-created',
      'installing',
      'installed',
      'launching',
      'relaunched',
      'healthy',
      'committing',
      'complete',
      'rolled-back',
    };
    for (final path in [plan.journalFile, '${plan.journalFile}.previous']) {
      final file = File(path);
      if (!file.existsSync()) continue;
      try {
        final decoded = jsonDecode(file.readAsStringSync());
        final phase = decoded is Map ? decoded['phase'] as String? ?? '' : '';
        final launchedPid = decoded is Map
            ? (decoded['launchedPid'] as num?)?.toInt() ?? 0
            : -1;
        final error = decoded is Map ? decoded['error'] as String? ?? '' : '';
        if (decoded is Map &&
            decoded['formatVersion'] == 1 &&
            decoded['transactionId'] == plan.transactionId &&
            phases.contains(phase) &&
            launchedPid >= 0 &&
            error.length <= 8192) {
          return _UpdateJournal(
            phase: phase,
            launchedPid: launchedPid,
            error: error,
          );
        }
      } on Object {
        continue;
      }
    }
    return const _UpdateJournal(phase: 'planned', launchedPid: 0, error: '');
  }

  void write(LauncherUpdateTransactionPlan plan) {
    _writeJsonAtomic(File(plan.journalFile), {
      'formatVersion': 1,
      'transactionId': plan.transactionId,
      'phase': phase,
      'launchedPid': launchedPid,
      'error': error,
      'updatedAtUtc': DateTime.now().toUtc().toIso8601String(),
    });
  }
}

Future<bool> _processExists(int processId) async {
  if (Platform.isWindows) {
    final result = await Process.run('tasklist', [
      '/FI',
      'PID eq $processId',
      '/FO',
      'CSV',
      '/NH',
    ]);
    return result.exitCode == 0 &&
        result.stdout.toString().contains('"$processId"');
  }
  final result = await Process.run('/bin/kill', ['-0', '$processId']);
  return result.exitCode == 0;
}

bool _healthMarkerMatches(String path, String nonce) {
  final file = File(path);
  if (FileSystemEntity.typeSync(path, followLinks: false) !=
          FileSystemEntityType.file ||
      file.lengthSync() > 4096) {
    return false;
  }
  try {
    final decoded = jsonDecode(file.readAsStringSync());
    return decoded is Map &&
        decoded['formatVersion'] == 1 &&
        decoded['nonce'] == nonce &&
        decoded['healthy'] == true;
  } on Object {
    return false;
  }
}

void _deleteMacStagingContainer(LauncherUpdateTransactionPlan plan) {
  if (plan.platformId != 'macos-universal') return;
  final container = Directory(p.dirname(plan.stagedRoot));
  if (FileSystemEntity.typeSync(container.path, followLinks: false) !=
      FileSystemEntityType.directory) {
    return;
  }
  final shim = File(p.join(container.path, 'topiaforge'));
  final shimType = FileSystemEntity.typeSync(shim.path, followLinks: false);
  if (shimType == FileSystemEntityType.file) {
    shim.deleteSync();
  } else if (shimType != FileSystemEntityType.notFound) {
    throw StateError('The macOS update staging shim is unsafe.');
  }
  if (container.listSync(followLinks: false).isEmpty) {
    container.deleteSync();
  } else {
    throw StateError('The macOS update staging container is not empty.');
  }
}

void _requireDirectory(String path, String label) {
  if (FileSystemEntity.typeSync(path, followLinks: false) !=
      FileSystemEntityType.directory) {
    throw StateError('Update $label is missing or unsafe.');
  }
}

void _deleteDirectory(String path) {
  final type = FileSystemEntity.typeSync(path, followLinks: false);
  if (type == FileSystemEntityType.notFound) return;
  if (type != FileSystemEntityType.directory) {
    throw StateError('Refusing to remove a non-directory update path.');
  }
  Directory(path).deleteSync(recursive: true);
}

void _writeJsonAtomic(File file, Map<String, Object?> value) {
  file.parent.createSync(recursive: true);
  final random = Random.secure();
  final temporary = File('${file.path}.tmp-$pid-${random.nextInt(0x7fffffff)}');
  final previous = File('${file.path}.previous');
  temporary.writeAsStringSync(
    '${const JsonEncoder.withIndent('  ').convert(value)}\n',
    flush: true,
  );
  if (file.existsSync()) {
    if (previous.existsSync()) previous.deleteSync();
    file.renameSync(previous.path);
  }
  try {
    temporary.renameSync(file.path);
  } on Object {
    if (!file.existsSync() && previous.existsSync()) {
      previous.renameSync(file.path);
    }
    rethrow;
  } finally {
    if (temporary.existsSync()) temporary.deleteSync();
  }
}
