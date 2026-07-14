import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:path/path.dart' as p;

import 'bounded_process.dart';

const ugcNpmCiArguments = <String>[
  'ci',
  '--ignore-scripts',
  '--no-audit',
  '--no-fund',
  '--omit=dev',
];

final class TrustedUgcSidecar {
  TrustedUgcSidecar._({
    required this.directory,
    required this.script,
    required this.lockDigest,
  });

  final Directory directory;
  final File script;
  final String lockDigest;

  static TrustedUgcSidecar inspectRepository(Directory repositoryRoot) {
    return inspectDirectory(
      Directory(
        p.join(repositoryRoot.absolute.path, 'tools', 'ugc-automerge-sidecar'),
      ),
    );
  }

  static TrustedUgcSidecar inspectDirectory(Directory directory) {
    _requireRegularDirectory(directory, 'UGC sidecar directory');
    final script = File(p.join(directory.path, 'index.mjs'));
    final packageFile = File(p.join(directory.path, 'package.json'));
    final lockFile = File(p.join(directory.path, 'package-lock.json'));
    _requireRegularFile(script, 'UGC sidecar script');
    final packageBytes = _readStableFile(
      packageFile,
      1024 * 1024,
      'package.json',
    );
    final lockBytes = _readStableFile(
      lockFile,
      16 * 1024 * 1024,
      'package-lock.json',
    );
    final package = _jsonObject(packageBytes, 'package.json');
    final lock = _jsonObject(lockBytes, 'package-lock.json');
    _validateLockfile(package, lock);
    return TrustedUgcSidecar._(
      directory: directory.absolute,
      script: script.absolute,
      lockDigest: sha256.convert([...packageBytes, ...lockBytes]).toString(),
    );
  }

  Future<UgcNodeToolchain> ensureDependencies() async {
    final toolchain = await UgcNodeToolchain.resolve();
    final nodeModules = Directory(p.join(directory.path, 'node_modules'));
    final stamp = File(p.join(nodeModules.path, '.topiaforge-lock-sha256'));
    if (_hasCurrentDependencyStamp(nodeModules, stamp, lockDigest)) {
      return toolchain;
    }
    final result = await runBoundedProcess(
      toolchain.nodeExecutable,
      [toolchain.npmCliPath, ...ugcNpmCiArguments],
      workingDirectory: directory.path,
      runInShell: false,
      timeout: const Duration(minutes: 10),
      maxStdoutBytes: 8 * 1024 * 1024,
      maxStderrBytes: 8 * 1024 * 1024,
    );
    if (result.exitCode != 0) {
      throw StateError(
        'Lockfile-backed npm ci failed with exit ${result.exitCode}: '
        '${result.stderr.trim()}',
      );
    }
    _requireRegularDirectory(nodeModules, 'UGC sidecar node_modules');
    final temporary = File('${stamp.path}.$pid.tmp');
    temporary.writeAsStringSync(lockDigest, flush: true);
    temporary.renameSync(stamp.path);
    return toolchain;
  }
}

final class UgcNodeToolchain {
  const UgcNodeToolchain({
    required this.nodeExecutable,
    required this.npmCliPath,
    required this.nodeMajor,
  });

  final String nodeExecutable;
  final String npmCliPath;
  final int nodeMajor;

  static Future<UgcNodeToolchain> resolve() async {
    final node = await _locateExecutable('node');
    final version = await runBoundedProcess(
      node,
      const ['--version'],
      runInShell: false,
      timeout: const Duration(seconds: 5),
      maxStdoutBytes: 4096,
      maxStderrBytes: 4096,
    );
    final match = RegExp(
      r'^v([0-9]+)(?:\.|$)',
    ).firstMatch(version.stdout.trim());
    final major = match == null ? null : int.tryParse(match.group(1)!);
    if (version.exitCode != 0 || major == null || major < 20) {
      throw StateError('The UGC publisher requires Node.js 20 or newer.');
    }
    final npmPath = await _locateExecutable('npm');
    final npmCli = _resolveNpmCli(node, npmPath);
    return UgcNodeToolchain(
      nodeExecutable: node,
      npmCliPath: npmCli,
      nodeMajor: major,
    );
  }
}

Future<String> _locateExecutable(String name) async {
  final result = await runBoundedProcess(
    Platform.isWindows ? 'where' : 'which',
    [name],
    runInShell: false,
    timeout: const Duration(seconds: 5),
    maxStdoutBytes: 64 * 1024,
    maxStderrBytes: 4096,
  );
  if (result.exitCode != 0) {
    throw StateError('$name was not found on PATH.');
  }
  for (final line in const LineSplitter().convert(result.stdout)) {
    final raw = line.trim();
    if (raw.isEmpty) continue;
    try {
      final resolved = File(raw).resolveSymbolicLinksSync();
      if (FileSystemEntity.typeSync(resolved, followLinks: false) ==
          FileSystemEntityType.file) {
        return resolved;
      }
    } on FileSystemException {
      // Try the next locator result.
    }
  }
  throw StateError('$name did not resolve to a regular executable file.');
}

String _resolveNpmCli(String node, String locatedNpm) {
  final candidates = <String>[];
  try {
    candidates.add(File(locatedNpm).resolveSymbolicLinksSync());
  } on FileSystemException {
    // Derived candidates below cover Windows npm.cmd and broken command wrappers.
  }
  for (final base in {p.dirname(node), p.dirname(locatedNpm)}) {
    candidates.addAll([
      p.join(base, 'node_modules', 'npm', 'bin', 'npm-cli.js'),
      p.normalize(
        p.join(base, '..', 'lib', 'node_modules', 'npm', 'bin', 'npm-cli.js'),
      ),
    ]);
  }
  for (final candidate in candidates) {
    if (p.basename(candidate).toLowerCase() == 'npm-cli.js' &&
        FileSystemEntity.typeSync(candidate, followLinks: false) ==
            FileSystemEntityType.file) {
      return File(candidate).absolute.path;
    }
  }
  throw StateError(
    'npm-cli.js was not found beside the selected Node.js runtime.',
  );
}

bool _hasCurrentDependencyStamp(
  Directory nodeModules,
  File stamp,
  String expected,
) {
  try {
    _requireRegularDirectory(nodeModules, 'UGC sidecar node_modules');
    final value = utf8.decode(
      _readStableFile(stamp, 1024, 'UGC dependency stamp'),
      allowMalformed: false,
    );
    return value == expected;
  } on Object {
    return false;
  }
}

void _validateLockfile(
  Map<String, Object?> package,
  Map<String, Object?> lock,
) {
  final engines = _objectMap(package['engines']);
  final nodeRange = engines['node'];
  final minimum = nodeRange is String
      ? RegExp(r'>=\s*([0-9]+)').firstMatch(nodeRange)
      : null;
  if (minimum == null || int.parse(minimum.group(1)!) < 20) {
    throw StateError('The UGC sidecar package must require Node.js 20+.');
  }
  if ((lock['lockfileVersion'] as num?)?.toInt() != 3 ||
      lock['requires'] != true) {
    throw StateError('The UGC sidecar requires a lockfileVersion 3 lockfile.');
  }
  final root = _objectMap(_objectMap(lock['packages'])['']);
  for (final key in const ['name', 'version']) {
    if (package[key] is! String || root[key] != package[key]) {
      throw StateError('package-lock.json does not match package.json ($key).');
    }
  }
  if (!_sameStringMap(
    _objectMap(package['dependencies']),
    _objectMap(root['dependencies']),
  )) {
    throw StateError(
      'package-lock.json dependencies do not match package.json.',
    );
  }
}

bool _sameStringMap(Map<String, Object?> left, Map<String, Object?> right) =>
    left.length == right.length &&
    left.entries.every(
      (entry) => entry.value is String && right[entry.key] == entry.value,
    );

Map<String, Object?> _jsonObject(List<int> bytes, String label) {
  final decoded = jsonDecode(utf8.decode(bytes, allowMalformed: false));
  if (decoded is! Map) throw StateError('$label must contain a JSON object.');
  return _objectMap(decoded);
}

Map<String, Object?> _objectMap(Object? value) => value is Map
    ? value.map((key, item) => MapEntry(key.toString(), item))
    : const {};

List<int> _readStableFile(File file, int maxBytes, String label) {
  final before = _requireRegularFile(file, label);
  final first = _readBounded(file, maxBytes, label);
  _requireRegularFile(file, label, expected: before);
  final middle = file.statSync();
  final second = _readBounded(file, maxBytes, label);
  _requireRegularFile(file, label, expected: middle);
  if (sha256.convert(first).toString() != sha256.convert(second).toString()) {
    throw StateError('$label changed while it was being read.');
  }
  return second;
}

List<int> _readBounded(File file, int maxBytes, String label) {
  final input = file.openSync();
  try {
    if (input.lengthSync() > maxBytes) throw StateError('$label is too large.');
    final bytes = input.readSync(maxBytes + 1);
    if (bytes.length > maxBytes) throw StateError('$label is too large.');
    return bytes;
  } finally {
    input.closeSync();
  }
}

FileStat _requireRegularFile(File file, String label, {FileStat? expected}) {
  if (FileSystemEntity.typeSync(file.path, followLinks: false) !=
      FileSystemEntityType.file) {
    throw StateError('$label must be a regular file and cannot be a link.');
  }
  final actual = file.statSync();
  if (expected != null &&
      (actual.size != expected.size || actual.modified != expected.modified)) {
    throw StateError('$label changed while it was being read.');
  }
  return actual;
}

void _requireRegularDirectory(Directory directory, String label) {
  if (FileSystemEntity.typeSync(directory.path, followLinks: false) !=
      FileSystemEntityType.directory) {
    throw StateError(
      '$label must be a regular directory and cannot be a link.',
    );
  }
}
