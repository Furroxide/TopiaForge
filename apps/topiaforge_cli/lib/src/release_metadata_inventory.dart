import 'dart:convert';
import 'dart:io';

import 'package:archive/archive_io.dart';
import 'package:crypto/crypto.dart';
import 'package:path/path.dart' as p;

import 'bounded_file_reader.dart';
import 'release_policy.dart';

class ReleaseMetadataInventory {
  const ReleaseMetadataInventory({
    required this.ecosystem,
    required this.provenance,
    required this.legalInventory,
  });

  final Map<String, Object?> ecosystem;
  final Map<String, Object?> provenance;
  final List<Map<String, Object?>> legalInventory;
}

class ReleaseMetadataInventoryBuilder {
  const ReleaseMetadataInventoryBuilder();

  Future<ReleaseMetadataInventory> build({
    required String repositoryRoot,
    required TopiaForgeReleasePolicy policy,
    required TopiaForgeReleaseCatalogEntry release,
    required Directory assets,
  }) async {
    final ecosystem = await _ecosystemInventory(release, assets);
    final provenance = await _provenanceInventory(repositoryRoot, policy);
    final legal = await _legalInventory(repositoryRoot, policy);
    return ReleaseMetadataInventory(
      ecosystem: ecosystem,
      provenance: provenance,
      legalInventory: legal,
    );
  }

  Future<Map<String, Object?>> _ecosystemInventory(
    TopiaForgeReleaseCatalogEntry release,
    Directory assets,
  ) async {
    final expectedMods = [
      for (final entry in release.mods.entries)
        '${entry.key}-${entry.value}.topiaforgemod',
    ]..sort();
    final expectedVpm = [
      'index.json',
      for (final entry in release.vpmPackages.entries)
        '${entry.key}-${entry.value}.zip',
    ]..sort();
    final expectedPaths = {
      for (final name in expectedMods) 'dist/$name',
      for (final name in expectedVpm) 'dist/vpm/$name',
    };
    final platformCopies = <Map<String, Object?>>[];
    Map<String, Map<String, Object?>>? canonical;
    for (final archiveName
        in release.artifacts
            .where((name) => name.startsWith('TopiaForge-'))
            .toList()
          ..sort()) {
      final archive = File(p.join(assets.path, archiveName));
      final copy = await _readEcosystemFromArchive(
        archive,
        expectedPaths: expectedPaths,
      );
      canonical ??= copy;
      if (jsonEncode(copy) != jsonEncode(canonical)) {
        throw StateError(
          'Canonical ecosystem bytes differ inside $archiveName.',
        );
      }
      platformCopies.add({
        'archive': archiveName,
        'identitySha256': _jsonSha256(copy),
      });
    }
    if (platformCopies.length != 3 || canonical == null) {
      throw StateError('All three platform archives are required in the BOM.');
    }
    for (final name in expectedMods) {
      final standalone = File(p.join(assets.path, name));
      final nested = canonical['dist/$name'];
      if (nested == null ||
          standalone.lengthSync() != nested['size'] ||
          await _sha256File(standalone) != nested['sha256']) {
        throw StateError(
          'Standalone mod package $name differs from canonical archive bytes.',
        );
      }
    }
    return {
      'canonical': true,
      'expectedMods': expectedMods,
      'expectedVpm': expectedVpm,
      'identitySha256': _jsonSha256(canonical),
      'files': [for (final value in canonical.values) value],
      'platformCopies': platformCopies,
    };
  }

  Future<Map<String, Map<String, Object?>>> _readEcosystemFromArchive(
    File file, {
    required Set<String> expectedPaths,
  }) async {
    _requireRegularFile(file, maxBytes: 512 * 1024 * 1024);
    final input = InputFileStream(file.path);
    final Archive archive;
    try {
      archive = ZipDecoder().decodeStream(input);
    } finally {
      await input.close();
    }
    try {
      if (archive.length > 20000) {
        throw StateError('Release archive has too many entries: ${file.path}');
      }
      final found = <String, Map<String, Object?>>{};
      var selectedBytes = 0;
      for (final entry in archive.files) {
        final normalized = entry.name.replaceAll('\\', '/');
        final marker = normalized.indexOf('dist/');
        if (marker < 0 ||
            (marker > 0 && normalized[marker - 1] != '/') ||
            entry.isDirectory ||
            entry.isSymbolicLink) {
          continue;
        }
        final path = normalized.substring(marker);
        if (!expectedPaths.contains(path)) {
          if (path.endsWith('.topiaforgemod') || path.startsWith('dist/vpm/')) {
            throw StateError(
              'Unexpected ecosystem entry $path in ${p.basename(file.path)}.',
            );
          }
          continue;
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
        if (path == 'dist/vpm/index.json') {
          final decoded = jsonDecode(utf8.decode(bytes, allowMalformed: false));
          if (decoded is! Map) {
            throw StateError('Nested VPM index must be a JSON object.');
          }
        }
        found[path] = {
          'path': path,
          'size': bytes.length,
          'sha256': sha256.convert(bytes).toString(),
        };
      }
      if (found.length != expectedPaths.length ||
          !found.keys.toSet().containsAll(expectedPaths)) {
        throw StateError(
          'Canonical ecosystem set is incomplete in ${p.basename(file.path)}.',
        );
      }
      return {for (final key in found.keys.toList()..sort()) key: found[key]!};
    } finally {
      await archive.clear();
    }
  }

  Future<Map<String, Object?>> _provenanceInventory(
    String root,
    TopiaForgeReleasePolicy policy,
  ) async {
    final bepFile = File(p.join(root, policy.bepInExProvenanceFile));
    final bep = _readBoundedJsonObject(bepFile);
    final unityUiManifest = File(
      p.join(
        root,
        'src',
        'TopiaForge.Mods.UnityUi',
        'Assets',
        'topiaforge-ui.manifest.json',
      ),
    );
    final unityUi = _readBoundedJsonObject(unityUiManifest);
    final unityUiBundle = File(
      p.join(p.dirname(unityUiManifest.path), 'topiaforge-ui.bundle'),
    );
    _requireRegularFile(unityUiBundle, maxBytes: 64 * 1024 * 1024);
    final bundleHash = await _sha256File(unityUiBundle);
    if (unityUi['editorVersion'] != policy.toolchains['unity'] ||
        unityUi['sha256'] != bundleHash) {
      throw StateError(
        'TopiaForge Unity UI provenance does not match the pinned editor or bundle.',
      );
    }
    return {
      'unity': {
        'editorVersion': unityUi['editorVersion'],
        'bundle': unityUi['bundle'],
        'bundleSha256': bundleHash,
        'manifest': p.relative(unityUiManifest.path, from: root),
        'manifestSha256': await _sha256File(unityUiManifest),
      },
      'bepInEx': {
        'version': policy.bepInExVersion,
        'unityDoorstopVersion': policy.unityDoorstopVersion,
        'unityDoorstopCommit': policy.unityDoorstopCommit,
        'manifest': policy.bepInExProvenanceFile,
        'manifestSha256': await _sha256File(bepFile),
        'bundledAssets': bep['bundledAssets'],
        'correspondingSource': bep['correspondingSource'],
      },
    };
  }

  Future<List<Map<String, Object?>>> _legalInventory(
    String root,
    TopiaForgeReleasePolicy policy,
  ) async {
    final paths = <String>{
      'THIRD_PARTY_NOTICES.md',
      for (final entity in Directory(
        p.join(root, 'third_party', 'BepInEx', 'LICENSES'),
      ).listSync(followLinks: false).whereType<File>())
        p.relative(entity.path, from: root),
      'packages/launcher_ui/fonts/Audiowide-OFL.txt',
      'packages/launcher_ui/fonts/Quicksand-OFL.txt',
      if (policy.licenseFile != null) policy.licenseFile!,
      // Owned license texts that are physically redistributed: the shared mod
      // license injected into every .topiaforgemod, the launcher UI package
      // license, the contribution certificate copied beside the payload, and
      // the license shipped inside each VPM package.
      'DCO',
      'mods/LICENSE',
      'packages/launcher_ui/LICENSE',
      'templates/TopiaForge.UnityWorldTemplate/Packages/'
          'io.github.furroxide.topiaforge.vpm-resolver/LICENSE.md',
      'templates/TopiaForge.UnityWorldTemplate/Packages/'
          'io.github.furroxide.topiaforge.world-companion/LICENSE.md',
      'templates/unity-companion/Packages/'
          'io.github.furroxide.topiaforge.ugc-companion/LICENSE.md',
    };
    if (paths.length < 9 || paths.length > 32) {
      throw StateError('License/notice inventory is incomplete or excessive.');
    }
    final inventory = <Map<String, Object?>>[];
    for (final path in paths.toList()..sort()) {
      final file = File(p.join(root, path));
      _requireRegularFile(file, maxBytes: 4 * 1024 * 1024);
      inventory.add({
        'path': path,
        'size': file.lengthSync(),
        'sha256': await _sha256File(file),
      });
    }
    return inventory;
  }
}

Map<String, Object?> _readBoundedJsonObject(File file) {
  return readBoundedJsonObjectSync(file, maxBytes: CliFileLimits.metadata);
}

void _requireRegularFile(File file, {required int maxBytes}) {
  if (FileSystemEntity.typeSync(file.path, followLinks: false) !=
      FileSystemEntityType.file) {
    throw StateError('Expected a regular file: ${file.path}');
  }
  final size = file.lengthSync();
  if (size <= 0 || size > maxBytes) {
    throw StateError('File is empty or exceeds $maxBytes bytes: ${file.path}');
  }
}

String _jsonSha256(Object value) =>
    sha256.convert(utf8.encode(jsonEncode(value))).toString();

Future<String> _sha256File(File file) async =>
    (await sha256.bind(file.openRead()).single).toString();
