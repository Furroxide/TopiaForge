part of '../local_launcher_repository.dart';

extension LocalLauncherRuntimeRepair on LocalLauncherRepository {
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
      await _repairBepInEx(layout, actions, issues);
      _repairLoader(install, actions, issues);
    } on FileSystemException catch (error) {
      issues.add(
        LauncherIssue(severity: IssueSeverity.error, message: error.message),
      );
    }

    final refreshed = await _validateGameDirectory(install.path);
    issues.addAll(refreshed.issues.where((issue) => issue.isBlocking));
    await _appendLauncherLog('Repair actions: ${actions.join('; ')}');
    return RepairReport(actions: actions, issues: issues);
  }

  Future<void> _repairBepInEx(
    GameLayout layout,
    List<String> actions,
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
    if (!source.existsSync()) {
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

    _copyRuntimeDirectory(source, Directory(layout.gameRoot));
    await _restoreExecutableBits(layout);
    actions.add(
      'Installed or repaired BepInEx ${LocalLauncherRepository._bepInExVersion}.',
    );
    if (layout.kind == GameInstallLayout.linuxProton) {
      actions.add(
        'Reminder: run the game under Proton/Wine with '
        'WINEDLLOVERRIDES="winhttp=n,b" so the mod loader injects.',
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
      if (File(target).existsSync()) {
        await Process.run('chmod', ['+x', target]);
      }
    }
  }

  void _repairLoader(
    GameInstall install,
    List<String> actions,
    List<LauncherIssue> issues,
  ) {
    final loaderSource = Directory(
      p.join(
        _repositoryRoot.path,
        'src',
        'Robotopia.ModManager',
        'bin',
        'Release',
        'netstandard2.1',
      ),
    );
    final loaderDlls = [
      'Robotopia.ModManager.dll',
      'Robotopia.ModManager.Core.dll',
      'Robotopia.Mods.Abstractions.dll',
      'Robotopia.Mods.UnityUi.dll',
    ];
    if (!loaderDlls.every(
      (dll) => File(p.join(loaderSource.path, dll)).existsSync(),
    )) {
      issues.add(
        const LauncherIssue(
          severity: IssueSeverity.error,
          message:
              'Built loader DLLs were not found. Run dotnet build RobotopiaModManager.slnx -c Release.',
        ),
      );
      return;
    }

    final pluginDir = Directory(
      p.join(install.path, 'BepInEx', 'plugins', 'RobotopiaModManager'),
    )..createSync(recursive: true);
    for (final dll in loaderDlls) {
      File(
        p.join(loaderSource.path, dll),
      ).copySync(p.join(pluginDir.path, dll));
    }
    _managerRoot(install).createSync(recursive: true);
    _packageInbox(install).createSync(recursive: true);
    _managerConfig(install).createSync(recursive: true);
    _managerData(install).createSync(recursive: true);
    _managerLogs(install).createSync(recursive: true);
    actions.add(
      'Installed or repaired Robotopia loader ${LocalLauncherRepository._loaderVersion}.',
    );
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
