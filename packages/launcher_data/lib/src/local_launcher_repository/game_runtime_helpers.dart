part of '../local_launcher_repository.dart';

extension _GameRuntimeHelpers on LocalLauncherRepository {
  Future<GameInstall> _validateGameDirectory(String path) async {
    final directory = Directory(path).absolute;
    final layout = GameLayout.resolve(directory.path);
    final issues = <LauncherIssue>[];

    if (layout == null) {
      final expected = Platform.isMacOS
          ? 'Robotopia.app or Robotopia.exe'
          : 'Robotopia.exe';
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          message: '$expected was not found in the selected folder.',
        ),
      );
      return GameInstall(
        path: directory.path,
        executablePath: p.join(directory.path, 'Robotopia.exe'),
        bepInExStatus: ComponentState.missing,
        loaderStatus: ComponentState.missing,
        issues: issues,
      );
    }

    if (layout.kind == GameInstallLayout.linuxProton) {
      issues.add(
        const LauncherIssue(
          severity: IssueSeverity.info,
          message:
              'Windows game build detected on this system. Run it under '
              'Proton/Wine with WINEDLLOVERRIDES="winhttp=n,b" so mods load.',
        ),
      );
    }

    final managedDir = Directory(layout.managedDirPath);
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

    final gameRoot = Directory(layout.gameRoot);
    final gameBuild = await _readInstalledGameBuild(layout);
    return GameInstall(
      path: layout.gameRoot,
      executablePath: layout.executablePath,
      bepInExStatus: _detectBepInEx(gameRoot, layout),
      loaderStatus: await _detectLoader(gameRoot),
      layout: layout.kind,
      gameVersion: gameBuild?.version,
      gameVersionLabel: gameBuild?.label ?? '',
      issues: issues,
      compatStatus: await _checkGameCompat(gameRoot, managedDir),
    );
  }

  /// Reads launcher-owned build provenance without depending on the optional
  /// reflection extractor. Invalid, oversized, linked, or concurrently torn
  /// metadata is treated as unknown so constrained mods fail closed later in
  /// dependency planning while unconstrained installs remain usable.
  Future<({String version, String label})?> _readInstalledGameBuild(
    GameLayout layout,
  ) async {
    for (final file in _installedBuildCandidates(layout)) {
      try {
        final type = FileSystemEntity.typeSync(file.path, followLinks: false);
        if (type == FileSystemEntityType.notFound) {
          continue;
        }
        if (type != FileSystemEntityType.file) {
          return null;
        }
        final bytes = await _readStableInstalledBuild(file);
        final decoded = jsonDecode(utf8.decode(bytes, allowMalformed: false));
        if (decoded is! Map) {
          return null;
        }
        final version = RobotopiaGameVersion.tryFromBuildId(decoded['id']);
        final label = RobotopiaGameVersion.tryBuildLabel(version);
        return version == null || label == null
            ? null
            : (version: version, label: label);
      } on Object {
        // An existing higher-priority marker must fail closed instead of
        // allowing a lower-priority file to override corrupt provenance.
        return null;
      }
    }
    return null;
  }

  List<File> _installedBuildCandidates(GameLayout layout) {
    final root = Directory(layout.gameRoot).absolute.path;
    if (layout.kind != GameInstallLayout.macAppBundle) {
      return [File(p.join(root, 'installed-build.json'))];
    }
    final appRoot = p.basename(root) == 'Robotopia.app'
        ? root
        : p.join(root, 'Robotopia.app');
    final launcherRoot = p.basename(root) == 'Robotopia.app'
        ? p.dirname(root)
        : root;
    return [
      File(p.join(appRoot, 'installed-build.json')),
      File(p.join(launcherRoot, 'installed-build.json')),
    ];
  }

  Future<Uint8List> _readStableInstalledBuild(File file) async {
    final initial = file.statSync();
    _requireUnchangedInstalledBuild(file, initial);
    final first = await _readLauncherFileBounded(file, _maxInstalledBuildBytes);
    _requireUnchangedInstalledBuild(file, initial);
    final middle = file.statSync();
    final second = await _readLauncherFileBounded(
      file,
      _maxInstalledBuildBytes,
    );
    _requireUnchangedInstalledBuild(file, middle);
    if (sha256.convert(first).toString() != sha256.convert(second).toString()) {
      throw StateError('Installed build marker changed while being read.');
    }
    return Uint8List.fromList(second);
  }

  /// Checks the installed game against the mods' declared reflection bindings by running the bundled
  /// GameCompat.Extractor. WARN-ONLY: the result is informational and never contributes to [GameInstall.issues],
  /// so it can never block a launch. The check is cached and keyed on the GameCode.dll hash, so the extractor
  /// process only re-runs when a game update actually changes the DLL (the "auto-trigger on game update"), keeping
  /// ordinary snapshot refreshes cheap.
  Future<GameCompatStatus> _checkGameCompat(
    Directory gameDir,
    Directory managedDir, {
    bool force = false,
  }) async {
    final gameCode = File(p.join(managedDir.path, 'GameCode.dll'));
    if (!managedDir.existsSync() || !gameCode.existsSync()) {
      return GameCompatStatus.skipped();
    }

    final gameCodeSha = (await sha256.bind(gameCode.openRead()).first)
        .toString();
    final cacheFile = File(
      p.join(gameDir.path, 'BepInEx', 'TopiaForge', 'compat-status.json'),
    );

    if (!force && cacheFile.existsSync()) {
      try {
        final cached = GameCompatStatus.fromJson(
          jsonDecode(
                utf8.decode(
                  await _readLauncherFileBounded(
                    cacheFile,
                    _maxCompatStatusBytes,
                  ),
                ),
              )
              as Map<String, Object?>,
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
        await _writeJsonFileAtomic(cacheFile, status.toJson());
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
      final result = await runBoundedProcess(
        exe,
        ['verify', '--managed', managedDir.path, '--format', 'json'],
        workingDirectory: managedDir.path,
        timeout: const Duration(minutes: 2),
        maxStdoutBytes: _maxCompatStatusBytes,
        maxStderrBytes: 1024 * 1024,
      );
      // Exit 0 = all critical bindings present; 1 = a critical binding is broken (still a valid report).
      if (result.exitCode != 0 && result.exitCode != 1) {
        return GameCompatStatus.unknown();
      }

      final out = result.stdout.trim();
      if (out.isEmpty) {
        return GameCompatStatus.unknown();
      }

      final json = jsonDecode(out) as Map<String, Object?>;
      final resolve = (json['resolve'] as Map<String, Object?>?) ?? const {};
      return GameCompatStatus(
        status: (json['status'] as String?) ?? 'unknown',
        gameVersion: SemanticVersion.tryParse(
          (json['gameVersion'] as String?)?.trim() ?? '',
        )?.toString(),
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
      // 1. Bundled in the package payload root.
      ..._extractorCandidates(_repositoryRoot.path),
      // 2. Developer distribution payload.
      ..._extractorCandidates(
        p.join(_repositoryRoot.path, 'dist', 'TopiaForge'),
      ),
      // 3. Developer source build.
      ..._extractorCandidates(
        p.join(
          _repositoryRoot.path,
          'src',
          'TopiaForge.GameCompat.Extractor',
          'bin',
          'Release',
          'net10.0',
        ),
      ),
    ];
    for (final candidate in candidates) {
      if (File(candidate).existsSync()) {
        return candidate;
      }
    }
    return null;
  }

  List<String> _extractorCandidates(String directory) {
    return [
      p.join(directory, 'TopiaForge.GameCompat.Extractor'),
      p.join(directory, 'TopiaForge.GameCompat.Extractor.exe'),
    ];
  }

  ComponentState _detectBepInEx(Directory gameDir, GameLayout layout) {
    final required = [
      for (final marker in layout.bepInExMarkerFiles)
        File(p.join(gameDir.path, p.joinAll(p.posix.split(marker)))),
    ];
    final present = required.where((file) => file.existsSync()).length;
    if (present == required.length) {
      return ComponentState.ready;
    }
    return present == 0 ? ComponentState.missing : ComponentState.partial;
  }

  Future<ComponentState> _detectLoader(Directory gameDir) async {
    const loaderDlls = [
      'TopiaForge.ModManager.dll',
      'TopiaForge.ModManager.Core.dll',
      'TopiaForge.Mods.Abstractions.dll',
      'TopiaForge.Mods.UnityUi.dll',
    ];
    final pluginDir = Directory(
      p.join(gameDir.path, 'BepInEx', 'plugins', 'TopiaForge.ModManager'),
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
        'TopiaForge.ModManager',
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
      if (!await _sameFileContents(installed[index], built[index])) {
        return ComponentState.partial;
      }
    }

    return ComponentState.ready;
  }

  Future<bool> _sameFileContents(File left, File right) async {
    if (await left.length() != await right.length()) {
      return false;
    }
    final leftHash = await sha256.bind(left.openRead()).first;
    final rightHash = await sha256.bind(right.openRead()).first;
    return leftHash.toString() == rightHash.toString();
  }
}

void _requireUnchangedInstalledBuild(File file, FileStat expected) {
  if (FileSystemEntity.typeSync(file.path, followLinks: false) !=
      FileSystemEntityType.file) {
    throw StateError('Installed build marker is not a regular file.');
  }
  final actual = file.statSync();
  if (actual.size != expected.size || actual.modified != expected.modified) {
    throw StateError('Installed build marker changed while being read.');
  }
}

const _maxInstalledBuildBytes = 64 * 1024;

const _maxCompatStatusBytes = 4 * 1024 * 1024;
