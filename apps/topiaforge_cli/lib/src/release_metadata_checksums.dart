import 'dart:io';

import 'package:path/path.dart' as p;

import 'release_handoff_models.dart';
import 'release_metadata_readiness.dart';

Map<String, File> presentReleaseHandoffFiles(
  Directory assets,
  List<String> targetPlatforms,
) {
  final names = <String>[
    releaseHandoffFileName,
    for (final platform in targetPlatforms)
      releasePlatformBundleFileName(platform),
  ];
  final present = <String, File>{};
  for (final name in names) {
    final file = File(p.join(assets.path, name));
    final type = FileSystemEntity.typeSync(file.path, followLinks: false);
    if (type == FileSystemEntityType.notFound) continue;
    if (type != FileSystemEntityType.file || file.lengthSync() == 0) {
      throw StateError('Release handoff checksum input is invalid: $name.');
    }
    present[name] = file;
  }
  if (present.isNotEmpty && present.length != names.length) {
    throw StateError(
      'SHA256SUMS requires the complete policy-derived release handoff set.',
    );
  }
  final signature = File(p.join(assets.path, '$releaseHandoffFileName.p7s'));
  final signatureType = FileSystemEntity.typeSync(
    signature.path,
    followLinks: false,
  );
  if (signatureType != FileSystemEntityType.notFound) {
    if (signatureType != FileSystemEntityType.file ||
        signature.lengthSync() == 0 ||
        present.isEmpty) {
      throw StateError('Release handoff signature checksum input is invalid.');
    }
    present[p.basename(signature.path)] = signature;
  }
  return present;
}

Map<String, File> presentCandidateMetadataFiles(Directory assets) {
  final files = <String, File>{};
  for (final name in releaseCandidateMetadataFileNames) {
    final file = File(p.join(assets.path, name));
    final type = FileSystemEntity.typeSync(file.path, followLinks: false);
    if (type == FileSystemEntityType.notFound) continue;
    if (type != FileSystemEntityType.file || file.lengthSync() == 0) {
      throw StateError(
        'Candidate qualification checksum input is invalid: $name.',
      );
    }
    files[name] = file;
  }
  if (files.isNotEmpty &&
      files.length != releaseCandidateMetadataFileNames.length) {
    throw StateError('Candidate qualification requires both detached records.');
  }
  return files;
}
