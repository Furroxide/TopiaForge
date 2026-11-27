import 'dart:io';

import 'package:path/path.dart' as p;

/// The data folder under the roaming application data folder on Windows.
const _windowsDataFolder = 'TopiaForgeLauncher';

String resolveTopiaForgeDataRoot({
  Map<String, String>? environment,
  bool? isWindows,
  String? currentDirectory,
}) {
  final values = environment ?? Platform.environment;
  final configured = values['TOPIAFORGE_DATA_ROOT']?.trim();
  if (configured != null && configured.isNotEmpty) {
    return configured;
  }

  if (isWindows ?? Platform.isWindows) {
    final appData = values['APPDATA'];
    if (appData != null && appData.isNotEmpty) {
      return p.join(appData, _windowsDataFolder);
    }
  }

  final home =
      values['HOME'] ??
      values['USERPROFILE'] ??
      currentDirectory ??
      Directory.current.path;
  return p.join(home, '.topiaforge_launcher');
}

/// Whether [dataRoot] is the data root this user gets without
/// `TOPIAFORGE_DATA_ROOT`. Spellings of one root compare equal: relative
/// roots resolve against [currentDirectory], separators and `.` or `..`
/// segments are normalized, and case is folded on Windows.
///
/// On Windows that root is under [windowsRoamingAppData], the user's own
/// roaming folder (`windowsRoamingAppDataFolder`), and never under
/// `APPDATA`: a process can point `APPDATA` anywhere for itself, so a run
/// with a scratch `APPDATA` is on a root of its own. Without that folder,
/// nothing is the default root on Windows.
bool isDefaultTopiaForgeDataRoot(
  String dataRoot, {
  Map<String, String>? environment,
  bool? isWindows,
  String? currentDirectory,
  String? windowsRoamingAppData,
}) {
  final onWindows = isWindows ?? Platform.isWindows;
  final context = p.Context(
    style: onWindows ? p.Style.windows : p.Style.posix,
    current: currentDirectory ?? Directory.current.path,
  );
  String key(String root) {
    final normalized = context.normalize(context.absolute(root));
    return onWindows ? normalized.toLowerCase() : normalized;
  }

  final String defaultRoot;
  if (onWindows) {
    if (windowsRoamingAppData == null || windowsRoamingAppData.isEmpty) {
      return false;
    }
    defaultRoot = context.join(windowsRoamingAppData, _windowsDataFolder);
  } else {
    defaultRoot = resolveTopiaForgeDataRoot(
      environment: {...(environment ?? Platform.environment)}
        ..remove('TOPIAFORGE_DATA_ROOT'),
      isWindows: false,
      currentDirectory: currentDirectory,
    );
  }
  return key(dataRoot) == key(defaultRoot);
}
