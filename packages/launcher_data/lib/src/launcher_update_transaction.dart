import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:math';

import 'package:path/path.dart' as p;

part 'launcher_update_transaction_journal.dart';

typedef LauncherUpdateTransitionHook =
    FutureOr<void> Function(String phase, LauncherUpdateTransactionPlan plan);
typedef LauncherUpdateProcessLauncher =
    Future<int> Function(
      LauncherUpdateTransactionPlan plan, {
      required bool healthHandshake,
    });
typedef LauncherUpdateProcessExists = Future<bool> Function(int processId);
typedef LauncherUpdateProcessKiller = bool Function(int processId);

final class LauncherUpdateInterruption implements Exception {
  const LauncherUpdateInterruption(this.message);

  final String message;

  @override
  String toString() => message;
}

final class LauncherUpdateTransactionPlan {
  const LauncherUpdateTransactionPlan({
    required this.transactionId,
    required this.platformId,
    required this.targetRoot,
    required this.stagedRoot,
    required this.backupRoot,
    required this.failedRoot,
    required this.launcherRelativePath,
    required this.launcherPid,
    required this.healthNonce,
    required this.healthFile,
    required this.journalFile,
    required this.healthTimeoutSeconds,
  });

  factory LauncherUpdateTransactionPlan.fromJson(Map<String, Object?> json) {
    if (json['formatVersion'] != 1) {
      throw const FormatException('Update transaction format is invalid.');
    }
    final plan = LauncherUpdateTransactionPlan(
      transactionId: json['transactionId'] as String? ?? '',
      platformId: json['platformId'] as String? ?? '',
      targetRoot: json['targetRoot'] as String? ?? '',
      stagedRoot: json['stagedRoot'] as String? ?? '',
      backupRoot: json['backupRoot'] as String? ?? '',
      failedRoot: json['failedRoot'] as String? ?? '',
      launcherRelativePath: json['launcherRelativePath'] as String? ?? '',
      launcherPid: (json['launcherPid'] as num?)?.toInt() ?? 0,
      healthNonce: json['healthNonce'] as String? ?? '',
      healthFile: json['healthFile'] as String? ?? '',
      journalFile: json['journalFile'] as String? ?? '',
      healthTimeoutSeconds:
          (json['healthTimeoutSeconds'] as num?)?.toInt() ?? 0,
    );
    plan.validate();
    return plan;
  }

  final String transactionId;
  final String platformId;
  final String targetRoot;
  final String stagedRoot;
  final String backupRoot;
  final String failedRoot;
  final String launcherRelativePath;
  final int launcherPid;
  final String healthNonce;
  final String healthFile;
  final String journalFile;
  final int healthTimeoutSeconds;

  String get launcherPath => p.join(targetRoot, launcherRelativePath);

  Map<String, Object?> toJson() => {
    'formatVersion': 1,
    'transactionId': transactionId,
    'platformId': platformId,
    'targetRoot': targetRoot,
    'stagedRoot': stagedRoot,
    'backupRoot': backupRoot,
    'failedRoot': failedRoot,
    'launcherRelativePath': launcherRelativePath,
    'launcherPid': launcherPid,
    'healthNonce': healthNonce,
    'healthFile': healthFile,
    'journalFile': journalFile,
    'healthTimeoutSeconds': healthTimeoutSeconds,
  };

  void validate() {
    if (!RegExp(r'^[0-9a-f]{32}$').hasMatch(transactionId) ||
        !RegExp(r'^[0-9a-f]{64}$').hasMatch(healthNonce) ||
        !const {
          'windows-x64',
          'linux-x64',
          'macos-universal',
        }.contains(platformId) ||
        launcherPid <= 0 ||
        healthTimeoutSeconds < 5 ||
        healthTimeoutSeconds > 300) {
      throw const FormatException('Update transaction identity is invalid.');
    }
    final paths = [
      targetRoot,
      stagedRoot,
      backupRoot,
      failedRoot,
      healthFile,
      journalFile,
    ].map((value) => p.normalize(p.absolute(value))).toList();
    if (paths.toSet().length != paths.length ||
        paths.any((path) => path == p.rootPrefix(path))) {
      throw const FormatException('Update transaction paths are unsafe.');
    }
    final targetParent = p.dirname(paths[0]);
    if (!p.isWithin(targetParent, paths[1]) ||
        p.dirname(paths[2]) != targetParent ||
        p.dirname(paths[3]) != targetParent ||
        p.rootPrefix(paths[0]).toLowerCase() !=
            p.rootPrefix(paths[1]).toLowerCase()) {
      throw const FormatException(
        'Update staging and rollback paths must share the install volume.',
      );
    }
    if (p.isAbsolute(launcherRelativePath) ||
        p.split(p.normalize(launcherRelativePath)).contains('..') ||
        launcherRelativePath.trim().isEmpty) {
      throw const FormatException('Update launcher path is invalid.');
    }
    final transactionRoot = p.dirname(paths[5]);
    if (!p.isWithin(transactionRoot, paths[4])) {
      throw const FormatException(
        'Update health marker must stay inside the transaction.',
      );
    }
  }

  static LauncherUpdateTransactionPlan read(File file) {
    if (FileSystemEntity.typeSync(file.path, followLinks: false) !=
        FileSystemEntityType.file) {
      throw StateError('Update transaction plan is not a regular file.');
    }
    if (file.lengthSync() > 64 * 1024) {
      throw StateError('Update transaction plan is oversized.');
    }
    final decoded = jsonDecode(file.readAsStringSync());
    if (decoded is! Map) {
      throw const FormatException('Update transaction plan must be an object.');
    }
    return LauncherUpdateTransactionPlan.fromJson(
      Map<String, Object?>.from(decoded),
    );
  }

  void write(File file) => _writeJsonAtomic(file, toJson());
}

final class LauncherUpdateTransactionHelper {
  const LauncherUpdateTransactionHelper({
    this.beforeTransitionHook,
    this.transitionHook,
    this.processLauncher,
    this.processExists = _processExists,
    this.processKiller = Process.killPid,
    this.pollInterval = const Duration(milliseconds: 250),
  });

  final LauncherUpdateTransitionHook? beforeTransitionHook;
  final LauncherUpdateTransitionHook? transitionHook;
  final LauncherUpdateProcessLauncher? processLauncher;
  final LauncherUpdateProcessExists processExists;
  final LauncherUpdateProcessKiller processKiller;
  final Duration pollInterval;

  Future<void> apply(String planPath) async {
    final planFile = File(p.normalize(p.absolute(planPath)));
    final plan = LauncherUpdateTransactionPlan.read(planFile);
    if (p.dirname(plan.journalFile) != planFile.parent.path) {
      throw StateError(
        'Update plan and journal must share a transaction root.',
      );
    }
    var journal = _UpdateJournal.read(plan);
    if (journal.phase == 'complete') return;
    try {
      if (journal.phase == 'planned' || journal.phase == 'waiting') {
        journal = await _transition(plan, journal, 'waiting');
        await _waitForExit(plan.launcherPid);
      }
      if (journal.phase == 'waiting') {
        journal = await _transition(plan, journal, 'backing-up');
      }
      if (journal.phase == 'backing-up') {
        _requireDirectory(plan.targetRoot, 'current installation');
        _requireDirectory(plan.stagedRoot, 'staged installation');
        if (FileSystemEntity.typeSync(plan.backupRoot, followLinks: false) !=
            FileSystemEntityType.notFound) {
          throw StateError('Update backup path already exists.');
        }
        Directory(plan.targetRoot).renameSync(plan.backupRoot);
        journal = await _transition(plan, journal, 'backup-created');
      }
      if (journal.phase == 'backup-created') {
        journal = await _transition(plan, journal, 'installing');
      }
      if (journal.phase == 'installing') {
        Directory(plan.stagedRoot).renameSync(plan.targetRoot);
        journal = await _transition(plan, journal, 'installed');
      }
      if (journal.phase == 'installed') {
        journal = await _transition(plan, journal, 'launching');
      }
      if (journal.phase == 'launching') {
        final launchedPid = await _launch(plan, healthHandshake: true);
        journal = await _transition(
          plan,
          journal,
          'relaunched',
          launchedPid: launchedPid,
        );
      }
      if (journal.phase == 'relaunched') {
        final healthy = await _waitForHealth(plan);
        if (!healthy) {
          throw TimeoutException('Updated launcher did not become healthy.');
        }
        journal = await _transition(plan, journal, 'healthy');
      }
      if (journal.phase == 'healthy') {
        await _commit(plan, journal);
      }
    } on LauncherUpdateInterruption {
      rethrow;
    } on Object catch (error) {
      journal = _UpdateJournal.read(plan);
      if (journal.phase == 'committing' || journal.phase == 'complete') {
        rethrow;
      }
      await _rollback(plan, journal, error);
      rethrow;
    }
  }

  Future<void> recover(String planPath) async {
    final plan = LauncherUpdateTransactionPlan.read(
      File(p.normalize(p.absolute(planPath))),
    );
    var journal = _UpdateJournal.read(plan);
    if (journal.phase == 'complete' || journal.phase == 'rolled-back') return;
    final healthReported = _healthMarkerMatches(
      plan.healthFile,
      plan.healthNonce,
    );
    if (journal.phase == 'healthy' ||
        journal.phase == 'committing' ||
        (healthReported &&
            const {
              'installed',
              'launching',
              'relaunched',
            }.contains(journal.phase))) {
      if (journal.phase != 'healthy' && journal.phase != 'committing') {
        journal = await _transition(plan, journal, 'healthy');
      }
      await _commit(plan, journal);
      return;
    }
    await _rollback(
      plan,
      journal,
      StateError('Recovering an interrupted launcher update.'),
      relaunch: false,
    );
  }

  Future<_UpdateJournal> _transition(
    LauncherUpdateTransactionPlan plan,
    _UpdateJournal current,
    String phase, {
    int? launchedPid,
  }) async {
    await beforeTransitionHook?.call(phase, plan);
    final next = _UpdateJournal(
      phase: phase,
      launchedPid: launchedPid ?? current.launchedPid,
      error: '',
    );
    next.write(plan);
    await transitionHook?.call(phase, plan);
    return next;
  }

  Future<void> _commit(
    LauncherUpdateTransactionPlan plan,
    _UpdateJournal journal,
  ) async {
    if (journal.phase != 'committing') {
      journal = await _transition(plan, journal, 'committing');
    }
    _deleteDirectory(plan.backupRoot);
    _deleteDirectory(plan.failedRoot);
    _deleteMacStagingContainer(plan);
    await _transition(plan, journal, 'complete');
  }

  Future<void> _rollback(
    LauncherUpdateTransactionPlan plan,
    _UpdateJournal journal,
    Object error, {
    bool relaunch = true,
  }) async {
    if (journal.launchedPid > 0) {
      processKiller(journal.launchedPid);
      await _waitForExit(
        journal.launchedPid,
        timeout: const Duration(seconds: 10),
      );
    }
    final targetType = FileSystemEntity.typeSync(
      plan.targetRoot,
      followLinks: false,
    );
    final backupType = FileSystemEntity.typeSync(
      plan.backupRoot,
      followLinks: false,
    );
    if (backupType == FileSystemEntityType.directory) {
      if (targetType == FileSystemEntityType.directory) {
        if (Directory(plan.failedRoot).existsSync()) {
          _deleteDirectory(plan.failedRoot);
        }
        Directory(plan.targetRoot).renameSync(plan.failedRoot);
      } else if (targetType != FileSystemEntityType.notFound) {
        throw StateError('Update target is not a recoverable directory.');
      }
      Directory(plan.backupRoot).renameSync(plan.targetRoot);
    } else if (backupType != FileSystemEntityType.notFound) {
      throw StateError('Update backup is not a recoverable directory.');
    } else if (targetType != FileSystemEntityType.directory) {
      throw StateError('Update target and backup are both unavailable.');
    }
    _deleteDirectory(plan.stagedRoot);
    _deleteMacStagingContainer(plan);
    final rolledBack = _UpdateJournal(
      phase: 'rolled-back',
      launchedPid: 0,
      error: error.toString(),
    )..write(plan);
    await transitionHook?.call(rolledBack.phase, plan);
    if (relaunch &&
        !await processExists(plan.launcherPid) &&
        FileSystemEntity.typeSync(plan.launcherPath, followLinks: false) ==
            FileSystemEntityType.file) {
      await _launch(plan, healthHandshake: false);
    }
  }

  Future<int> _launch(
    LauncherUpdateTransactionPlan plan, {
    required bool healthHandshake,
  }) async {
    final injected = processLauncher;
    if (injected != null) {
      return injected(plan, healthHandshake: healthHandshake);
    }
    final args = healthHandshake
        ? [
            '--topiaforge-update-health-nonce',
            plan.healthNonce,
            '--topiaforge-update-health-file',
            plan.healthFile,
          ]
        : const <String>[];
    final process = await Process.start(
      plan.launcherPath,
      args,
      workingDirectory: plan.targetRoot,
      mode: ProcessStartMode.detached,
    );
    return process.pid;
  }

  Future<bool> _waitForHealth(LauncherUpdateTransactionPlan plan) async {
    final deadline = DateTime.now().add(
      Duration(seconds: plan.healthTimeoutSeconds),
    );
    while (DateTime.now().isBefore(deadline)) {
      if (_healthMarkerMatches(plan.healthFile, plan.healthNonce)) return true;
      await Future<void>.delayed(pollInterval);
    }
    return false;
  }

  Future<void> _waitForExit(
    int processId, {
    Duration timeout = const Duration(minutes: 2),
  }) async {
    final deadline = DateTime.now().add(timeout);
    while (DateTime.now().isBefore(deadline)) {
      if (!await processExists(processId)) return;
      await Future<void>.delayed(pollInterval);
    }
    throw TimeoutException('Timed out waiting for launcher process to exit.');
  }
}
