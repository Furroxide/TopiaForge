import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;

import 'release_package_io.dart';

/// Complete managed loader payload required by a clean TopiaForge runtime.
///
/// Module contract assemblies live beside the loader because mod packages must
/// not bundle SDK/framework assemblies of their own.
const releaseLoaderAssemblies = topiaForgeRuntimeLoaderAssemblies;

/// File-name view shared with launcher detection and atomic runtime repair.
final releaseLoaderDlls = topiaForgeRuntimeLoaderDlls;

/// Licence texts shipped beside every copy of the loader assemblies.
final releaseLoaderNotices = topiaForgeRuntimeLoaderNotices;

/// Canonical loader directory of a payload, which runtime repair installs
/// from once the archive is extracted.
String releaseLoaderDirectory(String payloadRoot) => p.join(
  payloadRoot,
  'src',
  'TopiaForge.ModManager',
  'bin',
  'Release',
  'netstandard2.1',
);

/// Windows archive-root overlay, copied as-is into a game folder.
String releaseLoaderOverlayDirectory(String payloadRoot) =>
    p.join(payloadRoot, 'BepInEx', 'plugins', 'TopiaForge.ModManager');

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

/// Copies the loader assemblies and their licence texts into [destination].
///
/// Every place a release puts the loader receives the same notices, read from
/// the canonical repository paths that runtime repair installs from.
void copyReleaseLoaderPayload({
  required String repositoryRoot,
  required String builtLoaderDirectory,
  required String destination,
  required ReleaseFileOps fileOps,
}) {
  Directory(destination).createSync(recursive: true);
  for (final dll in releaseLoaderDlls) {
    fileOps.copyFileIfExists(
      p.join(builtLoaderDirectory, dll),
      p.join(destination, dll),
    );
  }
  for (final notice in releaseLoaderNotices) {
    final source = _noticeSource(repositoryRoot, notice);
    if (FileSystemEntity.typeSync(source.path, followLinks: false) !=
        FileSystemEntityType.file) {
      throw StateError(
        'Loader licence notice is missing or is not a regular file: '
        '${notice.sourcePath}',
      );
    }
    fileOps.copyFileIfExists(
      source.path,
      p.join(
        destination,
        topiaForgeRuntimeLoaderNoticeDirectory,
        notice.fileName,
      ),
    );
  }
}

/// Requires the loader notices in a built payload.
///
/// Each canonical source must be present, because runtime repair installs it
/// from the extracted archive, and every copy beside the loader must match it
/// byte for byte.
void validateReleaseLoaderNotices(
  String payloadRoot, {
  required bool windowsOverlay,
}) {
  final loaders = [
    releaseLoaderDirectory(payloadRoot),
    if (windowsOverlay) releaseLoaderOverlayDirectory(payloadRoot),
  ];
  for (final notice in releaseLoaderNotices) {
    final expected = _regularFileSha256(
      _noticeSource(payloadRoot, notice),
      'Package must include the loader licence source ${notice.sourcePath}.',
    );
    for (final loader in loaders) {
      final copy = File(
        p.join(loader, topiaForgeRuntimeLoaderNoticeDirectory, notice.fileName),
      );
      final label = p.relative(copy.path, from: payloadRoot);
      final actual = _regularFileSha256(
        copy,
        'Package must include the loader licence notice $label.',
      );
      if (actual != expected) {
        throw StateError(
          'Loader licence notice $label differs from ${notice.sourcePath}.',
        );
      }
    }
  }
}

/// Verifies that the Windows game-executed loader overlay is byte-identical
/// to the canonical release payload copy validated by `test-package`.
void validateWindowsLoaderOverlay(String payloadRoot) {
  final canonical = releaseLoaderDirectory(payloadRoot);
  final overlay = releaseLoaderOverlayDirectory(payloadRoot);
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

File _noticeSource(String root, TopiaForgeRuntimeLoaderNotice notice) =>
    File(p.joinAll([root, ...p.posix.split(notice.sourcePath)]));

String _regularFileSha256(File file, String missingMessage) {
  if (FileSystemEntity.typeSync(file.path, followLinks: false) !=
      FileSystemEntityType.file) {
    throw StateError(missingMessage);
  }
  return sha256.convert(file.readAsBytesSync()).toString();
}
