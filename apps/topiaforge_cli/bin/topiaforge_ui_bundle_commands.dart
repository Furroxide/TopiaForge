part of 'topiaforge.dart';

/// `topiaforge unity build-ui-bundle` — rebuilds the TopiaForge brand AssetBundle
/// (topiaforge-ui.bundle) from the committed Unity project at tools/unity-ui-bundle. The Unity
/// side (TopiaForge.UiBundleBuilder) copies the bundle into src/TopiaForge.Mods.UnityUi/Assets and
/// writes its provenance manifest; the kit csproj embeds it into TopiaForge.Mods.UnityUi.dll.
/// Cross-platform replacement for the retired tools/build-ui-bundle.ps1.
extension _TopiaForgeUiBundleCommands on _TopiaForgeCli {
  Future<int> _unityBuildUiBundle(List<String> args) async {
    final repoRoot = _findRepoRoot();
    if (repoRoot == null) {
      stderr.writeln(
        'The TopiaForge repository root was not found from '
        '${Directory.current.path} — run from inside the repo (the bundle '
        'project lives at tools/unity-ui-bundle).',
      );
      return 1;
    }
    final projectPath = p.join(repoRoot, 'tools', 'unity-ui-bundle');
    if (!Directory(p.join(projectPath, 'Assets')).existsSync() ||
        !Directory(p.join(projectPath, 'ProjectSettings')).existsSync()) {
      stderr.writeln(
        '$projectPath is not a Unity project (no Assets/ + ProjectSettings/).',
      );
      return 1;
    }

    final editor = await _resolveUiBundleEditor(_option(args, '--unity'));
    final dryRun = args.contains('--dry-run');

    final logDir = p.join(repoRoot, 'build');
    final essentialsLog = p.join(logDir, 'ui-bundle-essentials.log');
    final buildLog = p.join(logDir, 'ui-bundle-build.log');
    final needsEssentials = !Directory(
      p.join(projectPath, 'Assets', 'TextMesh Pro'),
    ).existsSync();

    if (dryRun) {
      stdout.writeln('Repo root:     $repoRoot');
      stdout.writeln('Unity project: $projectPath');
      stdout.writeln(
        'Build editor:  ${editor == null ? '(none eligible — need Unity ${RobotopiaGameUnityCompatibility.requiredEditorVersion})' : '${editor.version} at ${editor.path}'}',
      );
      stdout.writeln(
        'TMP essentials: ${needsEssentials ? 'would import first (Assets/TextMesh Pro missing)' : 'already imported'}',
      );
      stdout.writeln('Logs:          $essentialsLog, $buildLog');
      return editor == null ? 1 : 0;
    }
    if (editor == null) {
      // _resolveUiBundleEditor already printed the reason/remediation.
      return 1;
    }

    Directory(logDir).createSync(recursive: true);
    if (needsEssentials) {
      stdout.writeln('Importing TMP essentials (first run)...');
      final code = await _runUiBundlePhase(
        editor.path,
        projectPath,
        'TopiaForge.UiBundleBuilder.ImportEssentials',
        essentialsLog,
      );
      if (code != 0) {
        return code;
      }
    }

    stdout.writeln('Building UI bundle with Unity ${editor.version}...');
    final code = await _runUiBundlePhase(
      editor.path,
      projectPath,
      'TopiaForge.UiBundleBuilder.Build',
      buildLog,
    );
    if (code != 0) {
      return code;
    }

    final bundle = File(
      p.join(
        repoRoot,
        'src',
        'TopiaForge.Mods.UnityUi',
        'Assets',
        'topiaforge-ui.bundle',
      ),
    );
    if (!bundle.existsSync()) {
      stderr.writeln(
        'Unity reported success but ${bundle.path} was not produced. Check $buildLog.',
      );
      return 1;
    }
    final bytes = readBoundedRegularFileSync(
      bundle,
      maxBytes: CliFileLimits.uiBundle,
    );
    stdout.writeln('UI bundle written: ${bundle.path}');
    stdout.writeln(
      '  size:   ${(bytes.length / (1024 * 1024)).toStringAsFixed(2)} MB',
    );
    stdout.writeln('  sha256: ${sha256.convert(bytes)}');

    if (args.contains('--rebuild')) {
      if (!await _ensureBuildTooling()) {
        return 1;
      }
      stdout.writeln('Rebuilding TopiaForge.Mods.UnityUi (Release)...');
      if (!await _rebuildUiBundleAssembly(repoRoot)) {
        return 1;
      }
      stdout.writeln('TopiaForge.Mods.UnityUi rebuilt with the new bundle.');
    } else {
      stdout.writeln(
        'Rebuild TopiaForge.Mods.UnityUi so the embedded resource picks up the '
        'new bundle (or re-run with --rebuild).',
      );
    }
    return 0;
  }

  /// Picks the build editor: an explicit `--unity` executable gated by its own
  /// `-version` output, else the newest eligible editor from the repository's
  /// verified cross-platform Hub scan. Hub folder names are hints, not proof of
  /// the binary inside them. Prints the failure reason and returns null when
  /// nothing fits.
  Future<UnityEditor?> _resolveUiBundleEditor(String? explicitPath) async {
    if (explicitPath != null) {
      if (!File(explicitPath).existsSync()) {
        stderr.writeln('Unity editor not found at $explicitPath.');
        return null;
      }
      final probe = await _probeUnityVersion(explicitPath);
      if (probe.version == null) {
        stderr.writeln(
          '${probe.error} Required: Unity '
          '${RobotopiaGameUnityCompatibility.requiredEditorVersion}.',
        );
        return null;
      }
      final version = probe.version!;
      if (!WorldBundleEditorGate.isEligible(version)) {
        stderr.writeln(
          'Editor at $explicitPath reports version "$version" — required: '
          'Unity ${RobotopiaGameUnityCompatibility.requiredEditorDisplay}.',
        );
        return null;
      }
      return UnityEditor(version: version, path: explicitPath);
    }

    final editors = await developerRepository.listUnityEditors();
    for (final editor in editors) {
      if (WorldBundleEditorGate.isEligible(editor.version)) {
        return editor;
      }
    }
    stderr.writeln(
      'No eligible Unity editor found (required: Unity '
      '${RobotopiaGameUnityCompatibility.requiredEditorDisplay}). '
      '${WorldBundleEditorGate.installHint}',
    );
    return null;
  }

  /// Bounded `Unity -version` probe (prints the version and exits).
  Future<_UnityVersionProbeResult> _probeUnityVersion(String exePath) async {
    try {
      final result = await runBoundedProcess(
        exePath,
        const ['-version'],
        timeout: _unityProbeTimeout,
        maxStdoutBytes: _unityProbeOutputLimit ~/ 2,
        maxStderrBytes: _unityProbeOutputLimit ~/ 2,
      );
      if (result.exitCode != 0) {
        return _UnityVersionProbeResult.failure(
          'Unity version probe failed with exit code ${result.exitCode}: $exePath.',
        );
      }
      final match = RegExp(
        r'\d+\.\d+\.\d+[a-z]\d+',
      ).firstMatch(_coalesceProcessOutput(result.stdout, result.stderr));
      if (match == null) {
        return _UnityVersionProbeResult.failure(
          'Unity version probe returned no recognizable editor version: $exePath.',
        );
      }
      return _UnityVersionProbeResult.success(match.group(0)!);
    } on BoundedProcessException catch (error) {
      return _UnityVersionProbeResult.failure(
        _boundedProcessFailure(
          'Unity version probe',
          exePath,
          error,
          _unityProbeTimeout,
          _unityProbeOutputLimit,
        ),
      );
    } on Object catch (error) {
      return _UnityVersionProbeResult.failure(
        'Unity version probe could not run for $exePath: '
        '${_uiBundleErrorMessage(error)}',
      );
    }
  }

  /// One headless Unity phase. No -quit (the builder methods exit explicitly; the essentials
  /// import is asynchronous and exits from its completion callback) and no -nographics (it breaks
  /// Shader.Find, which the TMP font baking needs). The bounded runner waits for the real editor
  /// process to exit on every OS. Output is drained concurrently under a shared bound;
  /// timeout and overflow both terminate the editor.
  Future<int> _runUiBundlePhase(
    String editorPath,
    String projectPath,
    String method,
    String logPath,
  ) async {
    stdout.writeln('Running Unity $method (log: $logPath)...');
    late final BoundedProcessResult run;
    try {
      run = await runBoundedProcess(
        editorPath,
        [
          '-batchmode',
          '-projectPath',
          projectPath,
          '-executeMethod',
          method,
          '-logFile',
          logPath,
        ],
        timeout: _unityPhaseTimeout,
        maxStdoutBytes: _unityPhaseOutputLimit ~/ 2,
        maxStderrBytes: _unityPhaseOutputLimit ~/ 2,
      );
    } on BoundedProcessException catch (error) {
      stderr.writeln(
        _boundedProcessFailure(
          'Unity phase $method',
          editorPath,
          error,
          _unityPhaseTimeout,
          _unityPhaseOutputLimit,
        ),
      );
      final output = _coalesceProcessOutput(error.stdout, error.stderr);
      if (output.isNotEmpty) {
        stderr.writeln('--- bounded Unity process output ---');
        stderr.writeln(output);
      }
      _writeUiBundleLogTail(logPath, 40);
      return 1;
    } on Object catch (error) {
      stderr.writeln(
        'Unity phase $method could not run: ${_uiBundleErrorMessage(error)}',
      );
      _writeUiBundleLogTail(logPath, 40);
      return 1;
    }

    if (run.exitCode != 0) {
      stderr.writeln(
        'Unity phase $method failed with exit code ${run.exitCode}.',
      );
      final output = _coalesceProcessOutput(run.stdout, run.stderr);
      if (output.isNotEmpty) {
        stderr.writeln('--- bounded Unity process output ---');
        stderr.writeln(output);
      }
      _writeUiBundleLogTail(logPath, 40);
      return run.exitCode > 0 && run.exitCode <= 255 ? run.exitCode : 1;
    }
    return 0;
  }

  Future<bool> _rebuildUiBundleAssembly(String repoRoot) async {
    late final BoundedProcessResult build;
    late final String dotnetExecutable;
    try {
      final dotnet = await resolveRepositoryDotnetSdk(Directory(repoRoot));
      dotnetExecutable = dotnet.executable;
    } on Object catch (error) {
      stderr.writeln(
        'dotnet build could not select the repository SDK: '
        '${_uiBundleErrorMessage(error)}',
      );
      return false;
    }
    try {
      build = await runBoundedProcess(
        dotnetExecutable,
        [
          'build',
          p.join(repoRoot, 'src', 'TopiaForge.Mods.UnityUi'),
          '-c',
          'Release',
        ],
        workingDirectory: repoRoot,
        timeout: _dotnetBuildTimeout,
        maxStdoutBytes: _unityPhaseOutputLimit ~/ 2,
        maxStderrBytes: _unityPhaseOutputLimit ~/ 2,
      );
    } on BoundedProcessException catch (error) {
      stderr.writeln(
        _boundedProcessFailure(
          'dotnet build',
          dotnetExecutable,
          error,
          _dotnetBuildTimeout,
          _unityPhaseOutputLimit,
        ),
      );
      final output = _coalesceProcessOutput(error.stdout, error.stderr);
      if (output.isNotEmpty) stderr.writeln(output);
      return false;
    } on Object catch (error) {
      stderr.writeln(
        'dotnet build could not run: ${_uiBundleErrorMessage(error)}',
      );
      return false;
    }
    if (build.exitCode != 0) {
      stderr.writeln('dotnet build failed with exit code ${build.exitCode}.');
      final output = _coalesceProcessOutput(build.stdout, build.stderr);
      if (output.isNotEmpty) stderr.writeln(output);
      return false;
    }
    return true;
  }

  void _writeUiBundleLogTail(String path, int count) {
    final tail = _uiBundleLogTail(path, count);
    if (tail.error != null) {
      stderr.writeln('Unity log tail unavailable: ${tail.error}');
    } else if (tail.lines.isNotEmpty) {
      stderr.writeln('--- last ${tail.lines.length} log lines ($path) ---');
      if (tail.truncated) {
        stderr.writeln(
          '  | ... bounded to the final ${_unityLogTailLimit ~/ 1024} KiB ...',
        );
      }
      for (final line in tail.lines) {
        stderr.writeln('  | $line');
      }
    }
    stderr.writeln('Full log: $path');
  }

  ({List<String> lines, String? error, bool truncated}) _uiBundleLogTail(
    String path,
    int count,
  ) {
    try {
      final file = File(path);
      final truncated = file.statSync().size > _unityLogTailLimit;
      final text = readBoundedTextFileTailSync(
        file,
        maxBytes: _unityLogTailLimit,
      );
      final lines = const LineSplitter().convert(text);
      return (
        lines: lines.length <= count
            ? lines
            : lines.sublist(lines.length - count),
        error: null,
        truncated: truncated,
      );
    } on Object catch (error) {
      return (
        lines: const <String>[],
        error: _uiBundleErrorMessage(error),
        truncated: false,
      );
    }
  }
}

class _UnityVersionProbeResult {
  const _UnityVersionProbeResult._({this.version, this.error});

  factory _UnityVersionProbeResult.success(String version) =>
      _UnityVersionProbeResult._(version: version);
  factory _UnityVersionProbeResult.failure(String message) =>
      _UnityVersionProbeResult._(error: message);

  final String? version;
  final String? error;
}

String _uiBundleErrorMessage(Object error) =>
    error is StateError ? error.message : error.toString();

String _coalesceProcessOutput(String stdoutText, String stderrText) => [
  stdoutText.trim(),
  stderrText.trim(),
].where((value) => value.isNotEmpty).join('\n');

String _boundedProcessFailure(
  String operation,
  String executable,
  BoundedProcessException error,
  Duration timeout,
  int combinedOutputLimit,
) => switch (error.failure) {
  BoundedProcessFailure.timeout =>
    '$operation timed out after ${timeout.inSeconds} seconds and was terminated: $executable.',
  BoundedProcessFailure.stdoutLimitExceeded ||
  BoundedProcessFailure.stderrLimitExceeded =>
    '$operation exceeded the ${combinedOutputLimit ~/ 1024} KiB combined output limit and was terminated: $executable.',
  BoundedProcessFailure.outputReadFailed =>
    '$operation output could not be read; the process was terminated: $executable.',
  BoundedProcessFailure.terminationFailed =>
    '$operation exceeded a safety bound and could not be terminated: $executable.',
};

const _unityProbeTimeout = Duration(seconds: 5);
const _unityProbeOutputLimit = 256 * 1024;
const _unityPhaseTimeout = Duration(minutes: 45);
const _dotnetBuildTimeout = Duration(minutes: 10);
const _unityPhaseOutputLimit = 4 * 1024 * 1024;
const _unityLogTailLimit = 256 * 1024;
