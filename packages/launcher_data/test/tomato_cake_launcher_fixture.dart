import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;

/// A throwaway `%LOCALAPPDATA%` holding the Tomato Cake launcher's state,
/// plus a separate directory the official launcher can move the game to.
///
/// Tests inject [environment] with `hostPlatform: 'windows'`, so the Windows
/// lookup runs against real directories on every host.
final class TomatoCakeFixture {
  TomatoCakeFixture._(this.rawRoot, this.root);

  factory TomatoCakeFixture.create() {
    final raw = Directory.systemTemp.createTempSync('topiaforge-tomato-cake-');
    // Canonicalise: hosted Windows runners hand out 8.3 temp paths, and the
    // launcher state reports canonical paths.
    return TomatoCakeFixture._(
      raw.path,
      Directory(raw.resolveSymbolicLinksSync()),
    );
  }

  /// The temp root as the system spelled it.
  final String rawRoot;

  /// The canonical temp root.
  final Directory root;

  Directory get localAppData => Directory(p.join(root.path, 'LocalAppData'));

  Directory get stateDirectory =>
      Directory(p.join(localAppData.path, 'Tomato Cake', 'launcher'));

  Directory get defaultGameRoot =>
      Directory(p.join(stateDirectory.path, 'Robotopia'));

  /// The `game_dir` a player picked in the official launcher.
  Directory get relocatedParent =>
      Directory(p.join(root.path, 'Games', 'Tomato Cake'));

  Directory get relocatedGameRoot =>
      Directory(p.join(relocatedParent.path, 'Robotopia'));

  String get configPath =>
      p.join(stateDirectory.path, TomatoCakeLauncherState.configFileName);

  Map<String, String> get environment => {'LOCALAPPDATA': localAppData.path};

  TomatoCakeLauncherState launcherState({String hostPlatform = 'windows'}) =>
      TomatoCakeLauncherState(
        environment: environment,
        hostPlatform: hostPlatform,
      );

  void writeConfig(String text) => writeConfigBytes(utf8.encode(text));

  void writeConfigBytes(List<int> bytes) {
    stateDirectory.createSync(recursive: true);
    File(configPath).writeAsBytesSync(bytes, flush: true);
  }

  void writeGameDirectory(String gameDirectory) =>
      writeConfig(jsonEncode({'game_dir': gameDirectory}));

  /// Writes the launcher's marker the way build 2545 left it, extra field
  /// included.
  void writeStateMarker(int buildId) {
    stateDirectory.createSync(recursive: true);
    File(
      p.join(
        stateDirectory.path,
        TomatoCakeLauncherState.installedBuildFileName,
      ),
    ).writeAsStringSync('{"id":$buildId,"dev":false}');
  }

  /// Another spelling of [directory] on Windows: through the raw temp root,
  /// which hosted runners spell with 8.3 short names, and in upper case.
  /// Other hosts get [directory] unchanged, because their raw temp root can
  /// pass through a link (`/var` on macOS) that the launcher state refuses.
  String alternateSpelling(Directory directory) {
    if (!Platform.isWindows) return directory.path;
    final relative = p.relative(directory.path, from: root.path);
    return p.join(rawRoot, relative.toUpperCase());
  }

  void dispose() {
    if (root.existsSync()) root.deleteSync(recursive: true);
  }
}

/// Creates a directory link: a junction on Windows, which needs no
/// privilege, and a symbolic link elsewhere.
void linkDirectory(String link, String target) {
  if (!Platform.isWindows) {
    Link(link).createSync(target);
    return;
  }
  final result = Process.runSync('cmd', ['/c', 'mklink', '/J', link, target]);
  if (result.exitCode != 0) {
    throw StateError('mklink /J failed: ${result.stdout}${result.stderr}');
  }
}

/// Creates the files TopiaForge needs to recognise a Windows game folder.
void createWindowsGame(Directory root) {
  File(p.join(root.path, 'Robotopia.exe')).createSync(recursive: true);
  File(
    p.join(root.path, 'Robotopia_Data', 'Managed', 'UnityEngine.dll'),
  ).createSync(recursive: true);
}
