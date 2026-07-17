part of '../local_launcher_repository.dart';

extension _PathHelpers on LocalLauncherRepository {
  Directory _managerRoot(GameInstall install) =>
      Directory(p.join(install.path, 'BepInEx', 'TopiaForge'));

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

  Directory _managerStaging(GameInstall install) =>
      Directory(p.join(_managerRoot(install).path, 'staging'));

  File _managerStateFile(GameInstall install) =>
      File(p.join(_managerRoot(install).path, 'state.json'));

  void _ensureDataRoot() {
    _dataRoot.createSync(recursive: true);
    Directory(p.join(_dataRoot.path, 'logs')).createSync(recursive: true);
    Directory(
      p.join(_dataRoot.path, 'diagnostics'),
    ).createSync(recursive: true);
  }

  String _redact(String text, Iterable<String> gamePaths) {
    var result = text;
    final userHome =
        Platform.environment['USERPROFILE'] ?? Platform.environment['HOME'];
    if (userHome != null && userHome.isNotEmpty) {
      result = result.replaceAll(
        RegExp(RegExp.escape(userHome), caseSensitive: false),
        r'%USERHOME%',
      );
    }
    for (final gamePath in gamePaths) {
      if (gamePath.trim().isNotEmpty) {
        result = result.replaceAll(
          RegExp(RegExp.escape(gamePath), caseSensitive: false),
          r'%ROBOTOPIA_GAME%',
        );
      }
    }
    result = result.replaceAllMapped(
      RegExp(
        r'("(?:accessToken|refreshToken|idToken|sessionToken|session_token|jwt|token|password|secret|apiKey|authorization|documentUrl|connectedDocumentUrl|editorUrl)"\s*:\s*")[^"]*(")',
        caseSensitive: false,
      ),
      (match) => '${match.group(1)}%REDACTED%${match.group(2)}',
    );
    result = result.replaceAllMapped(
      RegExp(
        r'([?&](?:access_token|refresh_token|id_token|session_token|token|password|secret|api_key|signature)=)[^&#\s]+',
        caseSensitive: false,
      ),
      (match) => '${match.group(1)}%REDACTED%',
    );
    result = result.replaceAllMapped(
      RegExp(r'(https?://)[^/@\s]+@', caseSensitive: false),
      (match) => '${match.group(1)}%REDACTED%@',
    );
    result = result.replaceAllMapped(
      RegExp(
        r'(\bauthorization\s*[:=]\s*(?:(?:bearer|basic)\s+)?)[^\s,;]+',
        caseSensitive: false,
      ),
      (match) => '${match.group(1)}%REDACTED%',
    );
    result = result.replaceAllMapped(
      RegExp(
        r'(\b(?:bearer|basic)\s+)[A-Za-z0-9._~+/=-]+',
        caseSensitive: false,
      ),
      (match) => '${match.group(1)}%REDACTED%',
    );
    result = result.replaceAllMapped(
      RegExp(
        r'(\b(?:access[_-]?token|refresh[_-]?token|id[_-]?token|session(?:[_-]?token)?|token|password|secret|api[_-]?key)\s*[:=]\s*)[^\s,;]+',
        caseSensitive: false,
      ),
      (match) => '${match.group(1)}%REDACTED%',
    );
    result = result.replaceAll(
      RegExp(r'\beyJ[A-Za-z0-9_-]*\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b'),
      '%REDACTED_JWT%',
    );
    return result;
  }

  String _prettyJson(Object? value) =>
      const JsonEncoder.withIndent('  ').convert(value);
}

const _maxDiagnosticSourceBytes = 2 * 1024 * 1024;
const _maxDiagnosticSourceLines = 20000;
const _maxRuntimeSourceFileBytes = 512 * 1024 * 1024;
const _maxRuntimeSourceBytes = 2 * 1024 * 1024 * 1024;
const _maxRuntimeSourceEntries = 16384;

void _ensureRuntimeDirectory(Directory root, Directory directory) {
  _requireRuntimeDirectory(root, directory, label: 'Runtime destination');
  directory.createSync(recursive: true);
  if (FileSystemEntity.typeSync(directory.path, followLinks: false) !=
      FileSystemEntityType.directory) {
    throw StateError('Runtime destination is not a regular directory.');
  }
}

void _requireRuntimeDirectory(
  Directory root,
  Directory directory, {
  required String label,
}) {
  final rootPath = root.absolute.path;
  var current = directory.absolute;
  if (current.path != rootPath && !p.isWithin(rootPath, current.path)) {
    throw StateError('$label escapes its trusted root.');
  }
  while (true) {
    final type = FileSystemEntity.typeSync(current.path, followLinks: false);
    if (type == FileSystemEntityType.link) {
      throw StateError('$label contains a symbolic link: ${current.path}');
    }
    if (type != FileSystemEntityType.notFound &&
        type != FileSystemEntityType.directory) {
      throw StateError('$label contains a non-directory: ${current.path}');
    }
    if (current.path == rootPath) {
      return;
    }
    current = current.parent;
  }
}

String _findRepositoryRoot(String? workingDirectory) {
  return _findTopiaForgeRoot(workingDirectory);
}

String _findTopiaForgeRoot(String? workingDirectory) {
  // Tests inject workingDirectory instead of mutating the process-global
  // Directory.current, which is shared across concurrent test isolates.
  final cwd = workingDirectory != null
      ? Directory(workingDirectory).absolute
      : Directory.current.absolute;
  for (final seed in _topiaForgeRootSeeds(cwd)) {
    final root = _walkUpForTopiaForgeRoot(seed);
    if (root != null) {
      return root.path;
    }
  }
  return cwd.path;
}

Iterable<Directory> _topiaForgeRootSeeds(Directory workingDirectory) sync* {
  final configured = Platform.environment['TOPIAFORGE_REPOSITORY_ROOT'];
  if (configured != null && configured.trim().isNotEmpty) {
    yield Directory(configured).absolute;
  }

  final executableDir = File(Platform.resolvedExecutable).absolute.parent;
  yield executableDir;

  final macResources = _macResourcesRoot(executableDir);
  if (macResources != null) {
    yield macResources;
  }

  yield workingDirectory;
}

Directory? _macResourcesRoot(Directory executableDir) {
  final contentsDir = executableDir.parent;
  if (p.basename(executableDir.path) != 'MacOS' ||
      p.basename(contentsDir.path) != 'Contents') {
    return null;
  }
  return Directory(
    p.join(contentsDir.path, 'Resources', 'TopiaForge'),
  ).absolute;
}

Directory? _walkUpForTopiaForgeRoot(Directory seed) {
  var current = seed.absolute;
  while (true) {
    if (_isTopiaForgeRoot(current)) {
      return current;
    }
    final parent = current.parent;
    if (parent.path == current.path) {
      return null;
    }
    current = parent;
  }
}

bool _isTopiaForgeRoot(Directory directory) {
  if (File(p.join(directory.path, 'TopiaForge.slnx')).existsSync()) {
    return true;
  }
  return Directory(p.join(directory.path, 'tools')).existsSync() &&
      Directory(p.join(directory.path, 'templates')).existsSync() &&
      Directory(p.join(directory.path, 'dist')).existsSync();
}
