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

    final executable = File(refreshed.executablePath);
    if (!executable.existsSync()) {
      return const LaunchResult(
        started: false,
        message: 'Robotopia.exe was not found.',
      );
    }

    final process = await Process.start(
      executable.path,
      profile.launchSettings.extraArguments,
      workingDirectory: install.path,
      mode: ProcessStartMode.detached,
    );
    await _appendLauncherLog('$message pid=${process.pid}');
    return LaunchResult(
      started: true,
      message: message,
      processId: process.pid,
    );
  }

  Future<bool> _stopGameIfRunning(GameInstall install) async {
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
