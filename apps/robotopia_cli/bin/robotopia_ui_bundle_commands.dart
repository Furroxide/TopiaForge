part of 'robotopia.dart';

/// `robotopia unity build-ui-bundle` — rebuilds the QuantumWorks brand AssetBundle
/// (quantumworks-ui.bundle) from the committed Unity project at tools/unity-ui-bundle. The Unity
/// side (Robotopia.UiBundleBuilder) copies the bundle into src/Robotopia.Mods.UnityUi/Assets and
/// writes its provenance manifest; the kit csproj embeds it into Robotopia.Mods.UnityUi.dll.
/// Cross-platform replacement for the retired tools/build-ui-bundle.ps1.
extension _RobotopiaUiBundleCommands on _RobotopiaCli {
  Future<int> _unityBuildUiBundle(List<String> args) async {
    final repoRoot = _findRepoRoot();
    if (repoRoot == null) {
      stderr.writeln(
        'The QuantumWorks repository root was not found from '
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
        'Build editor:  ${editor == null ? '(none eligible — need 6000.0.x, patch <= ${WorldBundleEditorGate.maxPatch})' : '${editor.version} at ${editor.path}'}',
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
        'Robotopia.UiBundleBuilder.ImportEssentials',
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
      'Robotopia.UiBundleBuilder.Build',
      buildLog,
    );
    if (code != 0) {
      return code;
    }

    final bundle = File(
      p.join(
        repoRoot,
        'src',
        'Robotopia.Mods.UnityUi',
        'Assets',
        'quantumworks-ui.bundle',
      ),
    );
    if (!bundle.existsSync()) {
      stderr.writeln(
        'Unity reported success but ${bundle.path} was not produced. Check $buildLog.',
      );
      return 1;
    }
    final bytes = bundle.readAsBytesSync();
    stdout.writeln('UI bundle written: ${bundle.path}');
    stdout.writeln(
      '  size:   ${(bytes.length / (1024 * 1024)).toStringAsFixed(2)} MB',
    );
    stdout.writeln('  sha256: ${sha256.convert(bytes)}');

    if (args.contains('--rebuild')) {
      if (!await _ensureBuildTooling()) {
        return 1;
      }
      stdout.writeln('Rebuilding Robotopia.Mods.UnityUi (Release)...');
      final build = await Process.run('dotnet', [
        'build',
        p.join(repoRoot, 'src', 'Robotopia.Mods.UnityUi'),
        '-c',
        'Release',
      ], workingDirectory: repoRoot);
      if (build.exitCode != 0) {
        stderr.writeln('${build.stdout}\n${build.stderr}'.trim());
        return 1;
      }
      stdout.writeln('Robotopia.Mods.UnityUi rebuilt with the new bundle.');
    } else {
      stdout.writeln(
        'Rebuild Robotopia.Mods.UnityUi so the embedded resource picks up the '
        'new bundle (or re-run with --rebuild).',
      );
    }
    return 0;
  }

  /// Picks the build editor: an explicit `--unity` path (gated on the version derived from its
  /// Hub-layout folder, or a best-effort `Unity -version` probe), else the newest eligible editor
  /// from the cross-platform Hub scan. Bundles must come from the game player's editor stream —
  /// see [WorldBundleEditorGate]. Prints the failure reason and returns null when nothing fits.
  Future<UnityEditor?> _resolveUiBundleEditor(String? explicitPath) async {
    if (explicitPath != null) {
      if (!File(explicitPath).existsSync()) {
        stderr.writeln('Unity editor not found at $explicitPath.');
        return null;
      }
      var version = _uiBundleVersionFromEditorPath(explicitPath);
      if (version.isEmpty) {
        version = await _probeUnityVersion(explicitPath);
      }
      if (version.isEmpty) {
        stdout.writeln(
          'Warning: could not determine the editor version of $explicitPath — '
          'proceeding unverified (required: 6000.0.x with patch <= '
          '${WorldBundleEditorGate.maxPatch}).',
        );
        return UnityEditor(version: 'unknown', path: explicitPath);
      }
      if (!WorldBundleEditorGate.isEligible(version)) {
        stderr.writeln(
          'Editor at $explicitPath reports version "$version" — required: '
          '6000.0.x with patch <= ${WorldBundleEditorGate.maxPatch} (the '
          'Robotopia player is 6000.0.31f1; bundles serialized by newer '
          'editor streams are not safe to load in it).',
        );
        return null;
      }
      return UnityEditor(version: version, path: explicitPath);
    }

    final editors = await developerRepository.listUnityEditors();
    // Scan results are sorted newest-first, so the first eligible hit is the highest eligible patch.
    for (final editor in editors) {
      if (WorldBundleEditorGate.isEligible(editor.version)) {
        return editor;
      }
    }
    stderr.writeln(
      'No eligible Unity editor found (required: 6000.0.x with patch <= '
      '${WorldBundleEditorGate.maxPatch}). ${WorldBundleEditorGate.installHint}',
    );
    return null;
  }

  /// `…/Hub/Editor/<version>/Editor/Unity(.exe)` or `…/<version>/Unity.app/Contents/MacOS/Unity`
  /// → `<version>`; empty when the path does not look like a Hub layout.
  String _uiBundleVersionFromEditorPath(String exePath) {
    var dir = File(exePath).parent;
    for (var i = 0; i < 3 && dir.path != dir.parent.path; i++) {
      final name = p.basename(dir.path);
      if (RegExp(r'^\d').hasMatch(name)) {
        return name;
      }
      dir = dir.parent;
    }
    return '';
  }

  /// Best-effort `Unity -version` probe (prints the version and exits); empty on any failure.
  Future<String> _probeUnityVersion(String exePath) async {
    try {
      final run = await Process.run(exePath, ['-version']);
      final match = RegExp(
        r'\d+\.\d+\.\d+[a-z]\d+',
      ).firstMatch('${run.stdout}\n${run.stderr}');
      return match?.group(0) ?? '';
    } on Object {
      return '';
    }
  }

  /// One headless Unity phase. No -quit (the builder methods exit explicitly; the essentials
  /// import is asynchronous and exits from its completion callback) and no -nographics (it breaks
  /// Shader.Find, which the TMP font baking needs). Process.run waits for the real editor process
  /// to exit on every OS.
  Future<int> _runUiBundlePhase(
    String editorPath,
    String projectPath,
    String method,
    String logPath,
  ) async {
    stdout.writeln('Running Unity $method (log: $logPath)...');
    final run = await Process.run(editorPath, [
      '-batchmode',
      '-projectPath',
      projectPath,
      '-executeMethod',
      method,
      '-logFile',
      logPath,
    ]);
    if (run.exitCode != 0) {
      stderr.writeln(
        'Unity phase $method failed with exit code ${run.exitCode}.',
      );
      final tail = _uiBundleLogTail(logPath, 40);
      if (tail.isNotEmpty) {
        stderr.writeln('--- last ${tail.length} log lines ($logPath) ---');
        for (final line in tail) {
          stderr.writeln('  | $line');
        }
      }
      stderr.writeln('Full log: $logPath');
      return run.exitCode;
    }
    return 0;
  }

  List<String> _uiBundleLogTail(String path, int count) {
    try {
      final lines = File(path).readAsLinesSync();
      return lines.length <= count
          ? lines
          : lines.sublist(lines.length - count);
    } on Object {
      return const <String>[];
    }
  }
}
