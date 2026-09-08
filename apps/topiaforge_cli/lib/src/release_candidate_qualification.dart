import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:path/path.dart' as p;

import 'bounded_file_reader.dart';
import 'release_candidate_acceptance.dart';
import 'release_candidate_contract.dart';
import 'release_candidate_payloads.dart';
import 'release_candidate_inventory.dart';
import 'release_handoff.dart';
import 'release_handoff_models.dart';
import 'release_readiness.dart';
import 'release_strict_json.dart';

/// Immutable qualification of one frozen source and its exact candidate bytes.
/// Validates evidence bindings; human review remains an external requirement.
final class ReleaseCandidateQualification {
  ReleaseCandidateQualification._(Map<String, Object?> summary, this.gates)
    : _summary = jsonEncode(summary);

  final String _summary;
  final List<ReleaseReadinessGateDecision> gates;
  String _digest(String key) => toPublicSummary()[key]! as String;
  String get decisionSha256 => _digest('decisionSha256');
  String get acceptanceSha256 => _digest('acceptanceSha256');
  String get handoffSha256 => _digest('handoffSha256');
  String get baseReadinessSha256 => _digest('baseReadinessSha256');
  String get baseSchemaSha256 => _digest('baseSchemaSha256');
  String get policySha256 => _digest('policySha256');
  String get catalogSha256 => _digest('catalogSha256');
  String get contractSha256 => _digest('contractSha256');
  String get status => 'ready';
  bool get isReady => true;
  Map<String, Object?> toPublicSummary() =>
      (jsonDecode(_summary) as Map).cast<String, Object?>();
  Map<String, Object?> toBomJson() => {
    'binding': 'detached-candidate-at-target-sha',
    'path': candidateReadinessName,
    'schemaPath': candidateReadinessSchemaPath,
    'acceptancePath': candidateAcceptanceName,
    'status': status,
    'blobSha256': decisionSha256,
    'summary': toPublicSummary(),
  };

  static Future<ReleaseCandidateQualification> loadAtGitSha({
    required String repositoryRoot,
    required String targetSha,
    required String expectedReleaseVersion,
    required String assetsDirectory,
    String expectedRepository = 'Furroxide/TopiaForge',
  }) async {
    final contract = await ReleaseCandidateContract.load(
      repositoryRoot,
      targetSha,
    );
    final base = ReleaseReadinessDecision.fromCandidateBlobs(
      readinessBytes: contract.blobs[releaseReadinessPath]!,
      schemaBytes: contract.blobs[releaseReadinessSchemaPath]!,
      targetSha: targetSha,
      expectedReleaseVersion: expectedReleaseVersion,
    );
    for (final gate in base.gates) {
      if (gate.id != 'P0-GAME-01' && gate.blocksRelease) {
        throw StateError('Required tracked approval is missing: ${gate.id}.');
      }
    }
    final assets = Directory(assetsDirectory).absolute;
    if (FileSystemEntity.typeSync(assets.path, followLinks: false) !=
            FileSystemEntityType.directory ||
        !p.equals(assets.path, assets.resolveSymbolicLinksSync())) {
      throw StateError(
        'Candidate assets must be a real directory without redirected ancestors.',
      );
    }
    final documents = <String, List<int>>{};
    Map<String, Object?> read(String name, {int limit = 256 * 1024}) {
      final bytes = readBoundedRegularFileSync(
        File(p.join(assets.path, name)),
        maxBytes: limit,
      );
      documents[name] = bytes;
      return decodeReleaseObject(bytes, maximumBytes: limit, label: name);
    }

    final decision = read(candidateReadinessName, limit: 128 * 1024);
    validateReleaseSchema(
      decision,
      contract.object(candidateReadinessSchemaPath),
      'Candidate decision',
    );
    final bindings = <String, Object?>{
      'repository': expectedRepository,
      'releaseVersion': expectedReleaseVersion,
      'targetSha': targetSha,
      'status': 'ready',
      'baseReadinessSha256': contract.digest(releaseReadinessPath),
      'baseSchemaSha256': contract.digest(releaseReadinessSchemaPath),
      'policySha256': contract.digest('release/release-policy.json'),
      'catalogSha256': contract.digest('release/catalog.json'),
      'contractSha256': contract.contractSha256,
    };
    for (final entry in bindings.entries) {
      if (decision[entry.key] != entry.value) {
        throw StateError(
          'Candidate ${entry.key} does not match its frozen source.',
        );
      }
    }
    final effective = _effectiveGates(
      contract,
      decision,
      targetSha,
      expectedReleaseVersion,
    );
    final policy = contract.object('release/release-policy.json');
    validateReleaseSchema(
      policy,
      contract.object('schemas/topiaforge.release-policy.schema.json'),
      'Release policy',
    );
    final catalog = contract.object('release/catalog.json');
    validateReleaseSchema(
      catalog,
      contract.object('schemas/topiaforge.release-catalog.schema.json'),
      'Release catalog',
    );
    final releases = (catalog['releases'] as List)
        .where((entry) => (entry as Map)['version'] == expectedReleaseVersion)
        .toList();
    if (releases.length != 1 ||
        (releases.single as Map)['status'] != 'ready' ||
        (releases.single as Map)['tag'] != 'v$expectedReleaseVersion') {
      throw StateError(
        'The exact source catalog inventory must be reviewed and ready.',
      );
    }
    final release = releases.single as Map;
    final artifactPolicy = policy['artifactPolicy']! as Map;
    final names = candidateNames(release['artifacts'], 'Catalog payloads');
    final archives = candidateNames(
      artifactPolicy['platformArchives'],
      'Platform archives',
    );
    final generated = candidateNames(
      artifactPolicy['generatedMetadata'],
      'Generated metadata',
    );
    validateCandidateInventory(
      policy: policy,
      release: release,
      names: names,
      archives: archives,
      generated: generated,
    );
    if (archives.any(
          (name) =>
              !name.startsWith('TopiaForge-') ||
              !name.endsWith('.zip') ||
              !names.contains(name),
        ) ||
        names.any(generated.contains) ||
        names.any(
          (name) =>
              name == candidateReadinessName ||
              name == candidateAcceptanceName ||
              name.startsWith('release-handoff-') ||
              name.startsWith('release-platform-bundle-'),
        )) {
      throw StateError(
        'Catalog payloads and generated qualification metadata must be disjoint.',
      );
    }
    final unsigned =
        (policy['signingIdentities']! as Map)['windowsDistribution'] ==
        'unsigned';
    final signatureName = '$releaseHandoffFileName.p7s';
    if (unsigned) {
      if (decision.containsKey('handoffSignatureSha256') ||
          FileSystemEntity.typeSync(
                p.join(assets.path, signatureName),
                followLinks: false,
              ) !=
              FileSystemEntityType.notFound) {
        throw StateError(
          'Unsigned candidate must omit detached CMS signature and its digest.',
        );
      }
    } else {
      final signature = readBoundedRegularFileSync(
        File(p.join(assets.path, signatureName)),
        maxBytes: 256 * 1024,
      );
      documents[signatureName] = signature;
      if (decision['handoffSignatureSha256'] !=
          sha256.convert(signature).toString()) {
        throw StateError(
          'Signed candidate must bind its exact detached CMS signature.',
        );
      }
    }
    final handoff = read(releaseHandoffFileName);
    if (decision['handoffSha256'] !=
        sha256.convert(documents[releaseHandoffFileName]!).toString()) {
      throw StateError('Candidate handoff digest changed.');
    }
    // Strict decoding occurs before the existing handoff reader can lose duplicate fields.
    for (final archive in archives) {
      read(
        'release-platform-bundle-v1-${archive.substring(11, archive.length - 4)}.json',
      );
    }
    if (handoff['targetSha'] != targetSha) {
      throw StateError('Candidate handoff source changed.');
    }
    final acceptance = read(candidateAcceptanceName);
    if (decision['acceptanceSha256'] !=
        sha256.convert(documents[candidateAcceptanceName]!).toString()) {
      throw StateError('Candidate acceptance digest changed.');
    }
    validateReleaseSchema(
      acceptance,
      contract.object(candidateAcceptanceSchemaPath),
      'Candidate acceptance',
    );
    for (final key in [
      'repository',
      'releaseVersion',
      'targetSha',
      'contractSha256',
      'handoffSha256',
      'payloads',
    ]) {
      if (canonicalReleaseJson(acceptance[key]) !=
          canonicalReleaseJson(decision[key])) {
        throw StateError('Acceptance $key is not bound to this candidate.');
      }
    }
    final actualPayloads = await inspectCandidatePayloads(
      assets: assets,
      names: names,
      archives: archives,
      generatedMetadata: generated,
      unsigned: unsigned,
    );
    if (canonicalReleaseJson(decision['payloads']) !=
            canonicalReleaseJson(actualPayloads) ||
        (decision['payloads'] as List).any(
          (entry) => (entry as Map)['size'] is! int,
        )) {
      throw StateError(
        'Candidate payload inventory does not match exact sorted bytes.',
      );
    }
    final verified = await contract.withSnapshot(
      (snapshot) => const TopiaForgeReleaseHandoff().verify(
        repositoryRoot: snapshot,
        version: expectedReleaseVersion,
        targetSha: targetSha,
        assetsDirectory: assets.path,
        verifyEmbeddedEcosystem: true,
        verifyCanonicalPackages: true,
      ),
    );
    validateCandidateAcceptance(
      acceptance: acceptance,
      decision: decision,
      gameMetadata: contract.object('.github/robotopia-game-build.json'),
      liveInventory: contract.object('tests/live-game-acceptance.json'),
      redesignInventory: contract.object(
        'tests/gamemode-release-acceptance.json',
      ),
      handoff: verified,
    );
    // Catch replacement between the independent readers and evidence checks.
    for (final entry in documents.entries) {
      final current = readBoundedRegularFileSync(
        File(p.join(assets.path, entry.key)),
        maxBytes: 256 * 1024,
      );
      if (sha256.convert(current) != sha256.convert(entry.value)) {
        throw StateError(
          'Candidate record changed while qualifying: ${entry.key}.',
        );
      }
    }
    final finalPayloads = await inspectCandidatePayloads(
      assets: assets,
      names: names,
      archives: archives,
      generatedMetadata: generated,
      unsigned: unsigned,
    );
    if (canonicalReleaseJson(finalPayloads) !=
        canonicalReleaseJson(actualPayloads)) {
      throw StateError('Candidate payload set changed while qualifying.');
    }
    return ReleaseCandidateQualification._({
      'schema': 'release-candidate-readiness-summary-v1',
      ...bindings,
      'decisionSha256': sha256
          .convert(documents[candidateReadinessName]!)
          .toString(),
      'acceptanceSha256': decision['acceptanceSha256'],
      'handoffSha256': decision['handoffSha256'],
      'payloads': actualPayloads,
      'gates': effective.gates.map((gate) => gate.toPublicSummary()).toList(),
    }, effective.gates);
  }
}

ReleaseReadinessDecision _effectiveGates(
  ReleaseCandidateContract contract,
  Map<String, Object?> decision,
  String targetSha,
  String version,
) {
  final base = contract.object(releaseReadinessPath);
  final baseGates = base['gates'] as List;
  final gates = decision['gates'] as List;
  if (gates.length != baseGates.length) {
    throw StateError('Candidate must retain all tracked gates.');
  }
  for (var index = 0; index < baseGates.length; index++) {
    final original = baseGates[index] as Map;
    final actual = gates[index] as Map;
    if (original['id'] == 'P0-GAME-01') {
      if (actual['id'] != original['id'] || actual['status'] != 'approved') {
        throw StateError('Candidate game approval is required.');
      }
    } else if (canonicalReleaseJson(original) != canonicalReleaseJson(actual)) {
      throw StateError('Only P0-GAME-01 may supersede its tracked gate.');
    }
  }
  base['gates'] = gates;
  base['status'] = 'ready';
  final effective = ReleaseReadinessDecision.fromCandidateBlobs(
    readinessBytes: utf8.encode(jsonEncode(base)),
    schemaBytes: contract.blobs[releaseReadinessSchemaPath]!,
    targetSha: targetSha,
    expectedReleaseVersion: version,
  );
  if (!effective.isReady) throw StateError('Candidate gates are not ready.');
  return effective;
}
