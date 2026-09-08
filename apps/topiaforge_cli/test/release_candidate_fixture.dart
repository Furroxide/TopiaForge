import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:archive/archive.dart';
import 'package:topiaforge/src/release_ecosystem_identity.dart';
import 'package:path/path.dart' as p;
import 'package:topiaforge/src/release_candidate_contract.dart';
import 'package:topiaforge/src/release_candidate_qualification.dart';
import 'package:topiaforge/src/release_handoff.dart';
import 'package:topiaforge/src/release_handoff_models.dart';
import 'package:topiaforge/src/release_policy.dart';

import 'release_handoff_qa_fixture.dart';
import 'release_candidate_acceptance_fixture.dart';

/// Entirely synthetic approvals and payloads; never production release evidence.
final class CandidateFixture {
  CandidateFixture._(this.root, this.assets, this.sha, this.contract);
  static const version = '0.1.0-rc.1';
  final Directory root;
  final Directory assets;
  final String sha;
  final ReleaseCandidateContract contract;

  static Future<CandidateFixture> create({
    void Function(Directory root)? mutateContracts,
    bool validArchive = true,
  }) async {
    var repository = Directory.current.absolute;
    while (!File(p.join(repository.path, 'TopiaForge.slnx')).existsSync()) {
      repository = repository.parent;
    }
    final root = Directory.systemTemp.createTempSync('candidate-fixture-');
    try {
      for (final name in candidateContractPaths) {
        final file = File(p.join(root.path, name));
        file.parent.createSync(recursive: true);
        file.writeAsBytesSync(
          File(p.join(repository.path, name)).readAsBytesSync(),
        );
      }
      final readinessFile = File(
        p.join(root.path, 'release/release-readiness.json'),
      );
      final readiness = readObject(readinessFile);
      for (final gate in readiness['gates'] as List) {
        if (gate['enforcement'] == 'blocking' && gate['id'] != 'P0-GAME-01') {
          gate['status'] = 'approved';
          gate.remove('reasonCode');
          gate['evidenceIds'] = ['EVID-${gate['id']}-0001'];
        }
      }
      writeObject(readinessFile, readiness);
      final catalogFile = File(p.join(root.path, 'release/catalog.json'));
      final catalog = readObject(catalogFile);
      (catalog['releases'] as List).single['status'] = 'ready';
      writeObject(catalogFile, catalog);
      // Keep fixture intent explicit even when the production policy changes.
      final policyFile = File(p.join(root.path, 'release/release-policy.json'));
      writeObject(
        policyFile,
        readObject(policyFile)..['signingIdentities'] = <String, Object?>{},
      );
      mutateContracts?.call(root);
      git(root, ['init', '--quiet']);
      git(root, ['config', 'user.name', 'Synthetic Candidate Test']);
      git(root, ['config', 'user.email', 'candidate@example.invalid']);
      git(root, ['add', '--', ...candidateContractPaths]);
      git(root, [
        '-c',
        'commit.gpgsign=false',
        'commit',
        '--quiet',
        '-m',
        'test: freeze synthetic candidate',
      ]);
      final sha = git(root, ['rev-parse', 'HEAD']);
      final contract = await ReleaseCandidateContract.load(root.path, sha);
      final assets = Directory(p.join(root.path, 'assets'))..createSync();
      final fixture = CandidateFixture._(root, assets, sha, contract);
      await fixture._build(readiness, validArchive: validArchive);
      return fixture;
    } catch (_) {
      root.deleteSync(recursive: true);
      rethrow;
    }
  }

  File asset(String name) => File(p.join(assets.path, name));
  Map<String, Object?> get decision =>
      readObject(asset(candidateReadinessName));
  Map<String, Object?> get acceptance =>
      readObject(asset(candidateAcceptanceName));
  void writeDecision(Map<String, Object?> value) =>
      writeObject(asset(candidateReadinessName), value);
  void writeAcceptance(Map<String, Object?> value, {bool rebind = true}) {
    writeObject(asset(candidateAcceptanceName), value);
    if (rebind) {
      writeDecision(
        decision
          ..['acceptanceSha256'] = fileDigest(asset(candidateAcceptanceName)),
      );
    }
  }

  Future<ReleaseCandidateQualification> qualify() =>
      ReleaseCandidateQualification.loadAtGitSha(
        repositoryRoot: root.path,
        targetSha: sha,
        expectedReleaseVersion: version,
        assetsDirectory: assets.path,
      );
  void dispose() => root.deleteSync(recursive: true);

  Future<void> _build(
    Map<String, Object?> baseReadiness, {
    required bool validArchive,
  }) async {
    final release = TopiaForgeReleaseCatalog.load(root.path).release(version);
    final policy = TopiaForgeReleasePolicy.load(root.path);
    final unsigned = policy.windowsDistribution == 'unsigned';
    for (final name in release.artifacts) {
      asset(name).writeAsStringSync('synthetic:$name');
    }
    final entries = <String, List<int>>{
      for (final mod in release.mods.entries)
        '${mod.key}-${mod.value}.topiaforgemod': asset(
          '${mod.key}-${mod.value}.topiaforgemod',
        ).readAsBytesSync(),
      'vpm/index.json': utf8.encode('synthetic-vpm-index'),
      for (final vpm in release.vpmPackages.entries)
        'vpm/${vpm.key}-${vpm.value}.zip': utf8.encode(
          'synthetic-vpm:${vpm.key}',
        ),
    };
    final ecosystem = ReleaseEcosystemIdentity.digestRecords({
      for (final entry in entries.entries)
        entry.key: sha256.convert(entry.value).toString(),
    });
    if (validArchive) {
      final archive = Archive();
      for (final entry in entries.entries) {
        archive.addFile(
          ArchiveFile('dist/${entry.key}', entry.value.length, entry.value),
        );
      }
      asset(
        'TopiaForge-windows-x64.zip',
      ).writeAsBytesSync(ZipEncoder().encode(archive));
    }
    writeReleaseQaFixtures(
      repositoryRoot: root.path,
      assets: assets,
      version: version,
      targetSha: sha,
      ecosystemSha: ecosystem,
      windowsDistribution: unsigned ? 'unsigned' : 'signed',
    );
    const handoffApi = TopiaForgeReleaseHandoff();
    await handoffApi.buildPlatformBundle(
      repositoryRoot: root.path,
      version: version,
      targetSha: sha,
      platform: 'windows-x64',
      archivePath: asset('TopiaForge-windows-x64.zip').path,
      canonicalEcosystemSha256: ecosystem,
      evidenceSha256: releaseQaEvidenceFor(
        assets,
        'windows-x64',
        ecosystemSha: ecosystem,
        windowsDistribution: unsigned ? 'unsigned' : 'signed',
      ),
      qaPath: releaseQaPath(assets, 'windows-x64'),
      outputPath: asset(releasePlatformBundleFileName('windows-x64')).path,
    );
    await handoffApi.buildHandoff(
      repositoryRoot: root.path,
      version: version,
      targetSha: sha,
      assetsDirectory: assets.path,
    );
    final handoff = await handoffApi.verify(
      repositoryRoot: root.path,
      version: version,
      targetSha: sha,
      assetsDirectory: assets.path,
    );
    asset('windows-qa-summary.json').deleteSync();
    if (!unsigned) {
      asset('release-handoff-v1.json.p7s').writeAsStringSync(
        'synthetic CMS identity only; trust is tested separately',
      );
    }
    final gates = baseReadiness['gates'] as List;
    final game = gates.singleWhere((gate) => gate['id'] == 'P0-GAME-01') as Map;
    game['status'] = 'approved';
    game.remove('reasonCode');
    game['evidenceIds'] = ['EVID-P0-GAME-01-0001'];
    final names = release.artifacts.toList()..sort();
    final decision = <String, Object?>{
      'schema': 'release-candidate-readiness-v1',
      'repository': 'Furroxide/TopiaForge',
      'releaseVersion': version,
      'targetSha': sha,
      'status': 'ready',
      'baseReadinessSha256': contract.digest('release/release-readiness.json'),
      'baseSchemaSha256': contract.digest(
        'schemas/topiaforge.release-readiness-v1.schema.json',
      ),
      'policySha256': contract.digest('release/release-policy.json'),
      'catalogSha256': contract.digest('release/catalog.json'),
      'contractSha256': contract.contractSha256,
      'handoffSha256': fileDigest(asset(releaseHandoffFileName)),
      'acceptanceSha256': '0' * 64,
      if (!unsigned)
        'handoffSignatureSha256': fileDigest(
          asset('release-handoff-v1.json.p7s'),
        ),
      'payloads': [
        for (final name in names)
          {
            'name': name,
            'size': asset(name).lengthSync(),
            'sha256': fileDigest(asset(name)),
          },
      ],
      'gates': gates,
    };
    writeDecision(decision);
    writeAcceptance(
      candidateAcceptanceFixture(
        decision: decision,
        repository: 'Furroxide/TopiaForge',
        contractSha256: contract.contractSha256,
        handoffSha256: decision['handoffSha256']! as String,
        payloads: (decision['payloads']! as List).cast<Map<String, Object?>>(),
        gameMetadata: contract.object('.github/robotopia-game-build.json'),
        redesignInventory: contract.object(
          'tests/gamemode-release-acceptance.json',
        ),
        handoff: handoff,
      ),
    );
  }
}

String fileDigest(File file) =>
    sha256.convert(file.readAsBytesSync()).toString();
Map<String, Object?> readObject(File file) =>
    (jsonDecode(file.readAsStringSync()) as Map).cast<String, Object?>();
void writeObject(File file, Object? value) =>
    file.writeAsStringSync('${jsonEncode(value)}\n');
String git(Directory root, List<String> args) {
  final result = Process.runSync(
    'git',
    args,
    workingDirectory: root.path,
    stdoutEncoding: utf8,
    stderrEncoding: utf8,
  );
  if (result.exitCode != 0) throw StateError('${result.stderr}');
  return (result.stdout as String).trim();
}
