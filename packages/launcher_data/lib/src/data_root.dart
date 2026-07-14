import 'dart:io';

import 'package:path/path.dart' as p;

String resolveRobotopiaDataRoot({
  Map<String, String>? environment,
  bool? isWindows,
  String? currentDirectory,
}) {
  final values = environment ?? Platform.environment;
  final configured = values['ROBOTOPIA_DATA_ROOT']?.trim();
  if (configured != null && configured.isNotEmpty) {
    return configured;
  }

  if (isWindows ?? Platform.isWindows) {
    final appData = values['APPDATA'];
    if (appData != null && appData.isNotEmpty) {
      return p.join(appData, 'RobotopiaLauncher');
    }
  }

  final home =
      values['HOME'] ??
      values['USERPROFILE'] ??
      currentDirectory ??
      Directory.current.path;
  return p.join(home, '.robotopia_launcher');
}
