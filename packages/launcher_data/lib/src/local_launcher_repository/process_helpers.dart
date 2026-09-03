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
            'TopiaForge runtime is missing or stale. Repair Runtime before launch.',
      );
    }

    final layout = GameLayout.resolve(refreshed.path);
    if (layout == null || !File(layout.executablePath).existsSync()) {
      return const LaunchResult(
        started: false,
        message: 'The Robotopia game was not found.',
      );
    }

    ProfileLaunchConfiguration configuration;
    try {
      configuration = ProfileLaunchConfiguration.fromProfile(profile);
    } on FormatException catch (error) {
      return LaunchResult(started: false, message: error.message.toString());
    }

    final selectionError = await _profileSelectionError(
      refreshed,
      configuration,
    );
    if (selectionError != null) {
      return LaunchResult(started: false, message: selectionError);
    }

    var executable = layout.executablePath;
    var arguments = profile.launchSettings.extraArguments;
    var logSuffix = '';
    if (layout.kind == GameInstallLayout.linuxProton) {
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
      executable = wineCommand;
      arguments = [
        layout.executablePath,
        ...profile.launchSettings.extraArguments,
      ];
      logSuffix = ' via configured Wine/Proton command';
    }

    final launchFile = await _writeProfileLaunchConfiguration(
      refreshed,
      configuration,
    );
    late final Map<String, String> environment;
    try {
      environment = _profileLaunchEnvironment(layout, profile, launchFile.path);
    } on Object catch (error) {
      await _deleteProfileLaunchConfiguration(launchFile);
      return LaunchResult(started: false, message: error.toString());
    }

    final int processId;
    try {
      processId = await _gameProcessStarter(
        GameProcessRequest(
          executable: executable,
          arguments: arguments,
          workingDirectory: layout.gameRoot,
          environment: environment,
        ),
      );
    } on Object catch (error) {
      await _deleteProfileLaunchConfiguration(launchFile);
      try {
        await _appendLauncherLogBestEffort(
          'Game process start failed (${error.runtimeType}).',
        );
      } on Object {
        // Launch failure is already represented by the returned result.
      }
      return const LaunchResult(
        started: false,
        message: 'TopiaForge could not be started. No mod state was changed.',
      );
    }

    try {
      await _appendLauncherLogBestEffort('$message$logSuffix pid=$processId');
    } on Object {
      // The detached process owns the one-shot file now; logging must not turn
      // a successful start into a failure or delete its launch configuration.
    }
    return LaunchResult(started: true, message: message, processId: processId);
  }

  Future<bool> _stopGameIfRunning(GameInstall install) async {
    if (!Platform.isWindows) {
      return _stopGameUnix(install);
    }

    final result = await runBoundedProcess('powershell.exe', [
      '-NoProfile',
      '-NonInteractive',
      '-ExecutionPolicy',
      'Bypass',
      '-Command',
      _stopTopiaForgeScript,
      install.executablePath,
    ], timeout: const Duration(seconds: 15));

    if (result.exitCode == 0) {
      await _appendLauncherLogBestEffort('Stopped TopiaForge before restart.');
      return true;
    }
    if (result.exitCode == 2) {
      await _appendLauncherLogBestEffort('No running Robotopia process found.');
      return false;
    }

    final detail = '${result.stdout}\n${result.stderr}'.trim();
    throw StateError(
      detail.isEmpty ? 'Unable to stop TopiaForge before restart.' : detail,
    );
  }

  /// Unix counterpart of the PowerShell stop script: find the game process,
  /// SIGTERM it, and wait up to five seconds for it to exit. Returns false
  /// when nothing was running, true when a process was stopped, and throws
  /// when a process refused to exit — the same contract as the Windows path.
  Future<bool> _stopGameUnix(GameInstall install) async {
    final layout = GameLayout.resolve(install.path);
    if (layout == null) {
      throw StateError('Unable to resolve the Robotopia executable to stop.');
    }

    Future<List<int>> matchingPids() =>
        findUnixGameProcessIds(layout.executablePath);

    final pids = await matchingPids();
    if (pids.isEmpty) {
      await _appendLauncherLogBestEffort('No running Robotopia process found.');
      return false;
    }

    final terminated = await runBoundedProcess(
      'kill',
      ['--', ...pids.map((processId) => '$processId')],
      timeout: const Duration(seconds: 5),
      maxStdoutBytes: 64 * 1024,
      maxStderrBytes: 64 * 1024,
    );
    if (terminated.exitCode != 0) {
      throw StateError('Unable to stop the matching Robotopia process.');
    }
    final deadline = DateTime.now().add(const Duration(seconds: 5));
    while (DateTime.now().isBefore(deadline)) {
      await Future<void>.delayed(const Duration(milliseconds: 200));
      if ((await matchingPids()).isEmpty) {
        await _appendLauncherLogBestEffort(
          'Stopped TopiaForge before restart.',
        );
        return true;
      }
    }
    throw StateError('TopiaForge did not exit before the restart timeout.');
  }
}

Future<int> _startDetachedGameProcess(GameProcessRequest request) async {
  final process = await Process.start(
    request.executable,
    request.arguments,
    workingDirectory: request.workingDirectory,
    environment: request.environment,
    mode: ProcessStartMode.detached,
  );
  return process.pid;
}

/// Exit code the probe script reserves for "no matching process", mirroring
/// the not-running code the stop script already uses.
const int _gameNotRunningExitCode = 2;

/// Read-only counterpart of the stop path: reports whether a Robotopia process
/// for this exact install is alive, without touching it.
///
/// Matching is by full executable path and never by basename, for the same
/// reason the stop path refuses a basename fallback: two installs may both
/// contain Robotopia.exe, and this must never answer for the other one.
///
/// Every failure path answers true. A probe that could not read the process
/// list must not be mistaken for one that looked and found nothing, or an
/// unrelated environment fault would silently clear a pending restart warning.
Future<bool> _defaultGameRunningProbe(GameInstall install) async {
  if (!Platform.isWindows) {
    final layout = GameLayout.resolve(install.path);
    if (layout == null) {
      return true;
    }
    try {
      return (await findUnixGameProcessIds(layout.executablePath)).isNotEmpty;
    } on Object {
      return true;
    }
  }

  try {
    final result = await runBoundedProcess('powershell.exe', [
      '-NoProfile',
      '-NonInteractive',
      '-ExecutionPolicy',
      'Bypass',
      '-Command',
      _probeTopiaForgeScript,
      install.executablePath,
    ], timeout: const Duration(seconds: 10));
    return result.exitCode != _gameNotRunningExitCode;
  } on Object {
    return true;
  }
}

const String _probeTopiaForgeScript = r'''
param([string]$TargetPath)

$target = [System.IO.Path]::GetFullPath($TargetPath)

$running = @(
  Get-CimInstance Win32_Process -Filter "Name = 'Robotopia.exe'" |
    Where-Object {
      $_.ExecutablePath -and
      ([System.IO.Path]::GetFullPath($_.ExecutablePath) -ieq $target)
    }
)

if ($running.Count -eq 0) { exit 2 }
exit 0
''';

const String _stopTopiaForgeScript = r'''
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
  Write-Error "TopiaForge did not exit before the restart timeout."
  exit 4
}

exit 0
''';
