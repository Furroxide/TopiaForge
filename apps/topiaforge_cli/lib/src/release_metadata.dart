import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:json_schema/json_schema.dart';
import 'package:path/path.dart' as p;

import 'release_metadata_inventory.dart';
import 'bounded_file_reader.dart';
import 'release_policy.dart';
import 'release_spdx_metadata.dart';

class ReleaseMetadataResult {
  const ReleaseMetadataResult({
    required this.bomPath,
    required this.sbomPath,
    required this.checksumsPath,
  });

  final String bomPath;
  final String sbomPath;
  final String checksumsPath;
}

class TopiaForgeReleaseMetadataBuilder {
  const TopiaForgeReleaseMetadataBuilder();

  static const trustEvidenceFileName = '.platform-trust-evidence.json';

  Future<ReleaseMetadataResult> build({
    required String repositoryRoot,
    required String version,
    required String targetSha,
    required String assetsDirectory,
    required String outputDirectory,
    bool allowUnresolvedPolicy = false,
  }) async {
    if (!RegExp(r'^[0-9a-f]{40,64}$').hasMatch(targetSha)) {
      throw ArgumentError.value(
        targetSha,
        'targetSha',
        'Expected a lowercase 40-64 character commit hash.',
      );
    }
    final policy = TopiaForgeReleasePolicy.load(repositoryRoot);
    final release = TopiaForgeReleaseCatalog.load(
      repositoryRoot,
    ).release(version);
    final policyIssues = await const ReleasePolicyValidator().validate(
      policy: policy,
      release: release,
      allowUnresolvedPolicy: allowUnresolvedPolicy,
    );
    if (policyIssues.isNotEmpty) {
      throw StateError(
        'Release policy validation failed:\n- ${policyIssues.join('\n- ')}',
      );
    }

    final assets = Directory(assetsDirectory);
    if (!assets.existsSync()) {
      throw StateError(
        'Release assets directory does not exist: $assetsDirectory',
      );
    }
    final actualNames = <String>{};
    for (final entity in listBoundedDirectorySync(assets)) {
      final name = p.basename(entity.path);
      if (name == trustEvidenceFileName) continue;
      if (policy.generatedMetadata.contains(name)) continue;
      if (entity is! File ||
          FileSystemEntity.typeSync(entity.path, followLinks: false) !=
              FileSystemEntityType.file) {
        throw StateError(
          'Release staging must contain only regular files: $name',
        );
      }
      if (!actualNames.add(name)) {
        throw StateError('Duplicate release asset basename: $name');
      }
    }
    if (!_sameSet(actualNames, release.artifacts.toSet())) {
      final missing = release.artifacts.toSet().difference(actualNames).toList()
        ..sort();
      final extra = actualNames.difference(release.artifacts.toSet()).toList()
        ..sort();
      throw StateError(
        'Release asset set mismatch. Missing: ${missing.join(', ')}. '
        'Extra: ${extra.join(', ')}.',
      );
    }

    final artifacts = <Map<String, Object?>>[];
    for (final name in release.artifacts.toList()..sort()) {
      final file = File(p.join(assets.path, name));
      if (file.lengthSync() == 0) {
        throw StateError('Release asset is empty: $name');
      }
      artifacts.add({
        'name': name,
        'size': file.lengthSync(),
        'sha256': await _sha256File(file),
      });
    }

    final output = Directory(outputDirectory)..createSync(recursive: true);
    final signedUpdateFiles = <String, File>{
      for (final name in const [
        'topiaforge-update-v1.json',
        'topiaforge-update-v1.json.sig',
      ])
        name: File(p.join(assets.path, name)),
    };
    for (final entry in signedUpdateFiles.entries) {
      if (FileSystemEntity.typeSync(entry.value.path, followLinks: false) !=
          FileSystemEntityType.file) {
        throw StateError(
          'Signed launcher update metadata is missing: ${entry.key}.',
        );
      }
    }
    final codeSigning = _readCodeSigningEvidence(
      File(p.join(assets.path, trustEvidenceFileName)),
      policy,
    );
    final licenseSha = policy.hasApprovedLicense
        ? await _sha256File(File(p.join(repositoryRoot, policy.licenseFile!)))
        : null;
    final gameMetadata = _readJsonObject(
      File(p.join(repositoryRoot, policy.gameBuildMetadataFile)),
    );
    final blockingReasons = <String>[
      if (!policy.hasApprovedLicense)
        'Project license is ${policy.licenseDecisionStatus}: ${policy.licenseExpression}.',
      if (release.status != 'ready')
        'Release catalog status is ${release.status}, not ready.',
    ];
    final inventory = await const ReleaseMetadataInventoryBuilder().build(
      repositoryRoot: repositoryRoot,
      policy: policy,
      release: release,
      assets: assets,
    );
    final bom = <String, Object?>{
      r'$schema':
          'https://raw.githubusercontent.com/furroxide/TopiaForge/main/schemas/topiaforge.release-bom.schema.json',
      'schemaVersion': 2,
      'version': release.version,
      'tag': release.tag,
      'targetSha': targetSha,
      'distributable': blockingReasons.isEmpty,
      'blockingReasons': blockingReasons,
      'rollback': policy.rollback,
      'gameBuild': policy.gameBuildId,
      'gameArchives': gameMetadata['archives'],
      'toolchains': _sortedMap(policy.toolchains),
      'license': {
        'spdxExpression': policy.licenseExpression,
        'decisionStatus': policy.licenseDecisionStatus,
        'file': policy.licenseFile,
        'sha256': licenseSha,
      },
      'codeSigning': codeSigning,
      'components': _sortedMap(release.components),
      'vpmPackages': _sortedMap(release.vpmPackages),
      'mods': _sortedMap(release.mods),
      'excludedDeveloperMods': _sortedMap(release.excludedDeveloperMods),
      'expectedArtifactSet': release.artifacts.toList()..sort(),
      'artifacts': artifacts,
      'ecosystem': inventory.ecosystem,
      'provenance': inventory.provenance,
      'legalInventory': inventory.legalInventory,
    };
    _validateSchema(
      repositoryRoot,
      'schemas/topiaforge.release-bom.schema.json',
      bom,
    );
    final bomFile = File(p.join(output.path, 'release-bom.json'));
    await _writeJson(bomFile, bom);

    final sbom = buildReleaseSpdxSbom(
      policy: policy,
      release: release,
      targetSha: targetSha,
      artifacts: artifacts,
    );
    _validateSchema(
      repositoryRoot,
      'schemas/topiaforge.release-spdx.schema.json',
      sbom,
    );
    final sbomFile = File(p.join(output.path, 'release-sbom.spdx.json'));
    await _writeJson(sbomFile, sbom);

    final checksumEntries = <({String name, File file})>[
      for (final name in release.artifacts)
        (name: name, file: File(p.join(assets.path, name))),
      for (final entry in signedUpdateFiles.entries)
        (name: entry.key, file: entry.value),
      (name: p.basename(bomFile.path), file: bomFile),
      (name: p.basename(sbomFile.path), file: sbomFile),
    ]..sort((left, right) => left.name.compareTo(right.name));
    final lines = <String>[];
    for (final entry in checksumEntries) {
      lines.add('${await _sha256File(entry.file)}  ${entry.name}');
    }
    final checksums = File(p.join(output.path, 'SHA256SUMS'));
    await checksums.writeAsString('${lines.join('\n')}\n', flush: true);

    await verify(
      repositoryRoot: repositoryRoot,
      version: version,
      targetSha: targetSha,
      assetsDirectory: assetsDirectory,
      metadataDirectory: outputDirectory,
      allowUnresolvedPolicy: allowUnresolvedPolicy,
    );
    return ReleaseMetadataResult(
      bomPath: bomFile.path,
      sbomPath: sbomFile.path,
      checksumsPath: checksums.path,
    );
  }

  Future<void> verify({
    required String repositoryRoot,
    required String version,
    required String targetSha,
    required String assetsDirectory,
    required String metadataDirectory,
    bool allowUnresolvedPolicy = false,
  }) async {
    final policy = TopiaForgeReleasePolicy.load(repositoryRoot);
    final release = TopiaForgeReleaseCatalog.load(
      repositoryRoot,
    ).release(version);
    final issues = await const ReleasePolicyValidator().validate(
      policy: policy,
      release: release,
      allowUnresolvedPolicy: allowUnresolvedPolicy,
    );
    if (issues.isNotEmpty) {
      throw StateError(
        'Release policy validation failed:\n- ${issues.join('\n- ')}',
      );
    }
    final metadata = Directory(metadataDirectory);
    final bomFile = File(p.join(metadata.path, 'release-bom.json'));
    final sbomFile = File(p.join(metadata.path, 'release-sbom.spdx.json'));
    final sumsFile = File(p.join(metadata.path, 'SHA256SUMS'));
    final bom = _readJsonObject(bomFile);
    final sbom = _readJsonObject(sbomFile);
    _validateSchema(
      repositoryRoot,
      'schemas/topiaforge.release-bom.schema.json',
      bom,
    );
    _validateSchema(
      repositoryRoot,
      'schemas/topiaforge.release-spdx.schema.json',
      sbom,
    );
    if (bom['version'] != version ||
        bom['targetSha'] != targetSha ||
        bom['tag'] != release.tag ||
        sbom['spdxVersion'] != 'SPDX-2.3' ||
        sbom['dataLicense'] != 'CC0-1.0' ||
        sbom['SPDXID'] != 'SPDXRef-DOCUMENT') {
      throw StateError(
        'Release BOM or SBOM metadata does not match the candidate.',
      );
    }
    final blockingReasons = (bom['blockingReasons'] as List?)
        ?.whereType<String>()
        .toList();
    if (blockingReasons == null ||
        (bom['distributable'] == true && blockingReasons.isNotEmpty) ||
        (bom['distributable'] == false && blockingReasons.isEmpty)) {
      throw StateError(
        'Release BOM distributability and blocking reasons disagree.',
      );
    }
    if (!allowUnresolvedPolicy && bom['distributable'] != true) {
      throw StateError('Publication metadata is marked non-distributable.');
    }
    final assets = Directory(assetsDirectory);
    final actualArtifacts = <Map<String, Object?>>[];
    for (final name in release.artifacts.toList()..sort()) {
      final file = File(p.join(assets.path, name));
      actualArtifacts.add({
        'name': name,
        'size': file.lengthSync(),
        'sha256': await _sha256File(file),
      });
    }
    if (jsonEncode(bom['expectedArtifactSet']) !=
            jsonEncode(release.artifacts.toList()..sort()) ||
        jsonEncode(bom['artifacts']) != jsonEncode(actualArtifacts)) {
      throw StateError(
        'Release BOM artifact inventory differs from exact bytes.',
      );
    }
    final expectedCodeSigning = _readCodeSigningEvidence(
      File(p.join(assets.path, trustEvidenceFileName)),
      policy,
    );
    if (jsonEncode(bom['codeSigning']) != jsonEncode(expectedCodeSigning)) {
      throw StateError('Release BOM code-signing evidence was changed.');
    }
    final inventory = await const ReleaseMetadataInventoryBuilder().build(
      repositoryRoot: repositoryRoot,
      policy: policy,
      release: release,
      assets: assets,
    );
    if (jsonEncode(bom['ecosystem']) != jsonEncode(inventory.ecosystem) ||
        jsonEncode(bom['provenance']) != jsonEncode(inventory.provenance) ||
        jsonEncode(bom['legalInventory']) !=
            jsonEncode(inventory.legalInventory)) {
      throw StateError(
        'Release BOM ecosystem, provenance, or legal inventory was changed.',
      );
    }
    verifyReleaseSpdxSbom(sbom, release);
    final expected = <String, File>{
      for (final name in release.artifacts)
        name: File(p.join(assetsDirectory, name)),
      for (final name in const [
        'topiaforge-update-v1.json',
        'topiaforge-update-v1.json.sig',
      ])
        name: File(p.join(assetsDirectory, name)),
      'release-bom.json': bomFile,
      'release-sbom.spdx.json': sbomFile,
    };
    final lines = sumsFile.readAsLinesSync();
    if (lines.length != expected.length) {
      throw StateError(
        'SHA256SUMS does not contain the exact expected asset set.',
      );
    }
    final seen = <String>{};
    for (final line in lines) {
      final match = RegExp(
        r'^([0-9a-f]{64})  ([A-Za-z0-9][A-Za-z0-9._+-]*)$',
      ).firstMatch(line);
      if (match == null || !seen.add(match.group(2)!)) {
        throw StateError('SHA256SUMS contains an invalid or duplicate line.');
      }
      final file = expected[match.group(2)];
      if (file == null || await _sha256File(file) != match.group(1)) {
        throw StateError(
          'SHA256SUMS verification failed for ${match.group(2)}.',
        );
      }
    }
  }
}

Map<String, String> _sortedMap(Map<String, String> source) => {
  for (final key in source.keys.toList()..sort()) key: source[key]!,
};

Future<void> _writeJson(File file, Object value) async {
  file.parent.createSync(recursive: true);
  await file.writeAsString(
    '${const JsonEncoder.withIndent('  ').convert(value)}\n',
    flush: true,
  );
}

Map<String, Object?> _readJsonObject(File file) {
  return readBoundedJsonObjectSync(file, maxBytes: CliFileLimits.metadata);
}

void _validateSchema(
  String repositoryRoot,
  String schemaPath,
  Map<String, Object?> value,
) {
  final schemaFile = File(p.join(repositoryRoot, schemaPath));
  final schema = JsonSchema.create(_readJsonObject(schemaFile));
  final result = schema.validate(value);
  if (!result.isValid) {
    throw StateError(
      '${p.basename(schemaPath)} rejected generated metadata:\n'
      '${result.errors.join('\n')}',
    );
  }
}

bool _sameSet(Set<String> left, Set<String> right) =>
    left.length == right.length && left.containsAll(right);

Future<String> _sha256File(File file) async =>
    (await sha256.bind(file.openRead()).single).toString();

Map<String, Object?> _readCodeSigningEvidence(
  File file,
  TopiaForgeReleasePolicy policy,
) {
  final json = _readJsonObject(file);
  const platforms = {'windows-x64', 'linux-x64', 'macos-universal'};
  if (!_sameSet(json.keys.toSet(), platforms)) {
    throw StateError(
      '${TopiaForgeReleaseMetadataBuilder.trustEvidenceFileName} must '
      'contain the exact supported platform set.',
    );
  }
  final normalized = <String, Object?>{};
  for (final platform in platforms.toList()..sort()) {
    final value = json[platform];
    if (value is! Map) {
      throw StateError('Code-signing evidence for $platform is invalid.');
    }
    final entry = Map<String, Object?>.from(value);
    final status = entry['status'];
    final exceptionApplied = entry['exceptionApplied'];
    if (entry.length != 2 || status is! String || exceptionApplied is! bool) {
      throw StateError('Code-signing evidence for $platform is invalid.');
    }
    switch (platform) {
      case 'windows-x64':
        if (status == 'trusted' && !exceptionApplied) break;
        if (status == 'unsigned' &&
            exceptionApplied &&
            policy.allowsUnsignedWindows) {
          break;
        }
        throw StateError(
          'Windows code-signing evidence is not permitted by release policy.',
        );
      case 'macos-universal':
        if (status == 'trusted' && !exceptionApplied) break;
        if (status == 'ad-hoc' && exceptionApplied && policy.allowsAdHocMacOS) {
          break;
        }
        throw StateError(
          'macOS code-signing evidence is not permitted by release policy.',
        );
      case 'linux-x64':
        if (status != 'not-applicable' || exceptionApplied) {
          throw StateError('Linux code-signing evidence is invalid.');
        }
    }
    normalized[platform] = {
      'status': status,
      'exceptionApplied': exceptionApplied,
    };
  }
  return {
    'exceptionVersion': policy.codeSigningException?.version,
    'platforms': normalized,
  };
}
