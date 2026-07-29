import 'dart:io';

import 'package:path/path.dart' as p;

import 'registry_entry_builder.dart';
import 'bounded_file_reader.dart';
import 'release_policy.dart';

class PrebuiltEcosystemPayload {
  const PrebuiltEcosystemPayload();

  void validate({
    required String repositoryRoot,
    required String path,
    String? version,
  }) {
    final rootType = FileSystemEntity.typeSync(path, followLinks: false);
    if (rootType != FileSystemEntityType.directory) {
      throw StateError(
        'Prebuilt ecosystem payload must be a real directory: $path',
      );
    }
    final policy = TopiaForgeReleasePolicy.load(repositoryRoot);
    final release = TopiaForgeReleaseCatalog.load(
      repositoryRoot,
    ).release(version ?? policy.productVersion);
    final expectedMods = {
      for (final entry in release.mods.entries)
        '${entry.key}-${entry.value}.topiaforgemod': entry,
    };
    final actualMods = <String>{};
    for (final entity in listBoundedDirectorySync(Directory(path))) {
      final name = p.basename(entity.path);
      if (name == 'vpm' && entity is Directory) continue;
      if (entity is! File ||
          FileSystemEntity.typeSync(entity.path, followLinks: false) !=
              FileSystemEntityType.file ||
          !name.endsWith('.topiaforgemod')) {
        throw StateError('Unexpected prebuilt ecosystem entry: $name');
      }
      final expected = expectedMods[name];
      if (expected == null || !actualMods.add(name)) {
        throw StateError('Unexpected or duplicate prebuilt mod package: $name');
      }
      final summary = readModPackage(
        readBoundedRegularFileSync(entity, maxBytes: CliFileLimits.package),
      );
      if (summary.manifest.id.toLowerCase() != expected.key ||
          summary.manifest.version != expected.value) {
        throw StateError('$name manifest does not match the release catalog.');
      }
    }
    if (!_sameNames(actualMods, expectedMods.keys.toSet())) {
      throw StateError('Prebuilt ecosystem payload is missing mod packages.');
    }

    final expectedVpm = {
      'index.json',
      for (final entry in release.vpmPackages.entries)
        '${entry.key}-${entry.value}.zip',
    };
    final vpm = Directory(p.join(path, 'vpm'));
    if (FileSystemEntity.typeSync(vpm.path, followLinks: false) !=
        FileSystemEntityType.directory) {
      throw StateError('Prebuilt ecosystem payload is missing vpm/.');
    }
    final actualVpm = <String>{};
    for (final entity in listBoundedDirectorySync(vpm)) {
      final name = p.basename(entity.path);
      if (entity is! File ||
          FileSystemEntity.typeSync(entity.path, followLinks: false) !=
              FileSystemEntityType.file ||
          !actualVpm.add(name)) {
        throw StateError('Invalid prebuilt VPM entry: $name');
      }
    }
    if (!_sameNames(actualVpm, expectedVpm)) {
      throw StateError('Prebuilt VPM files do not exactly match the catalog.');
    }
    readBoundedJsonObjectSync(
      File(p.join(vpm.path, 'index.json')),
      maxBytes: CliFileLimits.metadata,
    );
  }
}

bool _sameNames(Set<String> left, Set<String> right) =>
    left.length == right.length && left.containsAll(right);
