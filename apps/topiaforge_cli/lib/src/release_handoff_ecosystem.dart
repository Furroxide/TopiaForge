part of 'release_handoff.dart';

Future<String> _embeddedEcosystemDigest(
  File file,
  TopiaForgeReleaseCatalogEntry release,
) async {
  final resolved = file.resolveSymbolicLinksSync();
  final before = file.statSync();
  final expectedPaths = {
    for (final entry in release.mods.entries)
      'dist/${entry.key}-${entry.value}.topiaforgemod',
    'dist/vpm/index.json',
    for (final entry in release.vpmPackages.entries)
      'dist/vpm/${entry.key}-${entry.value}.zip',
  };
  final input = InputFileStream(file.path);
  final Archive archive;
  try {
    archive = ZipDecoder().decodeStream(input);
  } finally {
    await input.close();
  }
  try {
    if (archive.length > 20000) {
      throw StateError(
        'Release archive has too many entries: ${p.basename(file.path)}.',
      );
    }
    final found = <String, String>{};
    var selectedBytes = 0;
    for (final entry in archive.files) {
      final normalized = entry.name.replaceAll('\\', '/');
      final marker = normalized.indexOf('dist/');
      if (marker < 0 || (marker > 0 && normalized[marker - 1] != '/')) {
        continue;
      }
      final path = normalized.substring(marker);
      if (entry.isSymbolicLink) {
        throw StateError(
          'Canonical ecosystem cannot contain symbolic link $path.',
        );
      }
      if (entry.isDirectory) {
        continue;
      }
      if (!expectedPaths.contains(path)) {
        throw StateError(
          'Unexpected ecosystem entry $path in ${p.basename(file.path)}.',
        );
      }
      if (found.containsKey(path) ||
          entry.size <= 0 ||
          entry.size > 256 * 1024 * 1024) {
        throw StateError(
          'Duplicate, empty, or oversized ecosystem entry $path.',
        );
      }
      selectedBytes += entry.size;
      if (selectedBytes > 512 * 1024 * 1024) {
        throw StateError('Canonical ecosystem payload exceeds 512 MB.');
      }
      final bytes = entry.readBytes();
      if (bytes == null || bytes.length != entry.size) {
        throw StateError('Could not read the complete nested entry $path.');
      }
      found[path] = sha256.convert(bytes).toString();
    }
    if (found.length != expectedPaths.length ||
        !found.keys.toSet().containsAll(expectedPaths)) {
      throw StateError(
        'Canonical ecosystem set is incomplete in ${p.basename(file.path)}.',
      );
    }
    final digest = ReleaseEcosystemIdentity.digestRecords({
      for (final entry in found.entries)
        entry.key.substring('dist/'.length): entry.value,
    });
    final after = file.statSync();
    if (file.resolveSymbolicLinksSync() != resolved ||
        after.size != before.size ||
        after.modified != before.modified ||
        after.changed != before.changed) {
      throw StateError(
        '${p.basename(file.path)} changed while its ecosystem was verified.',
      );
    }
    return digest;
  } finally {
    await archive.clear();
  }
}
