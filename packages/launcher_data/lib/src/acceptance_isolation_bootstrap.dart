import 'dart:io';
import 'package:crypto/crypto.dart';
import 'package:path/path.dart' as p;
import 'acceptance_isolation_context.dart';
import 'launch_process_control.dart';

/// Doorstop 4.5.0 from the pinned BepInEx 5.4.23.5 Windows tree. Its target
/// assembly is relative to the isolated working directory, with no overrides.
const acceptanceDoorstopSha256 =
    '4d5c6dfa0f771c6a5b1b0c559aca0bd0ece7d08b08fff894708dc3b73ce73cfc';

Map<String, String> acceptanceLaunchEnvironment(
  AcceptanceIsolationContext context,
  String profilePath,
  String requestId,
) {
  context.verify();
  final config = File(p.join(context.gameRoot, 'doorstop_config.ini'));
  if (sha256.convert(readAcceptanceRecord(config)).toString() !=
      acceptanceDoorstopSha256) {
    throw StateError(
      'Acceptance requires the pinned relative Doorstop bootstrap without path overrides.',
    );
  }
  // A fresh runtime layout must not import an ordinary user's loader config.
  final loaderConfig = Directory(p.join(context.bepInExRoot, 'config'));
  requireAcceptanceUnlinkedPath(loaderConfig.path);
  if (loaderConfig.existsSync() &&
      loaderConfig.listSync(followLinks: false).isNotEmpty) {
    throw StateError(
      'Acceptance requires a fresh loader configuration in its isolated runtime layout.',
    );
  }
  final temporary = p.join(context.launcherRoot, 'native-temp');
  requireAcceptanceUnlinkedPath(temporary);
  Directory(temporary).createSync(recursive: true);
  final windows = readWindowsSystemRoot();
  return {
    'SystemRoot': windows,
    'WINDIR': windows,
    'PATH': p.join(windows, 'System32'),
    'TEMP': temporary,
    'TMP': temporary,
    'TOPIAFORGE_DATA_ROOT': context.launcherRoot,
    'TOPIAFORGE_LAUNCH_PROFILE': profilePath,
    AcceptanceIsolationContext.environmentVariable: requestId,
  };
}
