part of '../local_developer_repository.dart';

typedef UnityEditorScanner = Future<List<UnityEditor>> Function();
typedef UnityEditorVersionProbe = Future<String> Function(String executable);
typedef UnityEditorLauncher =
    Future<void> Function(String executable, List<String> arguments);

/// VCC-style multi-project registry + Unity editor detection. The registry (`developer_projects.json` at the
/// launcher data root) holds only metadata + a path; each project's own files stay the source of truth. Unity
/// detection is detect-only — the launcher never installs Unity.
extension LocalDeveloperProjectRegistry on LocalDeveloperRepository {
  File get _projectsFile =>
      File(p.join(_dataRoot.path, 'developer_projects.json'));

  // Canonical key for dedupe/lookup (lowercases the drive + normalizes separators on Windows).
  String _canonicalKey(String path) => p.canonicalize(path);

  Future<List<RegisteredProject>> _readRegistry() async {
    final file = _projectsFile;
    _recoverDeveloperAtomicBackupIfMissing(file);
    if (!file.existsSync()) {
      return <RegisteredProject>[];
    }
    try {
      final decoded = jsonDecode(
        utf8.decode(
          await _readDeveloperFileBounded(
            file,
            maxBytes: _maxDeveloperCatalogBytes,
            label: 'Developer project registry',
          ),
        ),
      );
      final list = decoded is Map ? decoded['projects'] : null;
      if (list is! List) {
        return <RegisteredProject>[];
      }
      return list
          .whereType<Map>()
          .map(
            (item) => RegisteredProject.fromJson(item.cast<String, Object?>()),
          )
          .where((project) => project.path.isNotEmpty)
          .toList();
    } on Object {
      return <RegisteredProject>[];
    }
  }

  Future<void> _writeRegistry(List<RegisteredProject> projects) async {
    final json = _prettyJson({
      'projects': projects.map((project) => project.toJson()).toList(),
    });
    _writeDeveloperTextAtomic(_projectsFile, json);
  }

  Future<List<RegisteredProject>> _registerProject(String path) async {
    final normalized = p.normalize(p.absolute(path));
    final dir = Directory(normalized);
    if (!dir.existsSync()) {
      throw StateError('Project directory does not exist: $normalized');
    }
    final kind = _detectProjectKind(dir);
    if (kind == ProjectKind.unknown) {
      throw StateError(
        'Not a recognized project: $normalized (expected robotopia.project.json, '
        'Packages/vpm-manifest.json, or package.json).',
      );
    }

    final projects = await _readRegistry();
    final key = _canonicalKey(normalized);
    final existing = projects
        .where((project) => _canonicalKey(project.path) == key)
        .toList();
    final lastOpened = existing.isEmpty ? '' : existing.first.lastOpenedUtc;
    projects.removeWhere((project) => _canonicalKey(project.path) == key);
    projects.add(
      RegisteredProject(
        path: normalized,
        name: _readProjectName(dir, kind),
        kind: kind,
        unityVersion: _readUnityVersion(dir),
        lastOpenedUtc: lastOpened,
      ),
    );
    await _writeRegistry(projects);
    return projects;
  }

  // Instantiates a new Unity world project from templates/Robotopia.UnityWorldTemplate: copies the template,
  // installs the UGC companion package into Packages/, stamps the name, and registers it. Mirrors VCC's
  // copy-template-then-resolve flow. The embedded package is a read-only recovery bridge; archive/network
  // restore remains behind this repository's hardened VPM implementation.
  Future<List<RegisteredProject>> _createUnityProject({
    required String parentDirectory,
    required String name,
    String template = 'world',
  }) async {
    if (template != 'world') {
      throw StateError(
        'Unknown Unity template "$template" (only "world" is available).',
      );
    }
    final templateDir = Directory(
      p.join(_repositoryRoot.path, 'templates', 'Robotopia.UnityWorldTemplate'),
    );
    if (FileSystemEntity.typeSync(templateDir.path, followLinks: false) !=
        FileSystemEntityType.directory) {
      throw StateError(
        'Unity world template not found at ${templateDir.path}.',
      );
    }

    final parent = Directory(parentDirectory)..createSync(recursive: true);
    if (FileSystemEntity.typeSync(parent.path, followLinks: false) !=
        FileSystemEntityType.directory) {
      throw StateError(
        'Project parent is not a regular directory: ${parent.path}',
      );
    }
    final root = Directory(p.join(parent.path, _safeName(name)));
    if (FileSystemEntity.typeSync(root.path, followLinks: false) !=
        FileSystemEntityType.notFound) {
      throw StateError('Project already exists: ${root.path}');
    }
    final staging = Directory(
      '${root.path}.robotopia-new-$pid-'
      '${DateTime.now().microsecondsSinceEpoch}',
    );
    var installed = false;
    try {
      _copyDirectory(templateDir, staging, excludeUnityGenerated: true);

      // Install the same authored companion package used by mod scaffolds.
      await _ensureUgcCompanionPackage(staging.path);

      final readme = File(p.join(staging.path, 'README.md'));
      if (FileSystemEntity.typeSync(readme.path, followLinks: false) ==
          FileSystemEntityType.file) {
        try {
          final lines = const LineSplitter().convert(
            utf8.decode(
              _readDeveloperFileBoundedSync(
                readme,
                maxBytes: _maxDeveloperManifestBytes,
                label: 'Unity project README',
              ),
            ),
          );
          if (lines.isNotEmpty && lines.first.startsWith('# ')) {
            lines[0] = '# $name — Robotopia UGC World';
            _writeDeveloperTextAtomic(readme, '${lines.join('\n')}\n');
          }
        } on Object {
          // Cosmetic only; a valid project does not depend on README text.
        }
      }

      if (FileSystemEntity.typeSync(root.path, followLinks: false) !=
          FileSystemEntityType.notFound) {
        throw StateError(
          'Project target appeared while scaffolding: ${root.path}',
        );
      }
      staging.renameSync(root.path);
      installed = true;
      return await _registerProject(root.path);
    } on Object {
      if (staging.existsSync()) {
        staging.deleteSync(recursive: true);
      }
      if (installed &&
          FileSystemEntity.typeSync(root.path, followLinks: false) ==
              FileSystemEntityType.directory) {
        root.deleteSync(recursive: true);
      }
      rethrow;
    }
  }

  Future<List<RegisteredProject>> _unregisterProject(String path) async {
    final key = _canonicalKey(p.normalize(p.absolute(path)));
    final projects = await _readRegistry();
    projects.removeWhere((project) => _canonicalKey(project.path) == key);
    await _writeRegistry(projects);
    return projects;
  }

  Future<List<RegisteredProject>> _touchProject(String path) async {
    final key = _canonicalKey(p.normalize(p.absolute(path)));
    final now = DateTime.now().toUtc().toIso8601String();
    final projects = [
      for (final project in await _readRegistry())
        if (_canonicalKey(project.path) == key)
          project.copyWith(lastOpenedUtc: now)
        else
          project,
    ];
    await _writeRegistry(projects);
    return projects;
  }

  ProjectKind _detectProjectKind(Directory dir) {
    if (File(p.join(dir.path, 'robotopia.project.json')).existsSync()) {
      return ProjectKind.modCSharp;
    }
    if (File(p.join(dir.path, 'Packages', 'vpm-manifest.json')).existsSync()) {
      return ProjectKind.unityWorld;
    }
    if (File(p.join(dir.path, 'package.json')).existsSync()) {
      return ProjectKind.unityPackage;
    }
    // A Unity project that hasn't been VPM-initialised yet still has these two folders.
    if (Directory(p.join(dir.path, 'ProjectSettings')).existsSync() &&
        Directory(p.join(dir.path, 'Assets')).existsSync()) {
      return ProjectKind.unityWorld;
    }
    return ProjectKind.unknown;
  }

  String _readProjectName(Directory dir, ProjectKind kind) {
    try {
      if (kind == ProjectKind.modCSharp) {
        final file = File(p.join(dir.path, 'robotopia.project.json'));
        if (file.existsSync()) {
          final decoded = jsonDecode(
            utf8.decode(
              _readDeveloperFileBoundedSync(
                file,
                maxBytes: _maxDeveloperManifestBytes,
                label: 'robotopia.project.json',
              ),
            ),
          );
          if (decoded is Map && decoded['name'] is String) {
            final name = (decoded['name'] as String).trim();
            if (name.isNotEmpty) return name;
          }
        }
      } else if (kind == ProjectKind.unityPackage) {
        final file = File(p.join(dir.path, 'package.json'));
        if (file.existsSync()) {
          final decoded = jsonDecode(
            utf8.decode(
              _readDeveloperFileBoundedSync(
                file,
                maxBytes: _maxDeveloperManifestBytes,
                label: 'Unity package.json',
              ),
            ),
          );
          if (decoded is Map) {
            final name =
                ((decoded['displayName'] ?? decoded['name']) as String?)
                    ?.trim();
            if (name != null && name.isNotEmpty) return name;
          }
        }
      }
    } on Object {
      // Fall through to the folder name on any parse error.
    }
    return p.basename(dir.path);
  }

  // Reads the project's required Unity version from ProjectSettings/ProjectVersion.txt.
  // UPM package.json `unity` is package API metadata, not a Robotopia editor pin.
  String _readUnityVersion(Directory dir) {
    final versionFile = File(
      p.join(dir.path, 'ProjectSettings', 'ProjectVersion.txt'),
    );
    if (versionFile.existsSync()) {
      final lines = const LineSplitter().convert(
        utf8.decode(
          _readDeveloperFileBoundedSync(
            versionFile,
            maxBytes: _maxDeveloperManifestBytes,
            label: 'Unity ProjectVersion.txt',
          ),
        ),
      );
      for (final line in lines) {
        final trimmed = line.trim();
        if (trimmed.startsWith('m_EditorVersion:')) {
          return trimmed.substring('m_EditorVersion:'.length).trim();
        }
      }
    }
    return '';
  }

  // Detect-only scan of installed Unity editors via Unity Hub install roots. Windows-first (the game is
  // Windows); a best-effort macOS/Linux fallback is included. Sorted newest-first.
  Future<List<UnityEditor>> _scanUnityEditors() async {
    final scanner = _unityEditorScanner;
    if (scanner != null) {
      return scanner();
    }

    final editorPaths = <String>{};

    void consider(String exePath) {
      if (!File(exePath).existsSync()) return;
      editorPaths.add(p.canonicalize(exePath));
    }

    void scanHubRoot(String root, String exeRelative) {
      final dir = Directory(root);
      if (!dir.existsSync()) return;
      for (final entry in dir.listSync().whereType<Directory>()) {
        consider(p.join(entry.path, exeRelative));
      }
    }

    if (Platform.isWindows) {
      for (final base in [
        Platform.environment['ProgramFiles'],
        Platform.environment['ProgramW6432'],
      ]) {
        if (base != null && base.isNotEmpty) {
          scanHubRoot(
            p.join(base, 'Unity', 'Hub', 'Editor'),
            p.join('Editor', 'Unity.exe'),
          );
        }
      }
      // Unity Hub's user-chosen secondary install location.
      final appData = Platform.environment['APPDATA'];
      if (appData != null && appData.isNotEmpty) {
        final secondaryFile = File(
          p.join(appData, 'UnityHub', 'secondaryInstallPath.json'),
        );
        if (secondaryFile.existsSync()) {
          try {
            final decoded = jsonDecode(
              utf8.decode(
                _readDeveloperFileBoundedSync(
                  secondaryFile,
                  maxBytes: _maxDeveloperManifestBytes,
                  label: 'Unity Hub secondary install path',
                ),
              ),
            );
            if (decoded is String && decoded.trim().isNotEmpty) {
              scanHubRoot(decoded.trim(), p.join('Editor', 'Unity.exe'));
            }
          } on Object {
            // ignore a malformed secondary-path file
          }
        }
      }
    } else if (Platform.isMacOS) {
      scanHubRoot(
        '/Applications/Unity/Hub/Editor',
        p.join('Unity.app', 'Contents', 'MacOS', 'Unity'),
      );
    } else {
      final home = Platform.environment['HOME'];
      if (home != null && home.isNotEmpty) {
        scanHubRoot(
          p.join(home, 'Unity', 'Hub', 'Editor'),
          p.join('Editor', 'Unity'),
        );
      }
    }

    final onPath = await _which(Platform.isWindows ? 'Unity.exe' : 'Unity');
    if (onPath.isNotEmpty) {
      consider(onPath);
    }

    // Include an explicit override even when it is outside a Unity Hub install root.
    final override = Platform.environment['UNITY_EDITOR_PATH'];
    if (override != null &&
        override.isNotEmpty &&
        File(override).existsSync()) {
      editorPaths.add(p.canonicalize(override));
    }

    final editors = <UnityEditor>[];
    for (final path in editorPaths) {
      final version = await _probeUnityEditorVersion(path);
      if (version.isNotEmpty) {
        editors.add(UnityEditor(version: version, path: path));
      }
    }
    editors.sort((a, b) => _compareUnityVersions(b.version, a.version));
    return editors;
  }

  Future<String> _probeUnityEditorVersion(String executable) async {
    try {
      final configured = _unityEditorVersionProbe;
      final output = configured != null
          ? await configured(
              executable,
            ).timeout(_unityEditorProbeTimeout, onTimeout: () => '')
          : await _runUnityEditorVersionProbe(executable);
      return RegExp(
            r'\b\d+\.\d+\.\d+[abfp]\d+\b',
          ).firstMatch(output)?.group(0) ??
          '';
    } on Object {
      return '';
    }
  }

  Future<String> _runUnityEditorVersionProbe(String executable) async {
    final result = await runBoundedProcess(executable, const [
      '-version',
    ], timeout: _unityEditorProbeTimeout);
    if (result.exitCode != 0) {
      return '';
    }
    return '${result.stdout}\n${result.stderr}';
  }

  // Numeric-aware compare of Unity version strings (e.g. 6000.0.23f1 vs 2022.3.10f1) by their digit runs.
  // Versions with no digit runs (e.g. a 'custom' UNITY_EDITOR_PATH override) sort lowest.
  int _compareUnityVersions(String a, String b) {
    final an = RegExp(
      r'\d+',
    ).allMatches(a).map((m) => int.parse(m[0]!)).toList();
    final bn = RegExp(
      r'\d+',
    ).allMatches(b).map((m) => int.parse(m[0]!)).toList();
    if (an.isEmpty || bn.isEmpty) {
      if (an.isEmpty && bn.isEmpty) return a.compareTo(b);
      return an.isEmpty ? -1 : 1;
    }
    for (var i = 0; i < an.length && i < bn.length; i++) {
      final cmp = an[i].compareTo(bn[i]);
      if (cmp != 0) return cmp;
    }
    final lengthCmp = an.length.compareTo(bn.length);
    return lengthCmp != 0 ? lengthCmp : a.compareTo(b);
  }

  Future<String> _openInUnity(String projectPath) async {
    final normalized = p.normalize(p.absolute(projectPath));
    final dir = Directory(normalized);
    if (!dir.existsSync()) {
      throw StateError('Project directory does not exist: $normalized');
    }

    final required = _readUnityVersion(dir);
    if (required.isEmpty) {
      throw StateError(
        '$normalized is not a Robotopia Unity authoring project: '
        'ProjectSettings/ProjectVersion.txt was not found or does not contain m_EditorVersion.',
      );
    }
    if (required != RobotopiaUnityCompatibility.requiredEditorVersion) {
      throw StateError(
        '$normalized is pinned to Unity $required, but Robotopia authoring '
        'requires Unity ${RobotopiaUnityCompatibility.requiredEditorDisplay}.',
      );
    }

    final editors = await _scanUnityEditors();
    UnityEditor? chosen;
    for (final editor in editors) {
      if (editor.version == RobotopiaUnityCompatibility.requiredEditorVersion) {
        chosen = editor;
        break;
      }
    }
    if (chosen == null) {
      final detected = editors.isEmpty
          ? 'none detected'
          : editors.map((editor) => editor.version).join(', ');
      throw StateError(
        'Unity ${RobotopiaUnityCompatibility.requiredEditorDisplay} is required '
        'to open Robotopia Unity projects. Detected editors: $detected. '
        '${RobotopiaUnityCompatibility.installHint}',
      );
    }

    await _startUnityEditor(chosen.path, ['-projectPath', normalized]);
    await _touchProject(normalized);
    return chosen.path;
  }

  Future<void> _startUnityEditor(
    String executable,
    List<String> arguments,
  ) async {
    final launcher = _unityEditorLauncher;
    if (launcher != null) {
      await launcher(executable, arguments);
      return;
    }
    await Process.start(executable, arguments, mode: ProcessStartMode.detached);
  }
}
