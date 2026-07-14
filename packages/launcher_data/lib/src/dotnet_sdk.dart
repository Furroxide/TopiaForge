import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:path/path.dart' as p;

import 'bounded_process.dart';

typedef DotnetVersionProbe =
    Future<BoundedProcessResult> Function(
      String executable,
      String workingDirectory,
    );

typedef RepositoryDotnetSdkResolver =
    Future<DotnetSdkSelection> Function(Directory repositoryRoot);

/// A concrete .NET host whose active SDK was verified from the repository.
final class DotnetSdkSelection {
  const DotnetSdkSelection({
    required this.executable,
    required this.version,
    required this.requiredVersion,
  });

  final String executable;
  final String version;
  final String requiredVersion;
}

/// Resolves a .NET host that actually activates the SDK pinned by global.json.
///
/// Merely locating a `dotnet` binary is insufficient: hosts installed in
/// different prefixes can expose different SDK inventories. Every candidate
/// is therefore run from [repositoryRoot], and only a successful, exact
/// `dotnet --version` result is accepted.
Future<DotnetSdkSelection> resolveRepositoryDotnetSdk(
  Directory repositoryRoot, {
  Iterable<String>? candidateExecutables,
  DotnetVersionProbe? versionProbe,
}) async {
  final root = repositoryRoot.absolute;
  final requiredVersion = _readPinnedDotnetSdkVersion(root);
  final candidates = candidateExecutables == null
      ? await _discoverDotnetCandidates()
      : _deduplicateCandidates(candidateExecutables);
  final probe = versionProbe ?? _probeDotnetVersion;
  final failures = <String>[];

  for (final executable in candidates) {
    try {
      final result = await probe(executable, root.path);
      final version = _reportedDotnetVersion(result.stdout);
      if (result.exitCode != 0 || version == null) {
        failures.add('$executable did not report a usable SDK version');
        continue;
      }
      if (version != requiredVersion) {
        failures.add('$executable reported $version');
        continue;
      }
      return DotnetSdkSelection(
        executable: executable,
        version: version,
        requiredVersion: requiredVersion,
      );
    } on Object {
      failures.add('$executable could not be probed');
    }
  }

  final requirement =
      '.NET SDK $requiredVersion pinned by ${p.join(root.path, 'global.json')}';
  final attempts = failures.isEmpty
      ? 'No dotnet executables were found.'
      : 'Checked: ${failures.join('; ')}.';
  throw StateError(
    'Could not find $requirement. $attempts Set ROBOTOPIA_DOTNET_PATH to '
    'the dotnet executable that provides the required SDK.',
  );
}

Future<BoundedProcessResult> _probeDotnetVersion(
  String executable,
  String workingDirectory,
) => runBoundedProcess(
  executable,
  const ['--version'],
  workingDirectory: workingDirectory,
  runInShell: false,
  timeout: const Duration(seconds: 10),
  maxStdoutBytes: 4096,
  maxStderrBytes: 16 * 1024,
);

String? _reportedDotnetVersion(String output) {
  final lines = const LineSplitter()
      .convert(output.trim())
      .where((line) => line.trim().isNotEmpty)
      .toList();
  if (lines.length != 1) return null;
  final version = lines.single.trim();
  return RegExp(r'^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$').hasMatch(version)
      ? version
      : null;
}

String _readPinnedDotnetSdkVersion(Directory repositoryRoot) {
  final file = File(p.join(repositoryRoot.path, 'global.json'));
  final type = FileSystemEntity.typeSync(file.path, followLinks: false);
  if (type == FileSystemEntityType.notFound) {
    throw StateError(
      'global.json was not found at ${file.path}; the required .NET SDK '
      'cannot be determined.',
    );
  }
  if (type != FileSystemEntityType.file) {
    throw StateError(
      'global.json must be a regular file and cannot be a link.',
    );
  }
  final bytes = _readStableGlobalJson(file);
  final decoded = jsonDecode(utf8.decode(bytes, allowMalformed: false));
  if (decoded is! Map || decoded['sdk'] is! Map) {
    throw StateError('global.json must contain an sdk object.');
  }
  final sdk = decoded['sdk']! as Map;
  final version = sdk['version'];
  if (version is! String ||
      !RegExp(r'^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$').hasMatch(version)) {
    throw StateError('global.json must pin an exact .NET SDK version.');
  }
  return version;
}

List<int> _readStableGlobalJson(File file) {
  const limit = 1024 * 1024;
  final before = file.statSync();
  final first = _readBoundedGlobalJson(file, limit);
  final middle = file.statSync();
  final second = _readBoundedGlobalJson(file, limit);
  final after = file.statSync();
  if (!_sameStat(before, middle) ||
      !_sameStat(middle, after) ||
      sha256.convert(first).toString() != sha256.convert(second).toString()) {
    throw StateError('global.json changed while it was being read.');
  }
  return second;
}

List<int> _readBoundedGlobalJson(File file, int limit) {
  if (FileSystemEntity.typeSync(file.path, followLinks: false) !=
      FileSystemEntityType.file) {
    throw StateError('global.json must remain a regular file while read.');
  }
  final input = file.openSync();
  try {
    if (input.lengthSync() > limit) {
      throw StateError('global.json exceeds the 1 MB limit.');
    }
    final bytes = input.readSync(limit + 1);
    if (bytes.length > limit) {
      throw StateError('global.json exceeds the 1 MB limit.');
    }
    return bytes;
  } finally {
    input.closeSync();
  }
}

bool _sameStat(FileStat left, FileStat right) =>
    left.size == right.size && left.modified == right.modified;

Future<List<String>> _discoverDotnetCandidates() async {
  final candidates = <String>[];
  void add(String? value) {
    if (value != null && value.trim().isNotEmpty) candidates.add(value.trim());
  }

  add(Platform.environment['ROBOTOPIA_DOTNET_PATH']);
  add(Platform.environment['DOTNET_HOST_PATH']);
  for (final name in const [
    'DOTNET_ROOT',
    'DOTNET_ROOT_ARM64',
    'DOTNET_ROOT_X64',
    'DOTNET_ROOT_X86',
  ]) {
    final dotnetRoot = Platform.environment[name];
    if (dotnetRoot != null && dotnetRoot.trim().isNotEmpty) {
      add(p.join(dotnetRoot, Platform.isWindows ? 'dotnet.exe' : 'dotnet'));
    }
  }

  try {
    final located = await runBoundedProcess(
      Platform.isWindows ? 'where' : 'which',
      Platform.isWindows ? const ['dotnet'] : const ['-a', 'dotnet'],
      runInShell: false,
      timeout: const Duration(seconds: 5),
      maxStdoutBytes: 64 * 1024,
      maxStderrBytes: 4096,
    );
    if (located.exitCode == 0) {
      for (final line in const LineSplitter().convert(located.stdout)) {
        add(line);
      }
    }
  } on Object {
    // Common install roots below still allow deterministic discovery.
  }

  if (Platform.isMacOS) {
    candidates.addAll(const [
      '/opt/homebrew/bin/dotnet',
      '/usr/local/bin/dotnet',
      '/opt/homebrew/share/dotnet/dotnet',
      '/usr/local/share/dotnet/dotnet',
    ]);
  } else if (Platform.isLinux) {
    candidates.addAll([
      p.join(Platform.environment['HOME'] ?? '', '.dotnet', 'dotnet'),
      '/usr/bin/dotnet',
      '/usr/local/bin/dotnet',
      '/usr/share/dotnet/dotnet',
    ]);
  } else if (Platform.isWindows) {
    for (final root in {
      Platform.environment['ProgramFiles'],
      Platform.environment['ProgramW6432'],
    }) {
      if (root != null && root.isNotEmpty) {
        add(p.join(root, 'dotnet', 'dotnet.exe'));
      }
    }
  }
  return _deduplicateCandidates(candidates.where(_isUsableDotnetCandidate));
}

bool _isUsableDotnetCandidate(String candidate) {
  try {
    final resolved = File(candidate).resolveSymbolicLinksSync();
    return FileSystemEntity.typeSync(resolved, followLinks: false) ==
        FileSystemEntityType.file;
  } on FileSystemException {
    return false;
  }
}

List<String> _deduplicateCandidates(Iterable<String> candidates) {
  final seen = <String>{};
  final result = <String>[];
  for (final raw in candidates) {
    final candidate = raw.trim();
    if (candidate.isEmpty) continue;
    final key = Platform.isWindows ? candidate.toLowerCase() : candidate;
    if (seen.add(key)) result.add(candidate);
  }
  return result;
}
