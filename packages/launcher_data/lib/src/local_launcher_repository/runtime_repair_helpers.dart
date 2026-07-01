part of '../local_launcher_repository.dart';

extension LocalLauncherRuntimeRepair on LocalLauncherRepository {
  Future<RepairReport> _installOrRepairRuntime(GameInstall install) async {
    final actions = <String>[];
    final issues = <LauncherIssue>[];
    if (!File(install.executablePath).existsSync()) {
      return RepairReport(
        actions: actions,
        issues: const [
          LauncherIssue(
            severity: IssueSeverity.error,
            message:
                'Robotopia.exe was not found. Select the game folder first.',
          ),
        ],
      );
    }

    try {
      _repairBepInEx(install, actions, issues);
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

  void _repairBepInEx(
    GameInstall install,
    List<String> actions,
    List<LauncherIssue> issues,
  ) {
    final source = Directory(
      p.join(
        _repositoryRoot.path,
        'third_party',
        'BepInEx',
        'win_x64_${LocalLauncherRepository._bepInExVersion}',
      ),
    );
    if (!source.existsSync()) {
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          message:
              'Bundled BepInEx ${LocalLauncherRepository._bepInExVersion} was not found.',
        ),
      );
      return;
    }

    _copyRuntimeDirectory(source, Directory(install.path));
    actions.add(
      'Installed or repaired BepInEx ${LocalLauncherRepository._bepInExVersion}.',
    );
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

    await Process.start('xdg-open', [path], mode: ProcessStartMode.detached);
  }
}
