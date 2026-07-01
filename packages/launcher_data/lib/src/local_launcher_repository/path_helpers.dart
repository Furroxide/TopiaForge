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
    final userProfile = Platform.environment['USERPROFILE'];
    if (userProfile != null && userProfile.isNotEmpty) {
      result = result.replaceAll(userProfile, r'%USERPROFILE%');
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
  final localAppData = Platform.environment['LOCALAPPDATA'];
  if (localAppData == null || localAppData.isEmpty) {
    return null;
  }

  return p.join(localAppData, 'Tomato Cake', 'launcher', 'Robotopia');
}

String _findRepositoryRoot() {
  var current = Directory.current.absolute;
  while (true) {
    if (File(p.join(current.path, 'RobotopiaModManager.slnx')).existsSync()) {
      return current.path;
    }

    final parent = current.parent;
    if (parent.path == current.path) {
      return Directory.current.absolute.path;
    }
    current = parent;
  }
}
