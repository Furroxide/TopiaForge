import 'dart:convert';
import 'dart:io';

import 'package:archive/archive_io.dart';
import 'package:crypto/crypto.dart';
import 'package:path/path.dart' as p;

import 'bounded_file_reader.dart';
import 'release_ecosystem_identity.dart';
import 'release_handoff_models.dart';
import 'release_handoff_qa.dart';
import 'release_policy.dart';

part 'release_handoff_contract.dart';
part 'release_handoff_ecosystem.dart';
part 'release_handoff_qa_contract.dart';

class ReleaseHandoffVerification {
  const ReleaseHandoffVerification({
    required this.handoff,
    required this.platformBundles,
  });

  final ReleaseHandoffManifest handoff;
  final Map<String, ReleasePlatformBundle> platformBundles;
}

class TopiaForgeReleaseHandoff {
  const TopiaForgeReleaseHandoff();

  Future<String> buildPlatformBundle({
    required String repositoryRoot,
    required String version,
    required String targetSha,
    required String platform,
    required String archivePath,
    required String canonicalEcosystemSha256,
    required Map<String, String> evidenceSha256,
    required String qaPath,
    required String outputPath,
  }) async {
    final context = _loadHandoffContext(repositoryRoot, version, targetSha);
    _requirePlatform(platform, context.targetPlatforms);
    _requireSha256(canonicalEcosystemSha256, 'canonicalEcosystemSha256');
    final expectedArchive = releaseArchiveForPlatform(platform);
    final archive = await _inspectFile(
      File(archivePath),
      expectedName: expectedArchive,
      maxBytes: CliFileLimits.package,
    );
    final validations = <String, ReleaseHandoffValidation>{
      for (final entry in evidenceSha256.entries)
        entry.key: ReleaseHandoffValidation(
          status: 'passed',
          evidenceSha256: entry.value,
        ),
    };
    final qaSource = _readQaSource(File(qaPath));
    final qa = buildReleasePlatformQa(
      platform: platform,
      source: qaSource.json,
      sourceDescriptorSha256: qaSource.sha256,
    );
    final bundle = ReleasePlatformBundle(
      version: version,
      targetSha: targetSha,
      platform: platform,
      builderProfile: _builderProfiles[platform]!,
      archive: archive,
      canonicalEcosystemSha256: canonicalEcosystemSha256,
      toolchains: context.policy.toolchains,
      platformToolchains: context.platformToolchains[platform]!,
      signing: _signingState(platform),
      validations: validations,
      qa: qa,
    );
    _validatePlatformBundle(bundle, context);
    final output = File(outputPath);
    final expectedOutput = releasePlatformBundleFileName(platform);
    if (p.basename(output.path) != expectedOutput) {
      throw StateError('Platform bundle output must be named $expectedOutput.');
    }
    _writeImmutableJson(output, bundle.toJson());
    return output.absolute.path;
  }

  Future<String> buildHandoff({
    required String repositoryRoot,
    required String version,
    required String targetSha,
    required String assetsDirectory,
    String? outputPath,
  }) async {
    final context = _loadHandoffContext(repositoryRoot, version, targetSha);
    final assets = _requireAssetsDirectory(assetsDirectory);
    _rejectUnexpectedBundleNames(assets, context.targetPlatforms);
    final references = <ReleaseHandoffPlatformReference>[];
    String? ecosystemSha256;
    String? reproducibilityEvidenceSha256;
    for (final platform in context.targetPlatforms) {
      final manifestName = releasePlatformBundleFileName(platform);
      final manifestFile = File(p.join(assets.path, manifestName));
      final manifestRecord = await _inspectFile(
        manifestFile,
        expectedName: manifestName,
        maxBytes: CliFileLimits.metadata,
      );
      final bundle = _readPlatformBundle(manifestFile);
      _validatePlatformBundle(bundle, context, expectedPlatform: platform);
      final archive = await _inspectFile(
        File(p.join(assets.path, bundle.archive.name)),
        expectedName: releaseArchiveForPlatform(platform),
        maxBytes: CliFileLimits.package,
      );
      _requireSameFile(bundle.archive, archive, 'archive for $platform');
      ecosystemSha256 ??= bundle.canonicalEcosystemSha256;
      if (bundle.canonicalEcosystemSha256 != ecosystemSha256) {
        throw StateError(
          'Platform bundles do not contain one canonical ecosystem digest.',
        );
      }
      final evidence =
          bundle.validations['ecosystem-reproducibility']!.evidenceSha256;
      reproducibilityEvidenceSha256 ??= evidence;
      if (evidence != reproducibilityEvidenceSha256) {
        throw StateError(
          'Platform bundles do not contain one ecosystem reproducibility '
          'evidence digest.',
        );
      }
      references.add(
        ReleaseHandoffPlatformReference(
          platform: platform,
          builderProfile: bundle.builderProfile,
          manifest: manifestRecord,
          archive: archive,
          signing: bundle.signing,
          validations: bundle.validations,
          qaSha256: _qaSha256(bundle.qa),
        ),
      );
    }
    final handoff = ReleaseHandoffManifest(
      version: version,
      targetSha: targetSha,
      canonicalEcosystemSha256: ecosystemSha256!,
      toolchains: context.policy.toolchains,
      platformBundles: references,
    );
    _validateHandoff(handoff, context);
    final requiredOutput = File(
      p.join(assets.path, releaseHandoffFileName),
    ).absolute;
    final output = File(outputPath ?? requiredOutput.path).absolute;
    if (!p.equals(output.path, requiredOutput.path)) {
      throw StateError(
        'Handoff output must be ${requiredOutput.path} so it remains in the '
        'verified asset set.',
      );
    }
    _writeImmutableJson(output, handoff.toJson());
    await verify(
      repositoryRoot: repositoryRoot,
      version: version,
      targetSha: targetSha,
      assetsDirectory: assetsDirectory,
    );
    return output.absolute.path;
  }

  Future<ReleaseHandoffVerification> verify({
    required String repositoryRoot,
    required String version,
    required String targetSha,
    required String assetsDirectory,
    String? trustOutputPath,
    bool verifyEmbeddedEcosystem = false,
  }) async {
    final context = _loadHandoffContext(repositoryRoot, version, targetSha);
    final assets = _requireAssetsDirectory(assetsDirectory);
    _rejectUnexpectedBundleNames(assets, context.targetPlatforms);
    final handoffFile = File(p.join(assets.path, releaseHandoffFileName));
    final handoff = _readHandoff(handoffFile);
    _validateHandoff(handoff, context);
    final bundles = <String, ReleasePlatformBundle>{};
    String? reproducibilityEvidenceSha256;
    for (var index = 0; index < context.targetPlatforms.length; index += 1) {
      final platform = context.targetPlatforms[index];
      final reference = handoff.platformBundles[index];
      if (reference.platform != platform) {
        throw StateError(
          'Handoff platform bundles must use the canonical platform order.',
        );
      }
      final expectedManifestName = releasePlatformBundleFileName(platform);
      final manifestRecord = await _inspectFile(
        File(p.join(assets.path, reference.manifest.name)),
        expectedName: expectedManifestName,
        maxBytes: CliFileLimits.metadata,
      );
      _requireSameFile(
        reference.manifest,
        manifestRecord,
        'bundle manifest for $platform',
      );
      final bundle = _readPlatformBundle(
        File(p.join(assets.path, expectedManifestName)),
      );
      _validatePlatformBundle(bundle, context, expectedPlatform: platform);
      _requireSameBundleSummary(reference, bundle);
      final archive = await _inspectFile(
        File(p.join(assets.path, reference.archive.name)),
        expectedName: releaseArchiveForPlatform(platform),
        maxBytes: CliFileLimits.package,
      );
      _requireSameFile(reference.archive, archive, 'archive for $platform');
      _requireSameFile(bundle.archive, archive, 'bundle archive for $platform');
      if (bundle.canonicalEcosystemSha256 != handoff.canonicalEcosystemSha256) {
        throw StateError(
          '$platform has a different canonical ecosystem digest.',
        );
      }
      final evidence =
          bundle.validations['ecosystem-reproducibility']!.evidenceSha256;
      reproducibilityEvidenceSha256 ??= evidence;
      if (evidence != reproducibilityEvidenceSha256) {
        throw StateError(
          'Platform bundles disagree on ecosystem reproducibility evidence.',
        );
      }
      if (verifyEmbeddedEcosystem) {
        final embeddedDigest = await _embeddedEcosystemDigest(
          File(p.join(assets.path, reference.archive.name)),
          context.release,
        );
        if (embeddedDigest != bundle.canonicalEcosystemSha256) {
          throw StateError(
            '$platform embedded ecosystem digest does not match '
            'canonicalEcosystemSha256.',
          );
        }
      }
      bundles[platform] = bundle;
    }
    if (trustOutputPath != null) {
      _writeImmutableJson(File(trustOutputPath), _buildTrustEvidence(bundles));
    }
    return ReleaseHandoffVerification(
      handoff: handoff,
      platformBundles: Map.unmodifiable(bundles),
    );
  }
}

Map<String, Object?> _buildTrustEvidence(
  Map<String, ReleasePlatformBundle> bundles,
) => {
  for (final entry in bundles.entries)
    entry.key: {
      'status': switch (entry.key) {
        'linux-x64' => 'not-applicable',
        _ => 'trusted',
      },
      'exceptionApplied': entry.value.signing.exceptionApplied,
    },
};

ReleasePlatformBundle _readPlatformBundle(File file) =>
    ReleasePlatformBundle.fromJson(
      readBoundedJsonObjectSync(file, maxBytes: CliFileLimits.metadata),
    );

ReleaseHandoffManifest _readHandoff(File file) =>
    ReleaseHandoffManifest.fromJson(
      readBoundedJsonObjectSync(file, maxBytes: CliFileLimits.metadata),
    );

Directory _requireAssetsDirectory(String path) {
  if (FileSystemEntity.typeSync(path, followLinks: false) !=
      FileSystemEntityType.directory) {
    throw StateError('Release handoff assets must be a real directory.');
  }
  return Directory(path).absolute;
}

void _rejectUnexpectedBundleNames(
  Directory assets,
  List<String> targetPlatforms,
) {
  final expected = {
    for (final platform in targetPlatforms)
      releasePlatformBundleFileName(platform),
  };
  final actual = listBoundedDirectorySync(assets)
      .map((entity) => p.basename(entity.path))
      .where(
        (name) =>
            name.startsWith('release-platform-bundle-v1-') &&
            name.endsWith('.json'),
      )
      .toSet();
  if (!_sameSet(actual, expected)) {
    throw StateError(
      'Release assets must contain the exact platform bundle manifest set.',
    );
  }
}

Future<ReleaseHandoffFile> _inspectFile(
  File file, {
  required String expectedName,
  required int maxBytes,
}) async {
  if (p.basename(file.path) != expectedName ||
      FileSystemEntity.typeSync(file.path, followLinks: false) !=
          FileSystemEntityType.file) {
    throw StateError('$expectedName must be a regular file.');
  }
  final resolved = file.resolveSymbolicLinksSync();
  final before = file.statSync();
  if (before.size <= 0 || before.size > maxBytes) {
    throw StateError('$expectedName has an invalid size.');
  }
  final digest = (await sha256.bind(file.openRead()).single).toString();
  final after = file.statSync();
  if (file.resolveSymbolicLinksSync() != resolved ||
      after.size != before.size ||
      after.modified != before.modified ||
      after.changed != before.changed) {
    throw StateError('$expectedName changed while it was being hashed.');
  }
  return ReleaseHandoffFile(
    name: expectedName,
    sha256: digest,
    size: before.size,
  );
}

void _writeImmutableJson(File file, Map<String, Object?> value) {
  final bytes = utf8.encode(
    '${const JsonEncoder.withIndent('  ').convert(value)}\n',
  );
  final type = FileSystemEntity.typeSync(file.path, followLinks: false);
  if (type == FileSystemEntityType.file) {
    final current = readBoundedRegularFileSync(
      file,
      maxBytes: CliFileLimits.metadata,
    );
    if (!_sameBytes(current, bytes)) {
      throw StateError(
        'Refusing to replace different release handoff metadata: ${file.path}',
      );
    }
    return;
  }
  if (type != FileSystemEntityType.notFound) {
    throw StateError('Release handoff output path is unsafe: ${file.path}');
  }
  file.parent.createSync(recursive: true);
  final temporary = File(
    '${file.path}.tmp-$pid-${DateTime.now().microsecondsSinceEpoch}',
  );
  try {
    temporary.writeAsBytesSync(bytes, flush: true);
    temporary.renameSync(file.path);
  } finally {
    if (temporary.existsSync()) temporary.deleteSync();
  }
}

void _requireSameFile(
  ReleaseHandoffFile expected,
  ReleaseHandoffFile actual,
  String label,
) {
  if (expected.name != actual.name ||
      expected.sha256 != actual.sha256 ||
      expected.size != actual.size) {
    throw StateError('$label does not match its recorded bytes.');
  }
}

void _requireSameBundleSummary(
  ReleaseHandoffPlatformReference reference,
  ReleasePlatformBundle bundle,
) {
  final referenceValidations = {
    for (final key in reference.validations.keys.toList()..sort())
      key: reference.validations[key]!.toJson(),
  };
  final bundleValidations = {
    for (final key in bundle.validations.keys.toList()..sort())
      key: bundle.validations[key]!.toJson(),
  };
  if (reference.builderProfile != bundle.builderProfile ||
      jsonEncode(reference.signing.toJson()) !=
          jsonEncode(bundle.signing.toJson()) ||
      jsonEncode(referenceValidations) != jsonEncode(bundleValidations) ||
      reference.qaSha256 != _qaSha256(bundle.qa)) {
    throw StateError(
      '${bundle.platform} handoff summary differs from its platform bundle.',
    );
  }
}

void _requirePlatform(String platform, List<String> targetPlatforms) {
  if (!targetPlatforms.contains(platform)) {
    throw StateError(
      'Release handoff platform is not targeted by policy: $platform.',
    );
  }
}

void _requireSha256(String value, String label) {
  if (!RegExp(r'^[0-9a-f]{64}$').hasMatch(value)) {
    throw StateError('$label must be a lowercase SHA-256 digest.');
  }
}

bool _sameStringMap(Map<String, String> left, Map<String, String> right) =>
    left.length == right.length &&
    left.entries.every((entry) => right[entry.key] == entry.value);

bool _sameSet(Set<String> left, Set<String> right) =>
    left.length == right.length && left.containsAll(right);

bool _sameBytes(List<int> left, List<int> right) {
  if (left.length != right.length) return false;
  for (var index = 0; index < left.length; index += 1) {
    if (left[index] != right[index]) return false;
  }
  return true;
}
