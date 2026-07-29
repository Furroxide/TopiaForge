part of '../local_developer_repository.dart';

/// Custom-world authoring: the `topiaforge.world.json` pairing config between a Unity world project and the
/// mod that ships its bundle, plus the headless Unity build that turns the world prefab into an AssetBundle
/// inside that mod. The headless invocation mirrors `topiaforge unity build-ui-bundle` (the brand-font bundle build):
/// `-batchmode -executeMethod` against the exact game-player editor, no `-quit` (the entry
/// point exits explicitly), no `-nographics` (HDRP shader access).
extension LocalDeveloperWorldAuthoring on LocalDeveloperRepository {
  static const String _worldBuilderEntryPoint =
      'TopiaForge.WorldCompanion.Editor.WorldBundleBuilder.Build';

  Future<WorldAuthoringConfig?> _readWorldAuthoringConfig(
    String unityProjectPath,
  ) async {
    final file = File(p.join(unityProjectPath, WorldAuthoringConfig.fileName));
    _recoverDeveloperAtomicBackupIfMissing(file);
    if (!file.existsSync()) {
      return null;
    }
    final decoded = jsonDecode(
      utf8.decode(
        await _readDeveloperFileBounded(
          file,
          maxBytes: _maxDeveloperManifestBytes,
          label: WorldAuthoringConfig.fileName,
        ),
      ),
    );
    if (decoded is! Map) {
      throw StateError(
        '${WorldAuthoringConfig.fileName} in $unityProjectPath is not a JSON object.',
      );
    }
    final config = WorldAuthoringConfig.fromJson(
      decoded.cast<String, Object?>(),
    );
    if (config.schemaVersion != 2) {
      throw const FormatException(
        'topiaforge.world.json must use schemaVersion 2.',
      );
    }
    if (config.worldId.isNotEmpty && !ModManifest.isValidId(config.worldId)) {
      throw const FormatException(
        'topiaforge.world.json worldId must use the safe TopiaForge id format.',
      );
    }
    return config;
  }

  Future<WorldAuthoringConfig> _writeWorldAuthoringConfig(
    String unityProjectPath,
    WorldAuthoringConfig config,
  ) async {
    final dir = Directory(unityProjectPath);
    if (!dir.existsSync()) {
      throw StateError('Unity project does not exist: $unityProjectPath');
    }
    if (config.schemaVersion != 2) {
      throw const FormatException(
        'topiaforge.world.json must use schemaVersion 2.',
      );
    }
    if (config.worldId.isNotEmpty && !ModManifest.isValidId(config.worldId)) {
      throw const FormatException(
        'topiaforge.world.json worldId must use the safe TopiaForge id format.',
      );
    }
    _writeDeveloperTextAtomic(
      File(p.join(unityProjectPath, WorldAuthoringConfig.fileName)),
      _prettyJson(config.toJson()),
    );
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
    return p.normalize(p.isAbsolute(raw) ? raw : p.join(unityProjectPath, raw));
  }

  Future<UnityEditor?> _pickWorldBuildEditor(
    List<UnityEditor> editors,
    String unityExePath,
  ) async {
    if (unityExePath.isNotEmpty) {
      final version = await _probeUnityEditorVersion(unityExePath);
      return WorldBundleEditorGate.isEligible(version)
          ? UnityEditor(version: version, path: unityExePath)
          : null;
    }
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
            '`topiaforge world link --project <unityProj> --mod <modDir>`.',
      );
    }
    if (!File(p.join(resolvedModPath, 'topiaforge.mod.json')).existsSync()) {
      return WorldBundleBuildResult(
        success: false,
        errorMessage:
            '$resolvedModPath is not a mod directory (no topiaforge.mod.json).',
      );
    }

    final effectiveBundleName = bundleName.isNotEmpty
        ? bundleName
        : (config?.bundleName ?? '');
    if (effectiveBundleName.isEmpty) {
      return const WorldBundleBuildResult(
        success: false,
        errorMessage:
            'No bundle name: pass --bundle or set bundleName in topiaforge.world.json.',
      );
    }

    final projectVersion = _readUnityVersion(Directory(projectRoot));
    if (!WorldBundleEditorGate.isEligible(projectVersion)) {
      return WorldBundleBuildResult(
        success: false,
        errorMessage:
            '$projectRoot is pinned to ${projectVersion.isEmpty ? 'an unknown Unity version' : 'Unity $projectVersion'}; '
            'TopiaForge world bundles require Unity '
            '${RobotopiaGameUnityCompatibility.requiredEditorDisplay}.',
      );
    }
    if (unityExePath.isNotEmpty && !File(unityExePath).existsSync()) {
      return WorldBundleBuildResult(
        success: false,
        errorMessage: 'Selected Unity editor does not exist: $unityExePath',
      );
    }

    final editor = await _pickWorldBuildEditor(
      unityExePath.isEmpty ? await _scanUnityEditors() : const [],
      unityExePath,
    );
    if (editor == null) {
      return const WorldBundleBuildResult(
        success: false,
        errorMessage:
            'No eligible Unity editor: world bundles must be built with Unity '
            '${RobotopiaGameUnityCompatibility.requiredEditorDisplay}. '
            '${WorldBundleEditorGate.installHint}',
      );
    }

    final logPath = p.join(projectRoot, 'Logs', 'topiaforge-world-build.log');
    Directory(p.dirname(logPath)).createSync(recursive: true);
    final arguments = <String>[
      '-batchmode',
      '-projectPath',
      projectRoot,
      '-executeMethod',
      _worldBuilderEntryPoint,
      '-logFile',
      logPath,
      '-topiaForgeModPath',
      resolvedModPath,
      '-topiaForgeBundleName',
      effectiveBundleName,
      if (config != null && config.worldPrefab.isNotEmpty) ...[
        '-topiaForgeWorldPrefab',
        config.worldPrefab,
      ],
    ];
    final run = await runBoundedProcess(
      editor.path,
      arguments,
      timeout: const Duration(minutes: 30),
      maxStdoutBytes: 8 * 1024 * 1024,
      maxStderrBytes: 8 * 1024 * 1024,
    );

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

    final attestation = await _attestBuiltWorldBundle(
      modPath: resolvedModPath,
      bundleName: effectiveBundleName,
      worldPrefab: config?.worldPrefab.isNotEmpty == true
          ? config!.worldPrefab
          : WorldAuthoringConfig.defaultWorldPrefab,
    );
    if (attestation == null) {
      return WorldBundleBuildResult(
        success: false,
        editorPath: editor.path,
        editorVersion: editor.version,
        logPath: logPath,
        errorMessage:
            'Unity reported success but the world bundle provenance did not validate. See $logPath.',
        logTail: _tailLines(logPath, 40),
      );
    }

    return WorldBundleBuildResult(
      success: true,
      bundlePath: attestation.bundlePath,
      sha256: attestation.sha256,
      sizeBytes: attestation.sizeBytes,
      editorPath: editor.path,
      editorVersion: editor.version,
      logPath: logPath,
    );
  }

  Future<WorldBundleAttestation?> _attestBuiltWorldBundle({
    required String modPath,
    required String bundleName,
    required String worldPrefab,
  }) async {
    try {
      return attestWorldBundleOutput(
        modPath: modPath,
        bundleName: bundleName,
        worldPrefab: worldPrefab,
      );
    } on Object {
      return null;
    }
  }

  List<String> _tailLines(String path, int count) {
    try {
      if (count <= 0 ||
          FileSystemEntity.typeSync(path, followLinks: false) !=
              FileSystemEntityType.file) {
        return const <String>[];
      }
      final input = File(path).openSync();
      late final String text;
      try {
        final length = input.lengthSync();
        final start = length > _maxWorldBuildLogTailBytes
            ? length - _maxWorldBuildLogTailBytes
            : 0;
        input.setPositionSync(start);
        var decoded = utf8.decode(
          input.readSync(length - start),
          allowMalformed: true,
        );
        if (start > 0) {
          final newline = decoded.indexOf('\n');
          decoded = newline < 0 ? '' : decoded.substring(newline + 1);
        }
        text = decoded;
      } finally {
        input.closeSync();
      }
      final lines = const LineSplitter().convert(text);
      return lines.length <= count
          ? lines
          : lines.sublist(lines.length - count);
    } on Object {
      return const <String>[];
    }
  }
}

const _maxWorldBuildLogTailBytes = 1024 * 1024;
