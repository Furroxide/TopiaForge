import 'dart:io';

import 'package:path/path.dart' as p;

/// Locates the self-contained GameCompat executable carried beside a
/// packaged TopiaForge CLI without depending on a source checkout.
class GameCompatExecutableLocator {
  const GameCompatExecutableLocator();

  String? findPackaged({required String resolvedExecutable, bool? isWindows}) {
    final windows = isWindows ?? Platform.isWindows;
    final cliName = p.basename(resolvedExecutable);
    final normalizedCliName = windows ? cliName.toLowerCase() : cliName;
    final expectedCliNames = windows
        ? const {'topiaforge.exe'}
        : const {'topiaforge', 'topiaforge-arm64', 'topiaforge-x64'};
    if (!expectedCliNames.contains(normalizedCliName) ||
        !_isRegularUnlinkedFile(resolvedExecutable)) {
      return null;
    }

    final releaseRoot = p.dirname(p.absolute(resolvedExecutable));
    final candidate = p.join(
      releaseRoot,
      windows
          ? 'TopiaForge.GameCompat.Extractor.exe'
          : 'TopiaForge.GameCompat.Extractor',
    );
    if (_isRegularUnlinkedFile(candidate)) {
      return candidate;
    }
    return null;
  }

  bool _isRegularUnlinkedFile(String path) {
    try {
      return FileSystemEntity.typeSync(path, followLinks: false) ==
          FileSystemEntityType.file;
    } on FileSystemException {
      // A missing, unreadable, linked, or racing path is not packaged code.
      return false;
    }
  }
}
