import 'dart:io';

import 'package:path/path.dart' as p;

/// Resolves the explicit Wine/Proton executable configured by the player.
/// Relative commands never gain authority from the launcher's ambient PATH.
String configuredWineExecutable(Object? value) {
  if (value is! String ||
      value.trim().isEmpty ||
      !p.isAbsolute(value.trim()) ||
      value.contains('\u0000')) {
    throw const FormatException(
      'Configure the full path to the Wine/Proton executable before launching this profile.',
    );
  }
  final executable = value.trim();
  if (FileSystemEntity.typeSync(executable, followLinks: false) !=
      FileSystemEntityType.file) {
    throw const FormatException(
      'The configured Wine/Proton executable must be an existing regular file. Configure its full path before launching this profile.',
    );
  }
  try {
    return File(executable).resolveSymbolicLinksSync();
  } on FileSystemException {
    throw const FormatException(
      'The configured Wine/Proton executable could not be read as a regular file. Configure its full path before launching this profile.',
    );
  }
}
