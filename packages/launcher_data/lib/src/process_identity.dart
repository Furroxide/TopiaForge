import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:path/path.dart' as p;

import 'bounded_process.dart';

const _maxProcessCommandLineBytes = 1024 * 1024;

/// Returns Unix process ids whose argument vector references [executablePath]
/// exactly. It deliberately has no basename fallback: two installs may both
/// contain Robotopia.exe, and restart must never terminate the other one.
Future<List<int>> findUnixGameProcessIds(String executablePath) async {
  if (Platform.isLinux) {
    return findLinuxGameProcessIds(
      executablePath,
      procRoot: Directory('/proc'),
    );
  }
  if (Platform.isMacOS) {
    return _findMacGameProcessIds(executablePath);
  }
  return const [];
}

Future<List<int>> findLinuxGameProcessIds(
  String executablePath, {
  required Directory procRoot,
}) async {
  if (!await procRoot.exists()) {
    return const [];
  }
  final matches = <int>[];
  await for (final entity in procRoot.list(followLinks: false)) {
    if (entity is! Directory) {
      continue;
    }
    final processId = int.tryParse(p.basename(entity.path));
    if (processId == null || processId == pid) {
      continue;
    }
    final commandLine = File(p.join(entity.path, 'cmdline'));
    try {
      final arguments = splitNullTerminatedArguments(
        await _readProcessCommandLine(commandLine),
      );
      if (processArgumentsReferenceExecutable(arguments, executablePath)) {
        matches.add(processId);
      }
    } on Object {
      // Processes routinely exit, or become unreadable, during enumeration.
    }
  }
  matches.sort();
  return matches;
}

List<String> splitNullTerminatedArguments(List<int> bytes) {
  final arguments = <String>[];
  var start = 0;
  for (var index = 0; index <= bytes.length; index++) {
    if (index != bytes.length && bytes[index] != 0) {
      continue;
    }
    if (index > start) {
      arguments.add(
        utf8.decode(bytes.sublist(start, index), allowMalformed: true),
      );
    }
    start = index + 1;
  }
  return arguments;
}

bool processArgumentsReferenceExecutable(
  List<String> arguments,
  String executablePath,
) {
  final target = _normalizedAbsolutePath(executablePath);
  if (target == null) {
    return false;
  }
  for (final argument in arguments) {
    final candidate = _normalizedAbsolutePath(argument);
    if (candidate != null && candidate == target) {
      return true;
    }
  }
  return false;
}

String? _normalizedAbsolutePath(String value) {
  if (value.isEmpty || !p.isAbsolute(value)) {
    return null;
  }
  return p.normalize(value);
}

Future<List<int>> _findMacGameProcessIds(String executablePath) async {
  final target = _normalizedAbsolutePath(executablePath);
  if (target == null) {
    return const [];
  }
  final pattern = '^${escapePosixExtendedRegex(target)}([[:space:]]|\$)';
  final result = await runBoundedProcess(
    'pgrep',
    ['-f', pattern],
    timeout: const Duration(seconds: 5),
    maxStdoutBytes: 1024 * 1024,
    maxStderrBytes: 64 * 1024,
  );
  if (result.exitCode != 0) {
    return const [];
  }
  final matches =
      result.stdout
          .split('\n')
          .map((line) => int.tryParse(line.trim()))
          .whereType<int>()
          .where((processId) => processId != pid)
          .toSet()
          .toList()
        ..sort();
  return matches;
}

Future<List<int>> _readProcessCommandLine(File file) async {
  final bytes = BytesBuilder(copy: false);
  await for (final chunk in file.openRead()) {
    if (chunk.length > _maxProcessCommandLineBytes - bytes.length) {
      throw StateError('Process command line exceeds its size limit.');
    }
    bytes.add(chunk);
  }
  return bytes.takeBytes();
}

String escapePosixExtendedRegex(String value) {
  return value.replaceAllMapped(
    RegExp(r'[.\\+*?\[\](){}^$|]'),
    (match) => '\\${match.group(0)}',
  );
}
