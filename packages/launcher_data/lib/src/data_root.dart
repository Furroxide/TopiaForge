import 'dart:io';

import 'package:path/path.dart' as p;

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
      return p.join(appData, 'TopiaForgeLauncher');
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
bool isDefaultTopiaForgeDataRoot(
  String dataRoot, {
  Map<String, String>? environment,
  bool? isWindows,
  String? currentDirectory,
}) {
  final onWindows = isWindows ?? Platform.isWindows;
  final values = {...(environment ?? Platform.environment)}
    ..remove('TOPIAFORGE_DATA_ROOT');
  final context = p.Context(
    style: onWindows ? p.Style.windows : p.Style.posix,
    current: currentDirectory ?? Directory.current.path,
  );
  String key(String root) {
    final normalized = context.normalize(context.absolute(root));
    return onWindows ? normalized.toLowerCase() : normalized;
  }

  final defaultRoot = resolveTopiaForgeDataRoot(
    environment: values,
    isWindows: onWindows,
    currentDirectory: currentDirectory,
  );
  return key(dataRoot) == key(defaultRoot);
}
