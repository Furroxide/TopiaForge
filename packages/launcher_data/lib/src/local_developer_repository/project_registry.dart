part of '../local_developer_repository.dart';

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
    if (!file.existsSync()) {
      return <RegisteredProject>[];
    }
    try {
      final decoded = jsonDecode(await file.readAsString());
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
    if (!_dataRoot.existsSync()) {
      _dataRoot.createSync(recursive: true);
    }
    // Atomic write (temp + rename) so a crash mid-write never truncates the registry.
    final json = _prettyJson({
      'projects': projects.map((project) => project.toJson()).toList(),
    });
    final temp = File('${_projectsFile.path}.tmp');
    await temp.writeAsString(json);
    if (_projectsFile.existsSync()) {
      await _projectsFile.delete();
    }
    await temp.rename(_projectsFile.path);
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
  // copy-template-then-resolve flow (the launcher-side VPM resolver fills locked versions in Phase 4).
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
    if (!templateDir.existsSync()) {
      throw StateError(
        'Unity world template not found at ${templateDir.path}.',
      );
    }

    final root = Directory(p.join(parentDirectory, _safeName(name)));
    if (root.existsSync()) {
      throw StateError('Project already exists: ${root.path}');
    }
    _copyDirectory(templateDir, root);

    // Install the UGC companion package (the same one createModProject scaffolds) into the project's Packages/.
    await _ensureUgcCompanionPackage(root.path);

    // Point the embedded resolver at the local QuantumWorks listing so a cloned copy self-heals offline.
    File(
      p.join(root.path, 'Packages', 'vpm-resolver-repos.json'),
    ).writeAsStringSync(
      _prettyJson([p.join(_repositoryRoot.path, 'dist', 'vpm', 'index.json')]),
    );

    // Stamp the display name into the README's first heading (best-effort).
    final readme = File(p.join(root.path, 'README.md'));
    if (readme.existsSync()) {
      try {
        final lines = readme.readAsLinesSync();
        if (lines.isNotEmpty && lines.first.startsWith('# ')) {
          lines[0] = '# $name — Robotopia UGC World';
          readme.writeAsStringSync('${lines.join('\n')}\n');
        }
      } on Object {
        // ignore — cosmetic
      }
    }

    return _registerProject(root.path);
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
          final decoded = jsonDecode(file.readAsStringSync());
          if (decoded is Map && decoded['name'] is String) {
            final name = (decoded['name'] as String).trim();
            if (name.isNotEmpty) return name;
          }
        }
      } else if (kind == ProjectKind.unityPackage) {
        final file = File(p.join(dir.path, 'package.json'));
        if (file.existsSync()) {
          final decoded = jsonDecode(file.readAsStringSync());
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

  // Reads the project's required Unity version from ProjectSettings/ProjectVersion.txt (Unity projects) or the
  // package.json `unity` field (UPM packages). Empty when unknown.
  String _readUnityVersion(Directory dir) {
    final versionFile = File(
      p.join(dir.path, 'ProjectSettings', 'ProjectVersion.txt'),
    );
    if (versionFile.existsSync()) {
      for (final line in versionFile.readAsLinesSync()) {
        final trimmed = line.trim();
        if (trimmed.startsWith('m_EditorVersion:')) {
          return trimmed.substring('m_EditorVersion:'.length).trim();
        }
      }
    }
    final packageFile = File(p.join(dir.path, 'package.json'));
    if (packageFile.existsSync()) {
      try {
        final decoded = jsonDecode(packageFile.readAsStringSync());
        if (decoded is Map && decoded['unity'] is String) {
          return (decoded['unity'] as String).trim();
        }
      } on Object {
        // ignore
      }
    }
    return '';
  }

  // Detect-only scan of installed Unity editors via Unity Hub install roots. Windows-first (the game is
  // Windows); a best-effort macOS/Linux fallback is included. Sorted newest-first.
  Future<List<UnityEditor>> _scanUnityEditors() async {
    final byVersion = <String, String>{};

    void consider(String version, String exePath) {
      if (version.isEmpty || !File(exePath).existsSync()) return;
      byVersion.putIfAbsent(version, () => exePath);
    }

    void scanHubRoot(String root, String exeRelative) {
      final dir = Directory(root);
      if (!dir.existsSync()) return;
      for (final entry in dir.listSync().whereType<Directory>()) {
        consider(p.basename(entry.path), p.join(entry.path, exeRelative));
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
            final decoded = jsonDecode(secondaryFile.readAsStringSync());
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

    // An explicit override always wins (version derived from its parent folder when it looks like a Hub layout).
    final override = Platform.environment['UNITY_EDITOR_PATH'];
    if (override != null &&
        override.isNotEmpty &&
        File(override).existsSync()) {
      final guessVersion = _versionFromEditorPath(override);
      byVersion[guessVersion.isEmpty ? 'custom' : guessVersion] = override;
    }

    final editors = byVersion.entries
        .map((entry) => UnityEditor(version: entry.key, path: entry.value))
        .toList();
    editors.sort((a, b) => _compareUnityVersions(b.version, a.version));
    return editors;
  }

  String _versionFromEditorPath(String exePath) {
    // …/Unity/Hub/Editor/<version>/Editor/Unity.exe → <version>
    var dir = File(exePath).parent;
    for (var i = 0; i < 3 && dir.path != dir.parent.path; i++) {
      final name = p.basename(dir.path);
      if (RegExp(r'^\d').hasMatch(name)) return name;
      dir = dir.parent;
    }
    return '';
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

    final editors = await _scanUnityEditors();
    if (editors.isEmpty) {
      throw StateError(
        'No installed Unity editor found. Install one via Unity Hub, '
        'or set UNITY_EDITOR_PATH.',
      );
    }

    final required = _readUnityVersion(dir);
    final chosen = editors.firstWhere(
      (editor) => editor.version == required,
      orElse: () => editors.first, // newest installed
    );

    await Process.start(chosen.path, [
      '-projectPath',
      normalized,
    ], mode: ProcessStartMode.detached);
    await _touchProject(normalized);
    return chosen.path;
  }
}
