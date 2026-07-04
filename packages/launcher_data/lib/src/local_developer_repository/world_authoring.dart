part of '../local_developer_repository.dart';

/// Custom-world authoring: the `robotopia.world.json` pairing config between a Unity world project and the
/// mod that ships its bundle, plus the headless Unity build that turns the world prefab into an AssetBundle
/// inside that mod. The headless invocation mirrors `robotopia unity build-ui-bundle` (the brand-font bundle build):
/// `-batchmode -executeMethod` against an editor from the game player's Unity stream, no `-quit` (the entry
/// point exits explicitly), no `-nographics` (HDRP shader access).
extension LocalDeveloperWorldAuthoring on LocalDeveloperRepository {
  static const String _worldBuilderEntryPoint =
      'Robotopia.WorldCompanion.Editor.WorldBundleBuilder.Build';

  Future<WorldAuthoringConfig?> _readWorldAuthoringConfig(
    String unityProjectPath,
  ) async {
    final file = File(
      p.join(unityProjectPath, WorldAuthoringConfig.fileName),
    );
    if (!file.existsSync()) {
      return null;
    }
    final decoded = jsonDecode(await file.readAsString());
    if (decoded is! Map) {
      throw StateError(
        '${WorldAuthoringConfig.fileName} in $unityProjectPath is not a JSON object.',
      );
    }
    return WorldAuthoringConfig.fromJson(decoded.cast<String, Object?>());
  }

  Future<WorldAuthoringConfig> _writeWorldAuthoringConfig(
    String unityProjectPath,
    WorldAuthoringConfig config,
  ) async {
    final dir = Directory(unityProjectPath);
    if (!dir.existsSync()) {
      throw StateError('Unity project does not exist: $unityProjectPath');
    }
    await File(
      p.join(unityProjectPath, WorldAuthoringConfig.fileName),
    ).writeAsString(_prettyJson(config.toJson()));
    return config;
  }

  /// Resolves the paired mod directory from config + override; empty when neither names one.
  String _resolveWorldModPath(
    String unityProjectPath,
    WorldAuthoringConfig? config,
    String modPathOverride,
  ) {
    final raw = modPathOverride.isNotEmpty
        ? modPathOverride
        : (config?.modPath ?? '');
    if (raw.isEmpty) {
      return '';
    }
    return p.normalize(
      p.isAbsolute(raw) ? raw : p.join(unityProjectPath, raw),
    );
  }

  UnityEditor? _pickWorldBuildEditor(
    List<UnityEditor> editors,
    String unityExePath,
  ) {
    if (unityExePath.isNotEmpty) {
      return UnityEditor(
        version: _versionFromEditorPath(unityExePath),
        path: unityExePath,
      );
    }
    // Scan results are sorted newest-first, so the first eligible hit is the highest eligible patch.
    for (final editor in editors) {
      if (WorldBundleEditorGate.isEligible(editor.version)) {
        return editor;
      }
    }
    return null;
  }

  Future<WorldBundleBuildResult> _buildWorldBundle({
    required String unityProjectPath,
    String modPath = '',
    String bundleName = '',
    String unityExePath = '',
  }) async {
    final projectRoot = p.normalize(p.absolute(unityProjectPath));
    if (!Directory(p.join(projectRoot, 'Assets')).existsSync() ||
        !Directory(p.join(projectRoot, 'ProjectSettings')).existsSync()) {
      return WorldBundleBuildResult(
        success: false,
        errorMessage:
            '$projectRoot is not a Unity project (no Assets/ + ProjectSettings/).',
      );
    }

    final config = await _readWorldAuthoringConfig(projectRoot);
    final resolvedModPath = _resolveWorldModPath(projectRoot, config, modPath);
    if (resolvedModPath.isEmpty) {
      return const WorldBundleBuildResult(
        success: false,
        errorMessage:
            'No paired mod: pass --mod, or pair the project once with '
            '`robotopia world link --project <unityProj> --mod <modDir>`.',
      );
    }
    if (!File(
      p.join(resolvedModPath, 'robotopia.mod.json'),
    ).existsSync()) {
      return WorldBundleBuildResult(
        success: false,
        errorMessage:
            '$resolvedModPath is not a mod directory (no robotopia.mod.json).',
      );
    }

    final effectiveBundleName = bundleName.isNotEmpty
        ? bundleName
        : (config?.bundleName ?? '');
    if (effectiveBundleName.isEmpty) {
      return const WorldBundleBuildResult(
        success: false,
        errorMessage:
            'No bundle name: pass --bundle or set bundleName in robotopia.world.json.',
      );
    }

    final editor = _pickWorldBuildEditor(await _scanUnityEditors(), unityExePath);
    if (editor == null) {
      return const WorldBundleBuildResult(
        success: false,
        errorMessage:
            'No eligible Unity editor: world bundles must be built with 6000.0.x '
            '(patch <= ${WorldBundleEditorGate.maxPatch}) to match the game player. '
            '${WorldBundleEditorGate.installHint}',
      );
    }

    final logPath = p.join(projectRoot, 'Logs', 'robotopia-world-build.log');
    Directory(p.dirname(logPath)).createSync(recursive: true);
    final arguments = <String>[
      '-batchmode',
      '-projectPath',
      projectRoot,
      '-executeMethod',
      _worldBuilderEntryPoint,
      '-logFile',
      logPath,
      '-robotopiaModPath',
      resolvedModPath,
      '-robotopiaBundleName',
      effectiveBundleName,
      if (config != null && config.worldPrefab.isNotEmpty) ...[
        '-robotopiaWorldPrefab',
        config.worldPrefab,
      ],
    ];
    final run = await Process.run(editor.path, arguments);

    if (run.exitCode != 0) {
      return WorldBundleBuildResult(
        success: false,
        editorPath: editor.path,
        editorVersion: editor.version,
        logPath: logPath,
        errorMessage:
            'Unity exited with code ${run.exitCode}. See $logPath for details.',
        logTail: _tailLines(logPath, 40),
      );
    }

    final bundleFile = File(
      p.join(resolvedModPath, 'AssetBundles', '$effectiveBundleName.bundle'),
    );
    if (!bundleFile.existsSync()) {
      return WorldBundleBuildResult(
        success: false,
        editorPath: editor.path,
        editorVersion: editor.version,
        logPath: logPath,
        errorMessage:
            'Unity reported success but ${bundleFile.path} was not produced. See $logPath.',
        logTail: _tailLines(logPath, 40),
      );
    }

    final bytes = bundleFile.readAsBytesSync();
    return WorldBundleBuildResult(
      success: true,
      bundlePath: bundleFile.path,
      sha256: sha256.convert(bytes).toString(),
      sizeBytes: bytes.length,
      editorPath: editor.path,
      editorVersion: editor.version,
      logPath: logPath,
    );
  }

  List<String> _tailLines(String path, int count) {
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
