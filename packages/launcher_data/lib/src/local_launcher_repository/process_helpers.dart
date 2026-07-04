part of '../local_launcher_repository.dart';

extension _ProcessHelpers on LocalLauncherRepository {
  Future<LaunchResult> _startGame(
    GameInstall install,
    LauncherProfile profile, {
    required String message,
  }) async {
    final refreshed = await _validateGameDirectory(install.path);
    if (refreshed.needsRepair) {
      return const LaunchResult(
        started: false,
        message:
            'Robotopia runtime is missing or stale. Repair Runtime before launch.',
      );
    }

    if (profile.launchSettings.safeMode) {
      await disableAllMods(refreshed);
    }

    final layout = GameLayout.resolve(refreshed.path);
    if (layout == null || !File(layout.executablePath).existsSync()) {
      return const LaunchResult(
        started: false,
        message: 'The Robotopia game was not found.',
      );
    }

    if (layout.kind == GameInstallLayout.linuxProton) {
      return _startGameProton(layout, profile, message: message);
    }

    final process = await Process.start(
      layout.executablePath,
      profile.launchSettings.extraArguments,
      workingDirectory: layout.gameRoot,
      // On macOS this injects the doorstop DYLD variables; empty elsewhere.
      environment: layout.launchEnvironment(),
      mode: ProcessStartMode.detached,
    );
    await _appendLauncherLog('$message pid=${process.pid}');
    return LaunchResult(
      started: true,
      message: message,
      processId: process.pid,
    );
  }

  /// Proton/Wine installs: the launcher cannot guess the right Proton prefix
  /// or runtime, so by default it defers to the user's own game launcher. A
  /// `wineCommand` launcher setting (e.g. `wine` or a proton wrapper script)
  /// opts into direct launching.
  Future<LaunchResult> _startGameProton(
    GameLayout layout,
    LauncherProfile profile, {
    required String message,
  }) async {
    final settings = await _loadSettings();
    final wineCommand = (settings['wineCommand'] as String?)?.trim() ?? '';
    if (wineCommand.isEmpty) {
      return const LaunchResult(
        started: false,
        message:
            'Mods are installed. Launch Robotopia through your usual '
            'launcher (Tomato Cake/Steam/Proton) with '
            'WINEDLLOVERRIDES="winhttp=n,b" so the mod loader injects. '
            'Alternatively set "wineCommand" in the launcher settings to '
            'launch directly.',
      );
    }

    final process = await Process.start(
      wineCommand,
      [layout.executablePath, ...profile.launchSettings.extraArguments],
      workingDirectory: layout.gameRoot,
      environment: layout.launchEnvironment(),
      mode: ProcessStartMode.detached,
    );
    await _appendLauncherLog('$message (via $wineCommand) pid=${process.pid}');
    return LaunchResult(
      started: true,
      message: message,
      processId: process.pid,
    );
  }

  Future<bool> _stopGameIfRunning(GameInstall install) async {
    if (!Platform.isWindows) {
      return _stopGameUnix(install);
    }

    final result = await Process.run('powershell.exe', [
      '-NoProfile',
      '-NonInteractive',
      '-ExecutionPolicy',
      'Bypass',
      '-Command',
      _stopRobotopiaScript,
      install.executablePath,
    ]);

    if (result.exitCode == 0) {
      await _appendLauncherLog('Stopped Robotopia before restart.');
      return true;
    }
    if (result.exitCode == 2) {
      await _appendLauncherLog('No running Robotopia process found.');
      return false;
    }

    final detail = '${result.stdout}\n${result.stderr}'.trim();
    throw StateError(
      detail.isEmpty ? 'Unable to stop Robotopia before restart.' : detail,
    );
  }

  /// Unix counterpart of the PowerShell stop script: find the game process,
  /// SIGTERM it, and wait up to five seconds for it to exit. Returns false
  /// when nothing was running, true when a process was stopped, and throws
  /// when a process refused to exit — the same contract as the Windows path.
  Future<bool> _stopGameUnix(GameInstall install) async {
    final layout = GameLayout.resolve(install.path);
    // Wine reports the exe with a Windows-style command line, so match the
    // basename there; on macOS the full bundle-executable path is unambiguous.
    final pattern = layout?.kind == GameInstallLayout.macAppBundle
        ? layout!.executablePath
        : 'Robotopia.exe';

    Future<List<String>> matchingPids() async {
      final result = await Process.run('pgrep', ['-f', pattern]);
      if (result.exitCode != 0) {
        return const [];
      }
      return (result.stdout as String)
          .split('\n')
          .map((line) => line.trim())
          .where((line) => line.isNotEmpty)
          .toList();
    }

    final pids = await matchingPids();
    if (pids.isEmpty) {
      await _appendLauncherLog('No running Robotopia process found.');
      return false;
    }

    await Process.run('kill', pids);
    final deadline = DateTime.now().add(const Duration(seconds: 5));
    while (DateTime.now().isBefore(deadline)) {
      await Future<void>.delayed(const Duration(milliseconds: 200));
      if ((await matchingPids()).isEmpty) {
        await _appendLauncherLog('Stopped Robotopia before restart.');
        return true;
      }
    }
    throw StateError('Robotopia did not exit before the restart timeout.');
  }
}

const String _stopRobotopiaScript = r'''
param([string]$TargetPath)

$target = [System.IO.Path]::GetFullPath($TargetPath)
$terminated = 0

function Get-MatchingProcess {
  Get-CimInstance Win32_Process -Filter "Name = 'Robotopia.exe'" |
    Where-Object {
      $_.ExecutablePath -and
      ([System.IO.Path]::GetFullPath($_.ExecutablePath) -ieq $target)
    }
}

$matches = @(Get-MatchingProcess)
foreach ($process in $matches) {
  $result = Invoke-CimMethod -InputObject $process -MethodName Terminate
  if ($result.ReturnValue -ne 0) {
    Write-Error "Terminate failed for PID $($process.ProcessId)."
    exit 3
  }
  $terminated += 1
}

if ($terminated -eq 0) {
  exit 2
}

$deadline = (Get-Date).AddSeconds(5)
do {
  Start-Sleep -Milliseconds 200
  $remaining = @(Get-MatchingProcess)
} while ($remaining.Count -gt 0 -and (Get-Date) -lt $deadline)

if ($remaining.Count -gt 0) {
  Write-Error "Robotopia did not exit before the restart timeout."
  exit 4
}

exit 0
''';
