part of '../local_launcher_repository.dart';

extension _GameRuntimeHelpers on LocalLauncherRepository {
  Future<GameInstall> _validateGameDirectory(String path) async {
    final directory = Directory(path).absolute;
    final executable = File(p.join(directory.path, 'Robotopia.exe'));
    final managedDir = Directory(
      p.join(directory.path, 'Robotopia_Data', 'Managed'),
    );
    final issues = <LauncherIssue>[];

    if (!executable.existsSync()) {
      issues.add(
        const LauncherIssue(
          severity: IssueSeverity.error,
          message: 'Robotopia.exe was not found in the selected folder.',
        ),
      );
    }
    if (!managedDir.existsSync() ||
        !File(p.join(managedDir.path, 'UnityEngine.dll')).existsSync()) {
      issues.add(
        const LauncherIssue(
          severity: IssueSeverity.warning,
          message:
              'Unity Mono managed assemblies were not found or are incomplete.',
        ),
      );
    }

    return GameInstall(
      path: directory.path,
      executablePath: executable.path,
      bepInExStatus: _detectBepInEx(directory),
      loaderStatus: _detectLoader(directory),
      issues: issues,
      compatStatus: await _checkGameCompat(directory),
    );
  }

  /// Checks the installed game against the mods' declared reflection bindings by running the bundled
  /// GameCompat.Extractor. WARN-ONLY: the result is informational and never contributes to [GameInstall.issues],
  /// so it can never block a launch. The check is cached and keyed on the GameCode.dll hash, so the extractor
  /// process only re-runs when a game update actually changes the DLL (the "auto-trigger on game update"), keeping
  /// ordinary snapshot refreshes cheap.
  Future<GameCompatStatus> _checkGameCompat(
    Directory gameDir, {
    bool force = false,
  }) async {
    final managedDir = Directory(
      p.join(gameDir.path, 'Robotopia_Data', 'Managed'),
    );
    final gameCode = File(p.join(managedDir.path, 'GameCode.dll'));
    if (!managedDir.existsSync() || !gameCode.existsSync()) {
      return GameCompatStatus.skipped();
    }

    final gameCodeSha = sha256.convert(gameCode.readAsBytesSync()).toString();
    final cacheFile = File(
      p.join(
        gameDir.path,
        'BepInEx',
        'RobotopiaModManager',
        'compat-status.json',
      ),
    );

    if (!force && cacheFile.existsSync()) {
      try {
        final cached = GameCompatStatus.fromJson(
          jsonDecode(cacheFile.readAsStringSync()) as Map<String, Object?>,
        );
        // Same game build we already analysed → reuse it (no process spawn).
        if (cached.gameCodeSha == gameCodeSha && cached.isKnown) {
          return cached;
        }
      } catch (_) {
        // Corrupt cache; fall through and recompute.
      }
    }

    final status = await _runCompatExtractor(managedDir, gameCodeSha);

    // Only cache a real verdict; never cache 'unknown'/'skipped' so it retries once the tool is available.
    if (status.isKnown) {
      try {
        cacheFile.parent.createSync(recursive: true);
        cacheFile.writeAsStringSync(jsonEncode(status.toJson()));
      } catch (_) {
        // Non-writable install dir; skip caching but still return the live result.
      }
    }

    return status;
  }

  Future<GameCompatStatus> _runCompatExtractor(
    Directory managedDir,
    String gameCodeSha,
  ) async {
    final exe = _resolveExtractorExe();
    if (exe == null) {
      return GameCompatStatus.unknown();
    }

    try {
      final result = await Process.run(exe, [
        'verify',
        '--managed',
        managedDir.path,
        '--format',
        'json',
      ], workingDirectory: managedDir.path);
      // Exit 0 = all critical bindings present; 1 = a critical binding is broken (still a valid report).
      if (result.exitCode != 0 && result.exitCode != 1) {
        return GameCompatStatus.unknown();
      }

      final out = (result.stdout as String).trim();
      if (out.isEmpty) {
        return GameCompatStatus.unknown();
      }

      final json = jsonDecode(out) as Map<String, Object?>;
      final resolve = (json['resolve'] as Map<String, Object?>?) ?? const {};
      return GameCompatStatus(
        status: (json['status'] as String?) ?? 'unknown',
        gameVersionLabel: (json['gameVersionLabel'] as String?) ?? '',
        surfaceHash: (json['surfaceHash'] as String?) ?? '',
        gameCodeSha: gameCodeSha,
        extractorVersion: (json['extractorVersion'] as String?) ?? '',
        findings: _parseCompatFindings(resolve),
      );
    } catch (_) {
      // Extractor missing/crashed/locked — degrade to "unknown", never throw, never block launch.
      return GameCompatStatus.unknown();
    }
  }

  List<CompatFinding> _parseCompatFindings(Map<String, Object?> resolve) {
    final list = (resolve['findings'] as List<Object?>?) ?? const [];
    return [
      for (final item in list)
        if (item is Map<String, Object?>) CompatFinding.fromJson(item),
    ];
  }

  String? _resolveExtractorExe() {
    final candidates = <String>[
      // 1. bundled alongside the launcher executable (consumer install)
      p.join(
        File(Platform.resolvedExecutable).parent.path,
        'Robotopia.GameCompat.Extractor.exe',
      ),
      // 2. dev dist payload
      p.join(
        _repositoryRoot.path,
        'dist',
        'RobotopiaModManager',
        'Robotopia.GameCompat.Extractor.exe',
      ),
      // 3. dev source build
      p.join(
        _repositoryRoot.path,
        'src',
        'Robotopia.GameCompat.Extractor',
        'bin',
        'Release',
        'net8.0',
        'Robotopia.GameCompat.Extractor.exe',
      ),
    ];
    for (final candidate in candidates) {
      if (File(candidate).existsSync()) {
        return candidate;
      }
    }
    return null;
  }

  ComponentState _detectBepInEx(Directory gameDir) {
    final required = [
      File(p.join(gameDir.path, 'winhttp.dll')),
      File(p.join(gameDir.path, 'doorstop_config.ini')),
      File(p.join(gameDir.path, 'BepInEx', 'core', 'BepInEx.dll')),
    ];
    final present = required.where((file) => file.existsSync()).length;
    if (present == required.length) {
      return ComponentState.ready;
    }
    return present == 0 ? ComponentState.missing : ComponentState.partial;
  }

  ComponentState _detectLoader(Directory gameDir) {
    const loaderDlls = [
      'Robotopia.ModManager.dll',
      'Robotopia.ModManager.Core.dll',
      'Robotopia.Mods.Abstractions.dll',
    ];
    final pluginDir = Directory(
      p.join(gameDir.path, 'BepInEx', 'plugins', 'RobotopiaModManager'),
    );
    final installed = [
      for (final dll in loaderDlls) File(p.join(pluginDir.path, dll)),
    ];
    final present = installed.where((file) => file.existsSync()).length;
    if (present == 0) {
      return ComponentState.missing;
    }
    if (present != installed.length) {
      return ComponentState.partial;
    }

    final builtDir = Directory(
      p.join(
        _repositoryRoot.path,
        'src',
        'Robotopia.ModManager',
        'bin',
        'Release',
        'netstandard2.1',
      ),
    );
    final built = [
      for (final dll in loaderDlls) File(p.join(builtDir.path, dll)),
    ];
    if (!built.every((file) => file.existsSync())) {
      return ComponentState.ready;
    }

    for (var index = 0; index < loaderDlls.length; index++) {
      if (!_sameFileContents(installed[index], built[index])) {
        return ComponentState.partial;
      }
    }

    return ComponentState.ready;
  }

  bool _sameFileContents(File left, File right) {
    if (left.lengthSync() != right.lengthSync()) {
      return false;
    }
    final leftHash = sha256.convert(left.readAsBytesSync()).toString();
    final rightHash = sha256.convert(right.readAsBytesSync()).toString();
    return leftHash == rightHash;
  }
}
