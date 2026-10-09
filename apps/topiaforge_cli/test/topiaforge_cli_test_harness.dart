part of 'topiaforge_cli_test.dart';

/// Forges a syntactically valid .topiaforgemod zip without needing dotnet.
File _writeTestPackage(
  Directory temp, {
  required Map<String, Object?> manifest,
  String fileName = 'package.topiaforgemod',
  String entryAssembly = 'Mod.dll',
  List<int>? entryAssemblyBytes,
}) {
  final archive = Archive()
    ..addFile(ArchiveFile.string('topiaforge.mod.json', jsonEncode(manifest)))
    ..addFile(
      entryAssemblyBytes == null
          ? ArchiveFile.string(entryAssembly, 'dll-bytes')
          : ArchiveFile.bytes(entryAssembly, entryAssemblyBytes),
    );
  final licenseFiles = manifest['licenseFiles'];
  if (licenseFiles is List) {
    for (final path in licenseFiles.whereType<String>()) {
      archive.addFile(ArchiveFile.string(path, 'Test fixture license.'));
    }
  }
  final file = File(p.join(temp.path, fileName));
  file.writeAsBytesSync(ZipEncoder().encode(archive));
  return file;
}

class _CliTestHarness {
  _CliTestHarness()
    : temp = Directory.systemTemp.createTempSync('topiaforge-cli-test-');

  final Directory temp;

  Future<ProcessResult> runCli(
    List<String> args, {
    Map<String, String> environment = const {},
    String? workingDirectory,
  }) {
    final packageRoot = Directory.current.absolute.path;
    return Process.run(
      Platform.resolvedExecutable,
      workingDirectory == null
          ? ['run', 'topiaforge', ...args]
          : [p.join(packageRoot, 'bin', 'topiaforge.dart'), ...args],
      workingDirectory: workingDirectory ?? packageRoot,
      environment: {
        ...Platform.environment,
        'TOPIAFORGE_DATA_ROOT': p.join(temp.path, 'data'),
        ...environment,
      },
    );
  }

  /// Runs a command that discovers Robotopia by itself, such as `doctor`,
  /// without letting it reach a real install.
  ///
  /// The script starts outside the repository. The CLI finds the built
  /// GameCompat extractor by walking up from its working directory, so it can
  /// neither verify a discovered game nor cache the verdict in it as
  /// `BepInEx/TopiaForge/compat-status.json`. Outside the package `dart` also
  /// skips package resolution, which makes redirecting LOCALAPPDATA and HOME
  /// safe: `dart run` in the package would re-resolve into a pub cache under
  /// the redirected folder and rewrite `.dart_tool/package_config.json` to
  /// point there.
  ///
  /// Every root that discovery reads then names an empty directory:
  /// `ROBOTOPIA_GAME_DIR`, the Tomato Cake launcher folder (LOCALAPPDATA on
  /// Windows, HOME on macOS) and Steam's libraries. [hostEnvironment] stands
  /// in for a developer's shell, and its values for those roots are dropped
  /// like the real ones.
  Future<ProcessResult> runCliWithoutGameInstalls(
    List<String> args, {
    Map<String, String> hostEnvironment = const {},
  }) {
    final outside = Directory(p.join(temp.path, 'outside-checkout'))
      ..createSync();
    // Without a checkout above it, the toolchain check reads the pinned .NET
    // SDK from global.json in the working directory.
    File(
      p.join(Directory.current.absolute.path, '..', '..', 'global.json'),
    ).copySync(p.join(outside.path, 'global.json'));
    final empty = Directory(p.join(temp.path, 'no-game-roots'))..createSync();
    final roots = [
      'ROBOTOPIA_GAME_DIR',
      'STEAM_PATH',
      'STEAM_COMPAT_CLIENT_INSTALL_PATH',
      if (Platform.isWindows) ...[
        'LOCALAPPDATA',
        'ProgramFiles',
        'ProgramFiles(x86)',
      ] else
        'HOME',
    ];
    return runCli(
      args,
      workingDirectory: outside.path,
      environment: {
        for (final MapEntry(:key, :value) in hostEnvironment.entries)
          if (!roots.any((root) => _sameVariable(key, root))) key: value,
        // Reuse the parent's spelling: Process.run also passes every parent
        // variable whose exact name this map lacks.
        for (final root in roots)
          Platform.environment.keys.firstWhere(
            (key) => _sameVariable(key, root),
            orElse: () => root,
          ): empty.path,
      },
    );
  }

  static bool _sameVariable(String left, String right) => Platform.isWindows
      ? left.toUpperCase() == right.toUpperCase()
      : left == right;

  void dispose() {
    if (temp.existsSync()) {
      temp.deleteSync(recursive: true);
    }
  }
}
