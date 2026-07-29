import 'dart:io';

import 'package:path/path.dart' as p;

final class LauncherInstallationLayout {
  const LauncherInstallationLayout({
    required this.platformId,
    required this.installLayout,
    required this.targetRoot,
    required this.launcherRelativePath,
    required this.helperRelativePaths,
    required this.helperExecutableName,
  });

  final String platformId;
  final String installLayout;
  final String targetRoot;
  final String launcherRelativePath;
  final List<String> helperRelativePaths;
  final String helperExecutableName;

  String get launcherPath => p.join(targetRoot, launcherRelativePath);

  List<String> get helperSourcePaths => [
    for (final relative in helperRelativePaths) p.join(targetRoot, relative),
  ];

  String stagedRootFrom(Directory extractionRoot) =>
      installLayout == 'app-bundle'
      ? p.join(extractionRoot.path, 'TopiaForge.app')
      : extractionRoot.path;

  void validateCurrent() {
    _requireSafeTarget(targetRoot);
    _requireRegularInstallFile(
      launcherPath,
      targetRoot,
      'The packaged TopiaForge launcher was not found.',
    );
    for (final source in helperSourcePaths) {
      _requireRegularInstallFile(
        source,
        targetRoot,
        'The packaged TopiaForge update helper is missing.',
      );
    }
  }

  void validateStaged(Directory extractionRoot) {
    final root = Directory(stagedRootFrom(extractionRoot));
    if (!root.existsSync()) {
      throw StateError('The signed update has the wrong install root.');
    }
    final launcher = File(p.join(root.path, launcherRelativePath));
    if (!launcher.existsSync()) {
      throw StateError('The signed update is missing its launcher.');
    }
    for (final relative in helperRelativePaths) {
      if (!File(p.join(root.path, relative)).existsSync()) {
        throw StateError('The signed update is missing its update helper.');
      }
    }
    if (installLayout == 'app-bundle') {
      final entries = extractionRoot
          .listSync(followLinks: false)
          .map((entity) => p.basename(entity.path))
          .toSet();
      if (!entries.contains('TopiaForge.app') ||
          entries.difference({'TopiaForge.app', 'topiaforge'}).isNotEmpty) {
        throw StateError(
          'The signed macOS archive has an invalid root layout.',
        );
      }
    }
  }

  static LauncherInstallationLayout? detect({
    String? resolvedExecutable,
    String? operatingSystem,
  }) {
    final executable = p.normalize(
      p.absolute(resolvedExecutable ?? Platform.resolvedExecutable),
    );
    final os = operatingSystem ?? Platform.operatingSystem;
    if (os == 'windows') {
      final root = p.dirname(p.dirname(executable));
      final layout = LauncherInstallationLayout(
        platformId: 'windows-x64',
        installLayout: 'portable-root',
        targetRoot: root,
        launcherRelativePath: p.join('launcher', 'topiaforge_launcher.exe'),
        helperRelativePaths: const ['topiaforge.exe'],
        helperExecutableName: 'topiaforge-update-helper.exe',
      );
      return layout._looksPackaged() ? layout : null;
    }
    if (os == 'linux') {
      final root = p.dirname(p.dirname(executable));
      final layout = LauncherInstallationLayout(
        platformId: 'linux-x64',
        installLayout: 'portable-root',
        targetRoot: root,
        launcherRelativePath: p.join('launcher', 'topiaforge_launcher'),
        helperRelativePaths: const ['topiaforge'],
        helperExecutableName: 'topiaforge-update-helper',
      );
      return layout._looksPackaged() ? layout : null;
    }
    if (os == 'macos') {
      final executableDirectory = p.dirname(executable);
      if (p.basename(executableDirectory) != 'MacOS') return null;
      final contents = p.dirname(executableDirectory);
      final bundle = p.dirname(contents);
      if (p.basename(bundle) != 'TopiaForge.app') return null;
      final layout = LauncherInstallationLayout(
        platformId: 'macos-universal',
        installLayout: 'app-bundle',
        targetRoot: bundle,
        launcherRelativePath: p.join(
          'Contents',
          'MacOS',
          'topiaforge_launcher',
        ),
        helperRelativePaths: const [
          'Contents/Resources/TopiaForge/topiaforge',
          'Contents/Resources/TopiaForge/topiaforge-arm64',
          'Contents/Resources/TopiaForge/topiaforge-x64',
        ],
        helperExecutableName: 'topiaforge',
      );
      return layout._looksPackaged() ? layout : null;
    }
    return null;
  }

  bool _looksPackaged() =>
      File(launcherPath).existsSync() &&
      helperSourcePaths.every(FileSystemEntity.isFileSync);
}

void _requireSafeTarget(String value) {
  final normalized = p.normalize(p.absolute(value));
  final root = p.rootPrefix(normalized);
  if (normalized == root ||
      normalized.length <= root.length + 2 ||
      FileSystemEntity.typeSync(normalized, followLinks: false) ==
          FileSystemEntityType.link) {
    throw StateError('The launcher install root is unsafe.');
  }
}

void _requireRegularInstallFile(String value, String root, String message) {
  final normalized = p.normalize(p.absolute(value));
  final normalizedRoot = p.normalize(p.absolute(root));
  if (!p.isWithin(normalizedRoot, normalized) ||
      FileSystemEntity.typeSync(normalized, followLinks: false) !=
          FileSystemEntityType.file) {
    throw StateError(message);
  }
  var parent = p.dirname(normalized);
  while (!p.equals(parent, normalizedRoot)) {
    if (FileSystemEntity.typeSync(parent, followLinks: false) !=
        FileSystemEntityType.directory) {
      throw StateError(message);
    }
    final next = p.dirname(parent);
    if (p.equals(next, parent)) throw StateError(message);
    parent = next;
  }
}
