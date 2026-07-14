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
