import 'dart:io';

import 'package:path/path.dart' as p;

import 'bounded_file_reader.dart';
import 'release_handoff_models.dart';
import 'release_metadata_readiness.dart';
import 'release_metadata_source_content.dart';
import 'release_policy.dart';

/// Publication reads source files only from an unchanged candidate checkout.
/// Asset and metadata paths resolve from the caller's working directory, just
/// like the builder's Directory consumers; only Git paths are relative to root.
Future<void> verifyMetadataPublicationSource(
  String root,
  String targetSha,
  String version,
  String assetsDirectory,
  String metadataDirectory, {
  required bool allowUnresolved,
}) async {
  if (allowUnresolved) return;
  if (!RegExp(r'^[0-9a-f]{40}$').hasMatch(targetSha)) {
    throw StateError('Publication metadata requires an exact target SHA.');
  }
  final diff = await Process.run('git', [
    '--no-replace-objects',
    '-C',
    root,
    'diff',
    '--quiet',
    '--no-ext-diff',
    '--ignore-submodules=none',
    targetSha,
    '--',
  ]);
  if (diff.exitCode != 0) {
    throw StateError(
      'Publication metadata source differs from the exact target SHA.',
    );
  }
  await _requireTracked(root, targetSha, [
    'release/release-policy.json',
    'release/catalog.json',
  ]);
  final policy = TopiaForgeReleasePolicy.load(root);
  final release = TopiaForgeReleaseCatalog.load(root).release(version);
  final allowedUntracked = <String>{
    for (final name in {
      ...release.artifacts,
      ...policy.generatedMetadata,
      ...releaseCandidateMetadataFileNames,
      releaseHandoffFileName,
      '$releaseHandoffFileName.p7s',
      for (final platform in policy.targetPlatforms)
        releasePlatformBundleFileName(platform),
      '.platform-trust-evidence.json',
    })
      p.normalize(p.absolute(assetsDirectory, name)),
    for (final name in [
      'release-bom.json',
      'release-sbom.spdx.json',
      'SHA256SUMS',
    ])
      p.normalize(p.absolute(metadataDirectory, name)),
  };
  final untracked = await Process.run('git', [
    '-C',
    root,
    'ls-files',
    '--others',
    '--exclude-standard',
    '-z',
    '--',
    '.github',
    'release',
    'schemas',
    'src',
    'tools',
    'third_party',
    'mods',
    'packages',
    'templates',
    'apps',
    'baselines',
    'LICENSE',
    'THIRD_PARTY_NOTICES.md',
    'global.json',
    'Directory.Build.props',
    'DCO',
  ]);
  if (untracked.exitCode != 0 ||
      untracked.stdout is! String ||
      (untracked.stdout as String).length > 1024 * 1024) {
    throw StateError(
      'Cannot establish the publication metadata source inventory.',
    );
  }
  for (final name in (untracked.stdout as String).split('\x00')) {
    if (name.isEmpty) continue;
    if (!allowedUntracked.contains(p.normalize(p.absolute(root, name)))) {
      throw StateError('Publication metadata refuses untracked source inputs.');
    }
  }
  // Ignored files are not visible to ls-files --others. Every directly consumed
  // metadata input, including dynamically discovered legal texts, must be tracked.
  final licenses = Directory(p.join(root, 'third_party/BepInEx/LICENSES'));
  final legalFiles = listBoundedDirectorySync(licenses);
  final inputs = <String>{
    'schemas/topiaforge.release-bom.schema.json',
    'schemas/topiaforge.release-spdx.schema.json',
    policy.gameBuildMetadataFile,
    policy.bepInExProvenanceFile,
    release.notesFile,
    'src/TopiaForge.Mods.UnityUi/Assets/topiaforge-ui.manifest.json',
    'src/TopiaForge.Mods.UnityUi/Assets/topiaforge-ui.bundle',
    'THIRD_PARTY_NOTICES.md',
    'DCO',
    'mods/LICENSE',
    'packages/launcher_ui/LICENSE',
    'packages/launcher_ui/fonts/Audiowide-OFL.txt',
    'packages/launcher_ui/fonts/Quicksand-OFL.txt',
    'tools/unity-ui-bundle/Assets/TextMesh Pro/Fonts/LiberationSans - OFL.txt',
    'templates/TopiaForge.UnityWorldTemplate/Packages/'
        'io.github.furroxide.topiaforge.vpm-resolver/LICENSE.md',
    'templates/TopiaForge.UnityWorldTemplate/Packages/'
        'io.github.furroxide.topiaforge.world-companion/LICENSE.md',
    if (policy.licenseFile != null) policy.licenseFile!,
    for (final entity in legalFiles)
      if (entity is File) p.relative(entity.path, from: root),
  };
  await _requireTracked(root, targetSha, inputs.toList()..sort());
}

Future<void> _requireTracked(
  String root,
  String targetSha,
  List<String> paths,
) async {
  for (final path in paths) {
    final absolute = p.normalize(p.absolute(root, path));
    if (!p.isWithin(p.normalize(p.absolute(root)), absolute) ||
        FileSystemEntity.typeSync(absolute, followLinks: false) !=
            FileSystemEntityType.file) {
      throw StateError(
        'Publication metadata source input is missing or unsafe: $path.',
      );
    }
  }
  final result = await Process.run('git', [
    '-C',
    root,
    'ls-files',
    '--error-unmatch',
    '--',
    ...paths,
  ]);
  if (result.exitCode != 0) {
    throw StateError(
      'Publication metadata source input is not tracked at the candidate.',
    );
  }
  await verifyMetadataSourceContents(root, targetSha, paths);
}
