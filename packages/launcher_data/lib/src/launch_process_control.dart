import 'dart:ffi';
import 'dart:io';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;

part 'launch_process_control_windows.dart';
part 'launch_process_creation_windows.dart';
part 'launch_process_creation_windows_abi.dart';
part 'launch_process_control_linux.dart';

/// Receipt captured from the process creation operation, never a later PID lookup.
final class LaunchProcessReceipt {
  const LaunchProcessReceipt({required this.pid, this.identity});
  final int pid;
  final LaunchProcessIdentity? identity;
}

/// On Windows, identity is captured from the original creation handle before
/// the suspended primary thread runs. Other platforms return an unowned PID.
Future<LaunchProcessReceipt> startLaunchProcessWithReceipt({
  required String executable,
  required List<String> arguments,
  required String workingDirectory,
  required Map<String, String> environment,
}) async {
  final args = List<String>.of(arguments);
  final overrides = Map<String, String>.of(environment);
  _validateCreationInputs(executable, args, workingDirectory, overrides);
  if (Platform.isWindows) {
    return _createWindowsProcess(executable, args, workingDirectory, overrides);
  }
  final process = await Process.start(
    executable,
    args,
    workingDirectory: workingDirectory,
    environment: overrides,
    mode: ProcessStartMode.detached,
  );
  return LaunchProcessReceipt(pid: process.pid);
}

void _validateCreationInputs(
  String executable,
  List<String> arguments,
  String directory,
  Map<String, String> environment,
) {
  bool validText(String value) => !value.contains('\u0000');
  if (!p.isAbsolute(executable) ||
      !validText(executable) ||
      executable.contains('"')) {
    throw ArgumentError('A fully qualified executable path is required.');
  }
  if (!p.isAbsolute(directory) || !validText(directory)) {
    throw ArgumentError('A fully qualified working directory is required.');
  }
  if (arguments.any((value) => !validText(value))) {
    throw ArgumentError('Process arguments cannot contain null characters.');
  }
  for (final entry in environment.entries) {
    if (entry.key.isEmpty ||
        entry.key.contains('=') ||
        !validText(entry.key) ||
        !validText(entry.value)) {
      throw ArgumentError('Invalid process environment entry.');
    }
  }
}

/// Reads a process through an OS handle; no basename or sibling-process fallback.
Future<LaunchProcessIdentity?> readLaunchProcessIdentity(
  int processId,
  String expectedExecutablePath,
) async {
  if (!_validProcessId(processId) ||
      _imagePath(expectedExecutablePath) == null) {
    return null;
  }
  _OwnedProcess? process;
  try {
    process = _openProcess(processId);
    final identity = await process.identity();
    return _sameImage(identity.executablePath, expectedExecutablePath)
        ? identity
        : null;
  } on Object {
    return null;
  } finally {
    process?.close();
  }
}

/// False means the recorded generation is gone; null means it could not be proved.
Future<bool?> isLaunchProcessAlive(LaunchProcessIdentity identity) async {
  if (!_validIdentity(identity)) return null;
  _OwnedProcess? process;
  try {
    process = _openProcess(identity.pid);
    return _sameIdentity(await process.identity(), identity);
  } on _ProcessAbsent {
    return false;
  } on Object {
    return null;
  } finally {
    process?.close();
  }
}

/// Verifies and stops one held process object, closing the PID-reuse race.
Future<bool> stopLaunchProcess(LaunchProcessIdentity identity) async {
  if (!_validIdentity(identity) || identity.pid == pid) {
    throw StateError(
      'A verified, separate process identity is required to restart.',
    );
  }
  _OwnedProcess? process;
  try {
    process = _openProcess(identity.pid, terminate: true);
    if (!_sameIdentity(await process.identity(), identity)) {
      throw StateError('The process identity changed; restart was refused.');
    }
    return await process.stop();
  } on _ProcessAbsent {
    return false;
  } finally {
    process?.close();
  }
}

bool _validProcessId(int value) => value > 0 && value <= 2147483647;
bool _validIdentity(LaunchProcessIdentity value) =>
    _validProcessId(value.pid) &&
    value.nativeStartToken.isNotEmpty &&
    value.startTimeUtc.isUtc &&
    _imagePath(value.executablePath) != null;
bool _sameIdentity(LaunchProcessIdentity a, LaunchProcessIdentity b) =>
    a.pid == b.pid &&
    a.nativeStartToken == b.nativeStartToken &&
    a.startTimeUtc == b.startTimeUtc &&
    _sameImage(a.executablePath, b.executablePath);
bool _sameImage(String a, String b) {
  final left = _imagePath(a);
  return left != null && left == _imagePath(b);
}

String? _imagePath(String value) {
  if (!p.isAbsolute(value) || value.contains(String.fromCharCode(0))) {
    return null;
  }
  var path = p.normalize(value);
  try {
    path = File(path).resolveSymbolicLinksSync();
  } on FileSystemException {
    // A loaded executable may have been removed. Its recorded path still compares exactly.
  }
  return Platform.isWindows ? path.toLowerCase() : path;
}

_OwnedProcess _openProcess(int processId, {bool terminate = false}) {
  if (Platform.isWindows) return _WindowsOwnedProcess(processId, terminate);
  if (Platform.isLinux) return _LinuxOwnedProcess(processId);
  throw StateError('Verified process control is unavailable on this platform.');
}

abstract interface class _OwnedProcess {
  Future<LaunchProcessIdentity> identity();
  Future<bool> stop();
  void close();
}

final class _ProcessAbsent implements Exception {}
