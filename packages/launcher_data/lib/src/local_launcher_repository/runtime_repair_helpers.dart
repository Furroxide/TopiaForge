part of '../local_launcher_repository.dart';

extension LocalLauncherRuntimeRepair on LocalLauncherRepository {
  Future<_PreparedRuntime> _prepareRuntimeForLaunch(GameInstall install) async {
    final current = await _validateGameDirectory(install.path);
    final blockingIssues = current.issues.where((issue) => issue.isBlocking);
    if (blockingIssues.isNotEmpty) {
      return _PreparedRuntime.failed(
        LaunchResult(
          started: false,
          message: blockingIssues.map((issue) => issue.message).join(' '),
        ),
      );
    }

    if (!current.needsRepair) {
      return _PreparedRuntime.ready(current);
    }

    final report = await _installOrRepairRuntime(current);
    final refreshed = await _validateGameDirectory(current.path);
    if (report.ok && !refreshed.needsRepair && refreshed.canLaunch) {
      return _PreparedRuntime.ready(refreshed);
    }

    final messages = [
      ...report.issues
          .where((issue) => issue.isBlocking)
          .map((issue) => issue.message),
      ...refreshed.issues
          .where((issue) => issue.isBlocking)
          .map((issue) => issue.message),
    ];
    if (messages.isEmpty && refreshed.needsRepair) {
      messages.add('Runtime files are still missing or stale after repair.');
    }

    return _PreparedRuntime.failed(
      LaunchResult(
        started: false,
        message: [
          'Automatic runtime repair could not complete.',
          ...messages,
        ].join(' '),
      ),
    );
  }

  Future<RepairReport> _installOrRepairRuntime(GameInstall install) async {
    final actions = <String>[];
    final issues = <LauncherIssue>[];
    final layout = GameLayout.resolve(install.path);
    if (layout == null || !File(layout.executablePath).existsSync()) {
      return RepairReport(
        actions: actions,
        issues: const [
          LauncherIssue(
            severity: IssueSeverity.error,
            message:
                'The Robotopia game was not found. Select the game folder first.',
          ),
        ],
      );
    }

    try {
      await _withRuntimeRepairLock(Directory(layout.gameRoot), () async {
        final transaction = await _RuntimeRepairTransaction.begin(
          Directory(layout.gameRoot),
        );
        var completed = false;
        try {
          await _stageBepInEx(layout, transaction, issues);
          await _stageLoader(install, transaction, issues);
          if (issues.any((issue) => issue.isBlocking)) {
            return;
          }
          await transaction.prepare();
          await transaction.commit(hook: _runtimeRepairCommitHook);
          await _restoreExecutableBits(layout);
          for (final directory in [
            _managerRoot(install),
            _packageInbox(install),
            _managerConfig(install),
            _managerData(install),
            _managerLogs(install),
          ]) {
            _ensureRuntimeDirectory(Directory(layout.gameRoot), directory);
          }
          await transaction.complete();
          completed = true;
          actions.add(
            'Installed or repaired BepInEx ${LocalLauncherRepository._bepInExVersion}.',
          );
          actions.add(
            'Installed or repaired TopiaForge loader ${LocalLauncherRepository._loaderVersion}.',
          );
          if (layout.kind == GameInstallLayout.linuxProton) {
            actions.add(
              'Reminder: run the game under Proton/Wine with '
              'WINEDLLOVERRIDES="winhttp=n,b" so the mod loader injects.',
            );
          }
        } finally {
          if (!completed && transaction.root.existsSync()) {
            await transaction.rollback();
          }
        }
      });
    } on FileSystemException catch (error) {
      issues.add(
        LauncherIssue(severity: IssueSeverity.error, message: error.message),
      );
    } on StateError catch (error) {
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          message: error.message.toString(),
        ),
      );
    }

    final refreshed = await _validateGameDirectory(install.path);
    issues.addAll(refreshed.issues.where((issue) => issue.isBlocking));
    await _appendLauncherLog('Repair actions: ${actions.join('; ')}');
    return RepairReport(actions: actions, issues: issues);
  }

  Future<void> _stageBepInEx(
    GameLayout layout,
    _RuntimeRepairTransaction transaction,
    List<LauncherIssue> issues,
  ) async {
    final source = Directory(
      p.join(
        _repositoryRoot.path,
        'third_party',
        'BepInEx',
        layout.bepInExBundleDirName,
      ),
    );
    if (FileSystemEntity.typeSync(source.path, followLinks: false) !=
        FileSystemEntityType.directory) {
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          message:
              'Bundled BepInEx ${LocalLauncherRepository._bepInExVersion} '
              '(${layout.bepInExBundleDirName}) was not found.',
        ),
      );
      return;
    }
    _requireRuntimeDirectory(
      _repositoryRoot,
      source,
      label: 'Bundled BepInEx source',
    );

    final entities = source.listSync(recursive: true, followLinks: false)
      ..sort((left, right) => left.path.compareTo(right.path));
    if (entities.length > _maxRuntimeSourceEntries) {
      throw StateError('Bundled BepInEx exceeds the runtime entry limit.');
    }
    for (final entity in entities) {
      final type = FileSystemEntity.typeSync(entity.path, followLinks: false);
      if (type == FileSystemEntityType.directory) {
        _requireRuntimeDirectory(
          source,
          Directory(entity.path),
          label: 'Bundled BepInEx source',
        );
        continue;
      }
      if (type != FileSystemEntityType.file) {
        throw StateError(
          'Bundled BepInEx contains a symbolic link or special file.',
        );
      }
      await transaction.addSource(
        File(entity.path),
        p.relative(entity.path, from: source.path),
      );
    }
  }

  /// Dart's copySync drops Unix permission bits, so re-mark the runtime
  /// files that must stay executable (macOS bundle only). No-op on hosts
  /// without chmod.
  Future<void> _restoreExecutableBits(GameLayout layout) async {
    if (layout.executableRuntimeFiles.isEmpty || Platform.isWindows) {
      return;
    }
    for (final relative in layout.executableRuntimeFiles) {
      final target = p.join(layout.gameRoot, relative);
      _requireRuntimeDirectory(
        Directory(layout.gameRoot),
        File(target).parent,
        label: 'Executable runtime path',
      );
      final targetType = FileSystemEntity.typeSync(target, followLinks: false);
      if (targetType == FileSystemEntityType.link) {
        throw StateError('Executable runtime path is a symbolic link: $target');
      }
      if (targetType == FileSystemEntityType.file) {
        final result = await runBoundedProcess(
          'chmod',
          ['+x', target],
          timeout: const Duration(seconds: 10),
          maxStdoutBytes: 64 * 1024,
          maxStderrBytes: 64 * 1024,
        );
        if (result.exitCode != 0) {
          throw StateError('Could not restore executable permission: $target');
        }
      }
    }
  }

  Future<void> _stageLoader(
    GameInstall install,
    _RuntimeRepairTransaction transaction,
    List<LauncherIssue> issues,
  ) async {
    final loaderSource = Directory(
      p.join(
        _repositoryRoot.path,
        'src',
        'TopiaForge.ModManager',
        'bin',
        'Release',
        'netstandard2.1',
      ),
    );
    final loaderDlls = [
      'TopiaForge.ModManager.dll',
      'TopiaForge.ModManager.Core.dll',
      'TopiaForge.Mods.Abstractions.dll',
      'TopiaForge.Mods.UnityUi.dll',
    ];
    if (FileSystemEntity.typeSync(loaderSource.path, followLinks: false) !=
            FileSystemEntityType.directory ||
        !loaderDlls.every(
          (dll) =>
              FileSystemEntity.typeSync(
                p.join(loaderSource.path, dll),
                followLinks: false,
              ) ==
              FileSystemEntityType.file,
        )) {
      issues.add(
        const LauncherIssue(
          severity: IssueSeverity.error,
          message:
              'Built loader DLLs were not found. Run dotnet build TopiaForge.slnx -c Release.',
        ),
      );
      return;
    }
    _requireRuntimeDirectory(
      _repositoryRoot,
      loaderSource,
      label: 'Built loader source',
    );

    for (final dll in loaderDlls) {
      final source = File(p.join(loaderSource.path, dll));
      await transaction.addSource(
        source,
        p.posix.join('BepInEx', 'plugins', 'TopiaForge.ModManager', dll),
      );
    }
  }

  Future<void> _openPath(String path) async {
    if (Platform.isWindows) {
      await Process.start('explorer.exe', [
        path,
      ], mode: ProcessStartMode.detached);
      return;
    }

    await Process.start(Platform.isMacOS ? 'open' : 'xdg-open', [
      path,
    ], mode: ProcessStartMode.detached);
  }
}

class _PreparedRuntime {
  const _PreparedRuntime.ready(this.install) : failure = null;
  const _PreparedRuntime.failed(this.failure) : install = null;

  final GameInstall? install;
  final LaunchResult? failure;
}
