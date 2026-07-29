part of 'topiaforge.dart';

final class _SidecarRuntime {
  const _SidecarRuntime(this.scriptPath);

  final String scriptPath;

  String get directory => File(scriptPath).parent.path;
  String get npmExecutable => Platform.isWindows ? 'npm.cmd' : 'npm';

  Future<void> prepare({required bool requireDependencies}) async {
    _requireRegularFile(scriptPath, 'sidecar entry point');
    final packageJson = p.join(directory, 'package.json');
    final lockFile = p.join(directory, 'package-lock.json');
    _requireRegularFile(packageJson, 'sidecar package manifest');
    _requireRegularFile(lockFile, 'sidecar lockfile');

    final nodeVersion = await runBoundedProcess(
      'node',
      const ['--version'],
      timeout: const Duration(seconds: 10),
      maxStdoutBytes: 1024,
      maxStderrBytes: 4096,
    );
    if (nodeVersion.exitCode != 0 ||
        !UgcNodeVersionPolicy.supports(nodeVersion.stdout)) {
      throw StateError(
        'The UGC sidecar requires ${UgcNodeVersionPolicy.requirement}.',
      );
    }
    if (!requireDependencies) return;

    final modules = p.join(directory, 'node_modules');
    final modulesType = FileSystemEntity.typeSync(modules, followLinks: false);
    if (modulesType != FileSystemEntityType.notFound &&
        modulesType != FileSystemEntityType.directory) {
      throw StateError(
        'UGC sidecar node_modules must be a real directory. Remove it and retry.',
      );
    }
    var dependenciesOk = false;
    if (modulesType == FileSystemEntityType.directory) {
      final check = await runBoundedProcess(
        npmExecutable,
        const ['ls', '--omit=dev'],
        workingDirectory: directory,
        timeout: const Duration(seconds: 30),
        maxStdoutBytes: 1024 * 1024,
        maxStderrBytes: 1024 * 1024,
      );
      dependenciesOk = check.exitCode == 0;
    }
    if (!dependenciesOk) {
      stdout.writeln(
        'Restoring locked sidecar dependencies (npm ci --ignore-scripts)...',
      );
      final install = await runBoundedProcess(
        npmExecutable,
        const ['ci', '--ignore-scripts', '--no-fund', '--no-audit'],
        workingDirectory: directory,
        timeout: const Duration(minutes: 5),
        maxStdoutBytes: 4 * 1024 * 1024,
        maxStderrBytes: 4 * 1024 * 1024,
      );
      if (install.exitCode != 0) {
        throw StateError(
          'Locked sidecar dependency restore failed (npm exit ${install.exitCode}).',
        );
      }
    }

    final check = await runBoundedProcess(
      'node',
      [scriptPath, '--check'],
      workingDirectory: directory,
      timeout: const Duration(seconds: 30),
      maxStdoutBytes: 1024 * 1024,
      maxStderrBytes: 1024 * 1024,
    );
    if (check.exitCode != 0 ||
        !check.stdout.contains('deps        : installed')) {
      throw StateError('UGC sidecar dependency validation failed.');
    }
  }

  void _requireRegularFile(String path, String label) {
    if (FileSystemEntity.typeSync(path, followLinks: false) !=
        FileSystemEntityType.file) {
      throw StateError('The $label must be a regular file: $path');
    }
  }
}

_SidecarRuntime? _findSidecar() {
  var directory = Directory.current.absolute;
  while (true) {
    final candidate = p.join(
      directory.path,
      'tools',
      'ugc-automerge-sidecar',
      'index.mjs',
    );
    if (FileSystemEntity.typeSync(candidate, followLinks: false) ==
        FileSystemEntityType.file) {
      return _SidecarRuntime(candidate);
    }
    final parent = directory.parent;
    if (parent.path == directory.path) return null;
    directory = parent;
  }
}
