part of '../local_launcher_repository.dart';

extension _PathHelpers on LocalLauncherRepository {
  Directory _managerRoot(GameInstall install) =>
      Directory(p.join(install.path, 'BepInEx', 'RobotopiaModManager'));

  Directory _packagesRoot(GameInstall install) =>
      Directory(p.join(_managerRoot(install).path, 'packages'))
        ..createSync(recursive: true);

  Directory _packageInbox(GameInstall install) =>
      Directory(p.join(_managerRoot(install).path, 'package-inbox'));

  Directory _managerLogs(GameInstall install) =>
      Directory(p.join(_managerRoot(install).path, 'logs'));

  Directory _managerConfig(GameInstall install) =>
      Directory(p.join(_managerRoot(install).path, 'config'));

  Directory _managerData(GameInstall install) =>
      Directory(p.join(_managerRoot(install).path, 'data'));

  File _managerStateFile(GameInstall install) =>
      File(p.join(_managerRoot(install).path, 'state.json'));

  void _ensureDataRoot() {
    _dataRoot.createSync(recursive: true);
    Directory(p.join(_dataRoot.path, 'logs')).createSync(recursive: true);
    Directory(
      p.join(_dataRoot.path, 'diagnostics'),
    ).createSync(recursive: true);
  }

  void _copyRuntimeDirectory(Directory source, Directory destination) {
    for (final entity in source.listSync(recursive: true)) {
      final relative = p.relative(entity.path, from: source.path);
      final target = p.join(destination.path, relative);
      if (entity is Directory) {
        Directory(target).createSync(recursive: true);
      } else if (entity is File) {
        File(target).createSync(recursive: true);
        entity.copySync(target);
      }
    }
  }

  Future<void> _addFileIfExists(
    Archive archive,
    List<String> included,
    String name,
    File file,
    String gamePath,
  ) async {
    if (!file.existsSync()) {
      return;
    }

    archive.addFile(
      ArchiveFile.string(name, _redact(await file.readAsString(), gamePath)),
    );
    included.add(name);
  }

  String _redact(String text, String gamePath) {
    var result = text;
    final userHome =
        Platform.environment['USERPROFILE'] ?? Platform.environment['HOME'];
    if (userHome != null && userHome.isNotEmpty) {
      result = result.replaceAll(userHome, r'%USERHOME%');
    }
    result = result.replaceAll(gamePath, r'%ROBOTOPIA_GAME%');
    return result;
  }

  String _prettyJson(Object? value) =>
      const JsonEncoder.withIndent('  ').convert(value);

  List<String> _tail(List<String> lines, int maxLines) {
    if (lines.length <= maxLines) {
      return lines;
    }
    return lines.sublist(lines.length - maxLines);
  }
}

String _defaultDataRoot() {
  if (Platform.isWindows) {
    final appData = Platform.environment['APPDATA'];
    if (appData != null && appData.isNotEmpty) {
      return p.join(appData, 'RobotopiaLauncher');
    }
  }

  final home =
      Platform.environment['HOME'] ??
      Platform.environment['USERPROFILE'] ??
      Directory.current.path;
  return p.join(home, '.robotopia_launcher');
}

String? _defaultKnownGamePath() {
  final override = Platform.environment['ROBOTOPIA_GAME_DIR'];
  if (override != null && override.trim().isNotEmpty) {
    return override;
  }

  if (Platform.isWindows) {
    final localAppData = Platform.environment['LOCALAPPDATA'];
    if (localAppData == null || localAppData.isEmpty) {
      return null;
    }
    return p.join(localAppData, 'Tomato Cake', 'launcher', 'Robotopia');
  }

  if (Platform.isMacOS) {
    final home = Platform.environment['HOME'];
    if (home == null || home.isEmpty) {
      return null;
    }
    // The Tomato Cake launcher installs Robotopia.app here; the install root
    // is the directory containing the bundle.
    return p.join(
      home,
      'Library',
      'Application Support',
      'Tomato Cake',
      'launcher',
    );
  }

  // Linux runs the Windows build under Proton/Wine — there is no reliable
  // prefix heuristic, so the user selects the game folder manually.
  return null;
}

String _findRepositoryRoot() {
  return _findQuantumWorksRoot();
}

String _findQuantumWorksRoot() {
  for (final seed in _quantumWorksRootSeeds()) {
    final root = _walkUpForQuantumWorksRoot(seed);
    if (root != null) {
      return root.path;
    }
  }
  return Directory.current.absolute.path;
}

Iterable<Directory> _quantumWorksRootSeeds() sync* {
  final configured = Platform.environment['ROBOTOPIA_REPOSITORY_ROOT'];
  if (configured != null && configured.trim().isNotEmpty) {
    yield Directory(configured).absolute;
  }

  final executableDir = File(Platform.resolvedExecutable).absolute.parent;
  yield executableDir;

  final macResources = _macResourcesRoot(executableDir);
  if (macResources != null) {
    yield macResources;
  }

  yield Directory.current.absolute;
}

Directory? _macResourcesRoot(Directory executableDir) {
  final contentsDir = executableDir.parent;
  if (p.basename(executableDir.path) != 'MacOS' ||
      p.basename(contentsDir.path) != 'Contents') {
    return null;
  }
  return Directory(
    p.join(contentsDir.path, 'Resources', 'QuantumWorks'),
  ).absolute;
}

Directory? _walkUpForQuantumWorksRoot(Directory seed) {
  var current = seed.absolute;
  while (true) {
    if (_isQuantumWorksRoot(current)) {
      return current;
    }
    final parent = current.parent;
    if (parent.path == current.path) {
      return null;
    }
    current = parent;
  }
}

bool _isQuantumWorksRoot(Directory directory) {
  if (File(p.join(directory.path, 'RobotopiaModManager.slnx')).existsSync()) {
    return true;
  }
  return Directory(p.join(directory.path, 'tools')).existsSync() &&
      Directory(p.join(directory.path, 'templates')).existsSync() &&
      Directory(p.join(directory.path, 'dist')).existsSync();
}
