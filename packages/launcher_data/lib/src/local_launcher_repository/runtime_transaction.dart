part of '../local_launcher_repository.dart';

const _runtimeTransactionName = '.topiaforge-runtime-transaction';
const _runtimeRepairLockName = '.topiaforge-runtime-repair.lock';
const _maxRuntimeTransactionJournalBytes = 4 * 1024 * 1024;

Future<T> _withRuntimeRepairLock<T>(
  Directory gameRoot,
  Future<T> Function() action,
) async {
  _requireRuntimeDirectory(gameRoot, gameRoot, label: 'Game directory');
  final lock = File(p.join(gameRoot.path, _runtimeRepairLockName));
  final type = FileSystemEntity.typeSync(lock.path, followLinks: false);
  if (type == FileSystemEntityType.link) {
    throw StateError('Runtime repair lock cannot be a symbolic link.');
  }
  if (type != FileSystemEntityType.notFound &&
      type != FileSystemEntityType.file) {
    throw StateError('Runtime repair lock is not a regular file.');
  }
  final handle = await lock.open(mode: FileMode.append);
  try {
    await handle.lock(FileLock.exclusive);
    final lockedType = FileSystemEntity.typeSync(lock.path, followLinks: false);
    if (lockedType != FileSystemEntityType.file) {
      throw StateError('Runtime repair lock changed while it was opened.');
    }
    return await action();
  } finally {
    try {
      await handle.unlock();
    } on FileSystemException {
      // Closing the handle also releases the process-owned lock.
    }
    await handle.close();
  }
}

class _RuntimeRepairTransaction {
  _RuntimeRepairTransaction._(this.gameRoot, this.root);

  final Directory gameRoot;
  final Directory root;
  final List<_RuntimeFileOperation> operations = [];
  final Set<String> _collisionKeys = {};
  int _stagedBytes = 0;
  String status = 'staging';

  Directory get _staging => Directory(p.join(root.path, 'staging'));
  Directory get _backups => Directory(p.join(root.path, 'backups'));
  File get _journal => File(p.join(root.path, 'journal.json'));

  static Future<void> recoverIfNeeded(Directory gameRoot) async {
    final root = Directory(p.join(gameRoot.path, _runtimeTransactionName));
    final type = FileSystemEntity.typeSync(root.path, followLinks: false);
    if (type == FileSystemEntityType.notFound) {
      return;
    }
    if (type != FileSystemEntityType.directory) {
      throw StateError(
        'Runtime repair transaction path is not a regular directory.',
      );
    }
    _requireRuntimeDirectory(gameRoot, root, label: 'Runtime transaction');
    final transaction = _RuntimeRepairTransaction._(gameRoot, root);
    final journalType = FileSystemEntity.typeSync(
      transaction._journal.path,
      followLinks: false,
    );
    if (journalType == FileSystemEntityType.notFound) {
      transaction._deleteRoot();
      return;
    }
    if (journalType != FileSystemEntityType.file) {
      throw StateError('Runtime repair journal is not a regular file.');
    }
    final decoded = jsonDecode(
      utf8.decode(
        await _readLauncherFileBounded(
          transaction._journal,
          _maxRuntimeTransactionJournalBytes,
        ),
      ),
    );
    if (decoded is! Map<String, Object?> || decoded['formatVersion'] != 2) {
      throw StateError('Runtime repair journal is invalid.');
    }
    transaction.status = decoded['status'] as String? ?? '';
    final rawOperations = decoded['operations'];
    if (rawOperations is! List ||
        rawOperations.length > _maxRuntimeSourceEntries) {
      throw StateError('Runtime repair journal operations are invalid.');
    }
    for (var index = 0; index < rawOperations.length; index += 1) {
      final raw = rawOperations[index];
      if (raw is! Map) {
        throw StateError('Runtime repair journal operation is invalid.');
      }
      final operation = _RuntimeFileOperation.fromJson(raw, index: index);
      transaction._register(operation);
    }
    if (transaction.status == 'complete') {
      transaction._deleteRoot();
      return;
    }
    if (transaction.status != 'prepared' &&
        transaction.status != 'committing') {
      throw StateError('Runtime repair journal has an invalid state.');
    }
    await transaction.rollback();
  }

  static Future<_RuntimeRepairTransaction> begin(Directory gameRoot) async {
    await recoverIfNeeded(gameRoot);
    final root = Directory(p.join(gameRoot.path, _runtimeTransactionName));
    root.createSync();
    _requireRuntimeDirectory(gameRoot, root, label: 'Runtime transaction');
    final transaction = _RuntimeRepairTransaction._(gameRoot, root);
    transaction._staging.createSync();
    transaction._backups.createSync();
    return transaction;
  }

  Future<void> addSource(File source, String relativePath) async {
    final relative = portableArchivePath(relativePath, label: 'Runtime source');
    final operation = _RuntimeFileOperation(relative, operations.length);
    _register(operation);
    final beforeType = FileSystemEntity.typeSync(
      source.path,
      followLinks: false,
    );
    if (beforeType != FileSystemEntityType.file) {
      throw StateError('Runtime source is not a regular file: ${source.path}');
    }
    final before = source.statSync();
    if (before.size < 0 ||
        before.size > _maxRuntimeSourceFileBytes ||
        _stagedBytes > _maxRuntimeSourceBytes - before.size) {
      throw StateError('Runtime sources exceed their staging size limit.');
    }
    final bytes = await _readLauncherFileBounded(
      source,
      _maxRuntimeSourceFileBytes,
    );
    final afterType = FileSystemEntity.typeSync(
      source.path,
      followLinks: false,
    );
    final after = source.statSync();
    if (afterType != FileSystemEntityType.file ||
        before.size != after.size ||
        before.modified != after.modified) {
      throw StateError('Runtime source changed while it was being staged.');
    }
    _stagedBytes += bytes.length;
    final staged = operation.stagedFile(root);
    _ensureRuntimeDirectory(root, staged.parent);
    await staged.writeAsBytes(bytes, flush: true);
  }

  void _register(_RuntimeFileOperation operation) {
    if (operations.length >= _maxRuntimeSourceEntries) {
      throw StateError('Runtime source exceeds its entry limit.');
    }
    final firstSegment = p.posix.split(operation.relativePath).first;
    final firstKey = portableArchiveCollisionKey(
      firstSegment,
      label: 'Runtime source',
    );
    if (firstKey ==
            portableArchiveCollisionKey(
              _runtimeTransactionName,
              label: 'Runtime source',
            ) ||
        firstKey ==
            portableArchiveCollisionKey(
              _runtimeRepairLockName,
              label: 'Runtime source',
            )) {
      throw StateError('Runtime source targets a reserved repair path.');
    }
    final key = portableArchiveCollisionKey(
      operation.relativePath,
      label: 'Runtime source',
    );
    if (!_collisionKeys.add(key)) {
      throw StateError(
        'Runtime source contains a duplicate path: ${operation.relativePath}',
      );
    }
    operations.add(operation);
  }

  Future<void> prepare() async {
    status = 'prepared';
    await _writeJournal();
  }

  Future<void> commit({RuntimeRepairCommitHook? hook}) async {
    status = 'committing';
    await _writeJournal();
    var committed = 0;
    for (final operation in operations) {
      final target = operation.targetFile(gameRoot);
      _ensureRuntimeDirectory(gameRoot, target.parent);
      final targetType = FileSystemEntity.typeSync(
        target.path,
        followLinks: false,
      );
      if (targetType != FileSystemEntityType.notFound &&
          targetType != FileSystemEntityType.file) {
        throw StateError(
          'Runtime destination is not a regular file: ${target.path}',
        );
      }
      operation.hadOriginal = targetType == FileSystemEntityType.file;
      operation.phase = 'backingUp';
      await _writeJournal();
      if (operation.hadOriginal) {
        await target.rename(operation.backupFile(root).path);
      }
      operation.phase = 'backedUp';
      await _writeJournal();
      final staged = operation.stagedFile(root);
      if (FileSystemEntity.typeSync(staged.path, followLinks: false) !=
          FileSystemEntityType.file) {
        throw StateError('Staged runtime file is missing: ${staged.path}');
      }
      if (FileSystemEntity.typeSync(target.path, followLinks: false) !=
          FileSystemEntityType.notFound) {
        throw StateError('Runtime destination changed during repair.');
      }
      await staged.rename(target.path);
      operation.phase = 'installed';
      await _writeJournal();
      committed += 1;
      await hook?.call(committed);
    }
  }

  Future<void> complete() async {
    status = 'complete';
    await _writeJournal();
    _deleteRoot();
  }

  Future<void> rollback() async {
    Object? firstFailure;
    for (final operation in operations.reversed) {
      try {
        _rollbackOperation(operation);
      } on Object catch (error) {
        firstFailure ??= error;
      }
    }
    if (firstFailure == null) {
      _deleteRoot();
      return;
    }
    throw StateError('Runtime repair rollback failed: $firstFailure');
  }

  void _rollbackOperation(_RuntimeFileOperation operation) {
    if (operation.phase == 'pending') {
      return;
    }
    final target = operation.targetFile(gameRoot);
    final backup = operation.backupFile(root);
    final backupType = FileSystemEntity.typeSync(
      backup.path,
      followLinks: false,
    );
    final targetType = FileSystemEntity.typeSync(
      target.path,
      followLinks: false,
    );
    if (backupType == FileSystemEntityType.file) {
      if (targetType == FileSystemEntityType.file) {
        target.deleteSync();
      } else if (targetType != FileSystemEntityType.notFound) {
        throw StateError(
          'Cannot restore unsafe runtime target: ${target.path}',
        );
      }
      _ensureRuntimeDirectory(gameRoot, target.parent);
      backup.renameSync(target.path);
      return;
    }
    if (backupType != FileSystemEntityType.notFound) {
      throw StateError('Runtime repair backup is not a regular file.');
    }
    if (operation.hadOriginal) {
      if (targetType != FileSystemEntityType.file) {
        throw StateError(
          'Runtime repair lost an original file: ${target.path}',
        );
      }
      return;
    }
    if ((operation.phase == 'backedUp' || operation.phase == 'installed') &&
        targetType == FileSystemEntityType.file) {
      target.deleteSync();
    } else if (targetType != FileSystemEntityType.notFound) {
      throw StateError('Cannot remove unsafe runtime target: ${target.path}');
    }
  }

  Future<void> _writeJournal() => _writeJsonFileAtomic(
    _journal,
    {
      'formatVersion': 2,
      'status': status,
      'operations': operations.map((item) => item.toJson()).toList(),
    },
    maxBytes: _maxRuntimeTransactionJournalBytes,
    label: 'Runtime repair journal',
  );

  void _deleteRoot() {
    final type = FileSystemEntity.typeSync(root.path, followLinks: false);
    if (type == FileSystemEntityType.notFound) {
      return;
    }
    if (type != FileSystemEntityType.directory) {
      throw StateError('Runtime transaction path is unsafe.');
    }
    root.deleteSync(recursive: true);
  }
}

class _RuntimeFileOperation {
  _RuntimeFileOperation(this.relativePath, this.index);

  factory _RuntimeFileOperation.fromJson(Map raw, {required int index}) {
    final relative = raw['relativePath'];
    final phase = raw['phase'];
    final hadOriginal = raw['hadOriginal'];
    if (relative is! String ||
        phase is! String ||
        hadOriginal is! bool ||
        !const {
          'pending',
          'backingUp',
          'backedUp',
          'installed',
        }.contains(phase)) {
      throw StateError('Runtime repair journal operation is invalid.');
    }
    final normalized = portableArchivePath(relative, label: 'Runtime journal');
    final result = _RuntimeFileOperation(normalized, index);
    result.phase = phase;
    result.hadOriginal = hadOriginal;
    return result;
  }

  final String relativePath;
  final int index;
  String phase = 'pending';
  bool hadOriginal = false;

  File stagedFile(Directory transactionRoot) => File(
    p.joinAll([
      transactionRoot.path,
      'staging',
      ...p.posix.split(relativePath),
    ]),
  );

  File backupFile(Directory transactionRoot) =>
      File(p.join(transactionRoot.path, 'backups', '$index.bak'));

  File targetFile(Directory gameRoot) =>
      File(p.joinAll([gameRoot.path, ...p.posix.split(relativePath)]));

  Map<String, Object?> toJson() => {
    'relativePath': relativePath,
    'phase': phase,
    'hadOriginal': hadOriginal,
  };
}
