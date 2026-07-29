import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;

/// Complete managed loader payload required by a clean TopiaForge runtime.
///
/// Module contract assemblies live beside the loader because mod packages must
/// not bundle SDK/framework assemblies of their own.
const releaseLoaderAssemblies = topiaForgeRuntimeLoaderAssemblies;

/// File-name view shared with launcher detection and atomic runtime repair.
final releaseLoaderDlls = topiaForgeRuntimeLoaderDlls;

/// Rejects missing, linked, or drifted loader inputs before release copying.
Future<void> validateReleaseLoaderAssembly(
  String path,
  TopiaForgeRuntimeAssembly assembly,
) async {
  if (FileSystemEntity.typeSync(path, followLinks: false) !=
      FileSystemEntityType.file) {
    throw StateError(
      'Managed loader input is missing or is not a regular file: $path',
    );
  }
  if (!assembly.isPinnedPackage) {
    return;
  }
  final actual = (await sha256.bind(File(path).openRead()).first).toString();
  if (actual != assembly.sha256) {
    throw StateError(
      '${assembly.fileName} SHA-256 mismatch. '
      'Expected ${assembly.sha256} but got $actual.',
    );
  }
}

/// Verifies that the Windows game-executed loader overlay is byte-identical
/// to the canonical release payload copy validated by `test-package`.
void validateWindowsLoaderOverlay(String payloadRoot) {
  final canonical = p.join(
    payloadRoot,
    'src',
    'TopiaForge.ModManager',
    'bin',
    'Release',
    'netstandard2.1',
  );
  final overlay = p.join(
    payloadRoot,
    'BepInEx',
    'plugins',
    'TopiaForge.ModManager',
  );
  for (final assembly in releaseLoaderAssemblies) {
    final canonicalFile = File(p.join(canonical, assembly.fileName));
    final overlayFile = File(p.join(overlay, assembly.fileName));
    if (FileSystemEntity.typeSync(canonicalFile.path, followLinks: false) !=
            FileSystemEntityType.file ||
        FileSystemEntity.typeSync(overlayFile.path, followLinks: false) !=
            FileSystemEntityType.file) {
      throw StateError(
        'Windows loader payload is missing a regular ${assembly.fileName} copy.',
      );
    }
    final canonicalHash = sha256
        .convert(canonicalFile.readAsBytesSync())
        .toString();
    final overlayHash = sha256
        .convert(overlayFile.readAsBytesSync())
        .toString();
    if (canonicalHash != overlayHash) {
      throw StateError(
        'Windows loader overlay ${assembly.fileName} differs from the canonical payload copy.',
      );
    }
  }
}
