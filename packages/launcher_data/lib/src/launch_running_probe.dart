import 'dart:convert';
import 'dart:io';
import 'package:path/path.dart' as p;
import 'bounded_process.dart';
import 'process_identity.dart';

typedef LaunchProbeRunner =
    Future<BoundedProcessResult> Function(
      List<String> arguments,
      Map<String, String> environment,
    );

/// True is running, false is verified absent, null is unavailable or ambiguous.
Future<bool?> probeLaunchProcessRunning(String executablePath) async {
  if (!p.isAbsolute(executablePath) || executablePath.contains('\u0000')) {
    return null;
  }
  if (Platform.isWindows) {
    return probeWindowsLaunchProcessRunning(executablePath);
  }
  if (Platform.isLinux) return probeLinuxLaunchProcessRunning(executablePath);
  if (!Platform.isMacOS) return null;
  try {
    final target = p.normalize(executablePath);
    final result = await runBoundedProcess(
      '/usr/bin/pgrep',
      ['-f', '^${escapePosixExtendedRegex(target)}([[:space:]]|\$)'],
      timeout: const Duration(seconds: 5),
      maxStdoutBytes: 65536,
      maxStderrBytes: 4096,
    );
    if (result.exitCode == 1 &&
        result.stdout.trim().isEmpty &&
        result.stderr.trim().isEmpty) {
      return false;
    }
    if (result.exitCode != 0 || result.stderr.trim().isNotEmpty) return null;
    final ids = result.stdout.trim().split(RegExp(r'\s+'));
    if (ids.any((id) => int.tryParse(id) == null)) return null;
    return ids.any((id) => int.parse(id) != pid);
  } on Object {
    return null;
  }
}

Future<bool?> probeWindowsLaunchProcessRunning(
  String executablePath, {
  LaunchProbeRunner? runProbe,
  String Function(String path)? resolveImage,
}) async {
  try {
    final bytes = windowsLaunchRunningProbeScript.codeUnits
        .expand((unit) => [unit & 255, unit >> 8])
        .toList();
    final arguments = [
      '-NoProfile',
      '-NonInteractive',
      '-ExecutionPolicy',
      'Bypass',
      '-EncodedCommand',
      base64Encode(bytes),
    ];
    final environment = {'TOPIAFORGE_PROCESS_PROBE_TARGET': executablePath};
    final result = runProbe == null
        ? await runBoundedProcess(
            '${Platform.environment['SystemRoot']}\\System32\\WindowsPowerShell\\v1.0\\powershell.exe',
            arguments,
            environment: environment,
            timeout: const Duration(seconds: 10),
            maxStdoutBytes: 4096,
            maxStderrBytes: 4096,
          )
        : await runProbe(arguments, environment);
    if (result.exitCode != 0 || result.stderr.trim().isNotEmpty) return null;
    final decoded = jsonDecode(result.stdout);
    if (decoded is! Map ||
        decoded.length != 2 ||
        decoded['schemaVersion'] is! int ||
        decoded['schemaVersion'] != 1 ||
        decoded['paths'] is! List) {
      return null;
    }
    final paths = decoded['paths'] as List;
    if (paths.length > 32768 ||
        paths.any(
          (path) =>
              path is! String ||
              path.trim().isEmpty ||
              path.contains('\u0000') ||
              !p.windows.isAbsolute(path),
        )) {
      return null;
    }
    if (paths.isEmpty) return false;
    final resolve =
        resolveImage ?? (path) => File(path).resolveSymbolicLinksSync();
    final target = p.windows.normalize(resolve(executablePath)).toLowerCase();
    for (final path in paths.cast<String>()) {
      if (p.windows.normalize(resolve(path)).toLowerCase() == target) {
        return true;
      }
    }
    return false;
  } on Object {
    return null;
  }
}

Future<bool?> probeLinuxLaunchProcessRunning(
  String executablePath, {
  Directory? procRoot,
  Future<String> Function(String path)? readLink,
}) async {
  final root = procRoot ?? Directory('/proc');
  final elapsed = Stopwatch()..start();
  try {
    if (!await root.exists()) return null;
    var count = 0;
    await for (final entity in root.list(followLinks: false)) {
      if (++count > 32768 || elapsed.elapsed >= const Duration(seconds: 10)) {
        return null;
      }
      final processId = int.tryParse(p.basename(entity.path));
      if (processId == null || processId == pid || processId <= 0) continue;
      if (entity is! Directory) return null;
      try {
        final bytes = <int>[];
        await for (final chunk in File(
          p.join(entity.path, 'cmdline'),
        ).openRead().timeout(const Duration(seconds: 1))) {
          if (chunk.length > 1024 * 1024 - bytes.length) return null;
          bytes.addAll(chunk);
        }
        final arguments = utf8.decode(bytes).split('\u0000');
        if (processArgumentsReferenceExecutable(arguments, executablePath)) {
          return true;
        }
        final link = readLink ?? (path) => Link(path).target();
        String image;
        try {
          image = await link(p.join(entity.path, 'exe'));
        } on Object {
          if (arguments.every((value) => value.isEmpty) &&
              await _linuxKernelProcess(entity)) {
            continue;
          }
          rethrow;
        }
        if (!p.isAbsolute(image)) return null;
        final target = _canonicalProbeImage(executablePath);
        if (_canonicalProbeImage(image) == target) return true;
        for (final argument in arguments.where((value) => value.isNotEmpty)) {
          final name = argument.split(RegExp(r'[/\\]')).last;
          if (name.toLowerCase() != p.basename(executablePath).toLowerCase()) {
            continue;
          }
          if (p.isAbsolute(argument)) {
            if (_canonicalProbeImage(argument) == target) return true;
          } else {
            // Wine drive mappings are not inferred from an arbitrary process.
            if (p.windows.isAbsolute(argument)) return null;
            final cwd = await link(p.join(entity.path, 'cwd'));
            if (!p.isAbsolute(cwd)) return null;
            if (_canonicalProbeImage(p.join(cwd, argument)) == target) {
              return true;
            }
          }
        }
      } on Object {
        // A verified disappeared PID is harmless; a present unreadable one is unknown.
        if (await entity.exists()) return null;
      }
    }
    return false;
  } on Object {
    return null;
  }
}

String _canonicalProbeImage(String path) {
  try {
    return File(path).resolveSymbolicLinksSync();
  } on FileSystemException {
    // A running image can be deleted; its native recorded path is still useful.
    return p.normalize(
      path.endsWith(' (deleted)') ? path.substring(0, path.length - 10) : path,
    );
  }
}

Future<bool> _linuxKernelProcess(Directory process) async {
  final bytes = <int>[];
  await for (final chunk in File(
    p.join(process.path, 'stat'),
  ).openRead().timeout(const Duration(seconds: 1))) {
    if (chunk.length > 65536 - bytes.length) return false;
    bytes.addAll(chunk);
  }
  final text = utf8.decode(bytes);
  final close = text.lastIndexOf(')');
  if (close < 1) return false;
  final fields = text.substring(close + 1).trim().split(RegExp(r'\s+'));
  if (fields.length < 7) return false;
  final flags = int.tryParse(fields[6]);
  return flags != null && flags & 0x00200000 != 0; // Linux PF_KTHREAD.
}

// A failed query or unreadable candidate never establishes absence. The target
// arrives as data in an environment value, never as interpolated PowerShell code.
const String windowsLaunchRunningProbeScript = r'''
param([string]$TargetPath = $env:TOPIAFORGE_PROCESS_PROBE_TARGET)
$ErrorActionPreference = 'Stop'
try {
  if ([string]::IsNullOrWhiteSpace($TargetPath) -or
      -not [System.IO.Path]::IsPathRooted($TargetPath)) { exit 3 }
  $target = [System.IO.Path]::GetFullPath($TargetPath)
  $name = [System.IO.Path]::GetFileName($target).Replace("'", "''")
  $candidates = @(Get-CimInstance Win32_Process -Filter "Name = '$name'" -ErrorAction Stop)
  if ($candidates.Count -gt 32768) { exit 3 }
  $paths = @()
  foreach ($candidate in $candidates) {
    $path = $candidate.ExecutablePath
    if ($path -isnot [string] -or [string]::IsNullOrWhiteSpace($path) -or
        -not [System.IO.Path]::IsPathRooted($path)) { exit 3 }
    $paths += [System.IO.Path]::GetFullPath($path)
  }
  [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
  [pscustomobject]@{ schemaVersion = 1; paths = @($paths) } | ConvertTo-Json -Compress
  exit 0
} catch { exit 3 }
''';
