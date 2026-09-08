import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:path/path.dart' as p;

import 'bounded_file_reader.dart';
import 'release_candidate_contract.dart';
import 'release_strict_json.dart';

Future<List<Map<String, Object?>>> inspectCandidatePayloads({
  required Directory assets,
  required List<String> names,
  required List<String> archives,
  required List<String> generatedMetadata,
  required bool unsigned,
}) async {
  final auxiliary = {
    candidateReadinessName, candidateAcceptanceName, 'release-handoff-v1.json',
    if (!unsigned) 'release-handoff-v1.json.p7s',
    '.platform-trust-evidence.json',
    ...generatedMetadata,
    for (final archive in archives)
      'release-platform-bundle-v1-${archive.substring(11, archive.length - 4)}.json',
    // Private builder diagnostics are never uploaded by the publisher allowlist.
    'validation-windows.json',
  };
  final privateDirectories = archives.map(p.basenameWithoutExtension).toSet();
  for (final entity in listBoundedDirectorySync(assets)) {
    final name = p.basename(entity.path);
    final type = FileSystemEntity.typeSync(entity.path, followLinks: false);
    if (privateDirectories.contains(name) &&
        type == FileSystemEntityType.directory) {
      continue;
    }
    if (type != FileSystemEntityType.file ||
        (!names.contains(name) && !auxiliary.contains(name))) {
      throw StateError('Unexpected candidate asset: $name.');
    }
  }
  return [
    for (final name in names)
      await inspectCandidateFile(File(p.join(assets.path, name))),
  ];
}

Future<Map<String, Object?>> inspectCandidateFile(File file) async {
  final name = p.basename(file.path);
  final first = await _hashFile(file);
  final second = await _hashFile(file);
  if (canonicalReleaseJson(first) != canonicalReleaseJson(second)) {
    throw StateError('Candidate file changed while hashing: $name.');
  }
  return first;
}

Future<Map<String, Object?>> _hashFile(File file) async {
  final before = file.statSync();
  final resolved = file.resolveSymbolicLinksSync();
  if (FileSystemEntity.typeSync(file.path, followLinks: false) !=
          FileSystemEntityType.file ||
      before.size <= 0 ||
      before.size > CliFileLimits.package) {
    throw StateError(
      'Candidate payload must be a bounded regular file: ${file.path}.',
    );
  }
  var count = 0;
  final digest = await sha256
      .bind(
        file.openRead().map((chunk) {
          count += chunk.length;
          if (count > before.size) {
            throw StateError('Candidate payload grew while hashing.');
          }
          return chunk;
        }),
      )
      .single;
  final after = file.statSync();
  if (count != before.size ||
      after.size != before.size ||
      after.modified != before.modified ||
      after.changed != before.changed ||
      file.resolveSymbolicLinksSync() != resolved ||
      FileSystemEntity.typeSync(file.path, followLinks: false) !=
          FileSystemEntityType.file) {
    throw StateError('Candidate payload changed while hashing: ${file.path}.');
  }
  return {
    'name': p.basename(file.path),
    'size': count,
    'sha256': digest.toString(),
  };
}

List<String> candidateNames(Object? raw, String label) {
  if (raw is! List ||
      raw.isEmpty ||
      raw.length > 256 ||
      raw.any(
        (value) =>
            value is! String ||
            !RegExp(r'^[A-Za-z0-9][A-Za-z0-9._+-]{0,179}$').hasMatch(value),
      )) {
    throw StateError('$label must be a bounded array of safe asset names.');
  }
  final names = raw.cast<String>().toList()..sort();
  if (names.toSet().length != names.length ||
      names.map((name) => name.toLowerCase()).toSet().length != names.length) {
    throw StateError('$label contains duplicate or colliding names.');
  }
  return names;
}
