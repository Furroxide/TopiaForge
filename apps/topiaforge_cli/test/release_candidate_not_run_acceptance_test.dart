import 'dart:convert';
import 'dart:io';

import 'package:json_schema/json_schema.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/release_candidate_acceptance.dart';
import 'package:topiaforge/src/release_handoff.dart';
import 'package:topiaforge/src/release_handoff_models.dart';
import 'package:topiaforge/src/release_readiness.dart'
    show gameAcceptanceDispositionEvidenceId;

import 'release_candidate_acceptance_fixture.dart';

/// The `not-run` acceptance record: live game acceptance was skipped under the
/// owner's P0-GAME-01 disposition. It must say so, bind the same candidate and
/// Unity authoring receipt, and never pass for, or pair with, a performed run.
void main() {
  final root = p.normalize(p.join(Directory.current.path, '..', '..'));
  final live = _json(p.join(root, 'tests/live-game-acceptance.json'));
  final redesign = _json(
    p.join(root, 'tests/gamemode-release-acceptance.json'),
  );
  final metadata = _json(p.join(root, '.github/robotopia-game-build.json'));
  final inventorySha = 'c' * 64;
  final schema = JsonSchema.create(
    _json(
      p.join(
        root,
        'schemas/topiaforge.release-candidate-acceptance-v1.schema.json',
      ),
    ),
  );
  Map<String, Object?> trackedGame() => {
    'id': 'P0-GAME-01',
    'priority': 'P0',
    'enforcement': 'advisory',
    'status': 'blocked',
    'reasonCode': 'acceptance-evidence-missing',
    'reviewerRoles': ['robotopia-owner', 'runtime-mod-qa'],
    'evidenceIds': <String>[],
  };
  final approvedGame = <String, Object?>{
    'id': 'P0-GAME-01',
    'status': 'approved',
    'reviewerRoles': ['robotopia-owner', 'runtime-mod-qa'],
    'evidenceIds': ['EVID-P0-GAME-01-0001'],
  };
  late Map<String, Object?> decision;
  late ReleaseHandoffVerification handoff;
  late Map<String, Object?> acceptance;

  setUp(() {
    decision = {
      'gates': [trackedGame()],
    };
    handoff = _handoff(metadata, _notRunQa(metadata, inventorySha));
    acceptance = _record(metadata, redesign, handoff);
  });

  void validate() => validateCandidateAcceptance(
    acceptance: acceptance,
    decision: decision,
    gameMetadata: metadata,
    liveInventory: live,
    redesignInventory: redesign,
    handoff: handoff,
  );

  test('a complete not-run record is schema valid and accepted', () {
    final result = schema.validate(acceptance);
    expect(result.isValid, isTrue, reason: result.errors.join('\n'));
    final before = jsonEncode(acceptance);
    validate();
    expect(jsonEncode(acceptance), before);
  });

  for (final mutation in <String, void Function(Map<String, Object?>)>{
    'extra cases': (v) => v['cases'] = [
      {'id': 'case.one', 'result': 'passed', 'evidenceSha256': 'd' * 64},
    ],
    'extra isolation': (v) => v['isolation'] = {
      'kind': 'windows-user',
      'evidenceSha256': 'd' * 64,
      'persistentDataIsolated': true,
      'normalUserDataAccessed': false,
    },
    'extra game cycles': (v) => v['gameCycles'] = 10,
    'extra game evidence': (v) => v['gameEvidenceSha256'] = 'd' * 64,
    'extra reviewer evidence': (v) => v['reviewerEvidence'] = [
      {
        'evidenceId': 'EVID-P0-GAME-01-0001',
        'role': 'robotopia-owner',
        'reference': 'review:fixture',
        'sha256': 'd' * 64,
      },
    ],
    'missing disposition': (v) => v.remove('disposition'),
    'missing authoring digest': (v) => v.remove('authoringEvidenceSha256'),
    'short authoring cycles': (v) => v['authoringCycles'] = 15,
    'wrong-role disposition': (v) =>
        (v['disposition']! as Map)['role'] = 'robotopia-owner',
    'reviewer-role disposition': (v) =>
        (v['disposition']! as Map)['role'] = 'runtime-mod-qa',
    'other-gate disposition': (v) =>
        (v['disposition']! as Map)['evidenceId'] = 'EVID-P0-IP-01-0001',
    'unrecorded same-gate disposition': (v) =>
        (v['disposition']! as Map)['evidenceId'] = 'EVID-P0-GAME-01-0002',
    'unscoped disposition reference': (v) =>
        (v['disposition']! as Map)['reference'] = r'C:\private\owner.txt',
    'zero disposition digest': (v) =>
        (v['disposition']! as Map)['sha256'] = '0' * 64,
    'extra disposition field': (v) =>
        (v['disposition']! as Map)['approved'] = true,
    'unknown result': (v) => v['result'] = 'skipped',
  }.entries) {
    test('schema and reader reject ${mutation.key}', () {
      mutation.value(acceptance);
      expect(schema.validate(acceptance).isValid, isFalse);
      expect(validate, throwsStateError);
    });
  }

  test('the schema pins the disposition the reader requires', () {
    final document = _json(
      p.join(
        root,
        'schemas/topiaforge.release-candidate-acceptance-v1.schema.json',
      ),
    );
    final properties = document['properties']! as Map;
    final disposition = properties['disposition']! as Map;
    final evidenceId =
        (disposition['properties']! as Map)['evidenceId']! as Map;
    expect(evidenceId['const'], gameAcceptanceDispositionEvidenceId);
  });

  test('a passed record cannot carry a disposition', () {
    decision = {
      'gates': [approvedGame],
    };
    handoff = _handoff(metadata, _passQa(metadata, live, inventorySha));
    acceptance = candidateAcceptanceFixture(
      decision: decision,
      gameMetadata: metadata,
      redesignInventory: redesign,
      handoff: handoff,
      contractSha256: 'a' * 64,
      handoffSha256: 'b' * 64,
      payloads: [handoff.platformBundles['windows-x64']!.archive.toJson()],
    );
    validate();
    acceptance['disposition'] = _record(
      metadata,
      redesign,
      handoff,
    )['disposition'];
    expect(schema.validate(acceptance).isValid, isFalse);
    expect(validate, throwsStateError);
  });

  test('a stale authoring digest is rejected', () {
    acceptance['authoringEvidenceSha256'] = 'd' * 64;
    expect(schema.validate(acceptance).isValid, isTrue);
    expect(validate, throwsStateError);
  });

  for (final gate in <String, Map<String, Object?> Function()>{
    'an approved GAME row': () => approvedGame,
    'a blocking GAME row': () => trackedGame()..['enforcement'] = 'blocking',
    'a GAME row carrying evidence': () =>
        trackedGame()..['evidenceIds'] = ['EVID-P0-GAME-01-0001'],
    'a GAME row with another reason': () =>
        trackedGame()..['reasonCode'] = 'approval-evidence-missing',
  }.entries) {
    test('a not-run record cannot accompany ${gate.key}', () {
      decision = {
        'gates': [gate.value()],
      };
      expect(validate, throwsStateError);
    });
  }

  test('a not-run record over a performed handoff is rejected', () {
    handoff = _handoff(metadata, _passQa(metadata, live, inventorySha));
    acceptance = _record(metadata, redesign, handoff);
    expect(schema.validate(acceptance).isValid, isTrue);
    expect(validate, throwsStateError);
  });

  test('a passed record over a not-run handoff is rejected', () {
    decision = {
      'gates': [approvedGame],
    };
    final passed = _handoff(metadata, _passQa(metadata, live, inventorySha));
    acceptance = candidateAcceptanceFixture(
      decision: decision,
      gameMetadata: metadata,
      redesignInventory: redesign,
      handoff: passed,
      contractSha256: 'a' * 64,
      handoffSha256: 'b' * 64,
      payloads: [passed.platformBundles['windows-x64']!.archive.toJson()],
    );
    expect(schema.validate(acceptance).isValid, isTrue);
    expect(validate, throwsStateError);
  });

  for (final mutation in <String, void Function(Map<String, Object?>)>{
    'a receipt for other game bytes': (qa) =>
        (qa['robotopia']! as Map)['gameArchiveSha256'] = 'd' * 64,
    'a receipt with another file count': (qa) =>
        (qa['robotopia']! as Map)['gameFilesVerified'] = 1,
    'a receipt claiming a pass': (qa) =>
        (qa['robotopia']! as Map)['result'] = 'pass',
    'a receipt with run evidence': (qa) =>
        (qa['robotopia']! as Map)['evidenceSha256'] = 'd' * 64,
    'a short Unity receipt': (qa) => (qa['unity']! as Map)['cycles'] = 15,
    'a failed Unity smoke': (qa) =>
        (qa['unity']! as Map)['validatorSmoke'] = false,
    'another game build': (qa) => qa['gameBuildId'] = 1,
  }.entries) {
    test('a not-run record rejects ${mutation.key}', () {
      final qa = _notRunQa(metadata, inventorySha);
      mutation.value(qa);
      handoff = _handoff(metadata, qa);
      expect(validate, throwsStateError);
    });
  }
}

Map<String, Object?> _record(
  Map<String, Object?> metadata,
  Map<String, Object?> redesign,
  ReleaseHandoffVerification handoff,
) => candidateNotRunAcceptanceFixture(
  gameMetadata: metadata,
  redesignInventory: redesign,
  handoff: handoff,
  contractSha256: 'a' * 64,
  handoffSha256: 'b' * 64,
  payloads: [handoff.platformBundles['windows-x64']!.archive.toJson()],
);

Map<String, Object?> _notRunQa(
  Map<String, Object?> metadata,
  String inventorySha,
) {
  final manifest = metadata['windowsFilesManifest']! as Map;
  return {
    'gameBuildId': metadata['buildId'],
    'robotopia': <String, Object?>{
      'result': 'not-run',
      'gameArchiveSha256':
          ((metadata['archives']! as Map)['windows'] as Map)['sha256'],
      'gameExecutableSha256': manifest['gameExecutableSha256'],
      'gameFilesManifestSha256': manifest['sha256'],
      'gameFilesVerified': manifest['fileCount'],
      'caseInventorySha256': inventorySha,
    },
    'unity': _unity(),
  };
}

Map<String, Object?> _passQa(
  Map<String, Object?> metadata,
  Map<String, Object?> live,
  String inventorySha,
) {
  final ids = [for (final item in live['cases']! as List) (item as Map)['id']]
    ..sort();
  return {
    'gameBuildId': metadata['buildId'],
    'robotopia': <String, Object?>{
      'result': 'pass',
      'suite': 'full',
      'evidenceSha256': 'e' * 64,
      'caseInventorySha256': inventorySha,
      'requiredCases': ids,
      'passedCases': ids,
      'missingCases': <String>[],
      'failures': <String>[],
    },
    'unity': _unity(),
  };
}

Map<String, Object?> _unity() => <String, Object?>{
  'result': 'pass',
  'cycles': 16,
  'validatorSmoke': true,
  'evidenceSha256': 'f' * 64,
};

Map<String, Object?> _json(String file) =>
    (jsonDecode(File(file).readAsStringSync()) as Map).cast<String, Object?>();

ReleaseHandoffVerification _handoff(
  Map<String, Object?> metadata,
  Map<String, Object?> qa,
) {
  final bundle = ReleasePlatformBundle(
    version: '0.1.0-rc.1',
    targetSha: '1' * 40,
    platform: 'windows-x64',
    builderProfile: 'windows-x64',
    archive: ReleaseHandoffFile(
      name: 'TopiaForge-windows-x64.zip',
      size: 12,
      sha256: 'a' * 64,
    ),
    canonicalEcosystemSha256: 'a' * 64,
    toolchains: const {},
    platformToolchains: const {},
    signing: const ReleaseHandoffSigning(
      scheme: 'none',
      status: 'unsigned',
      notarization: 'not-applicable',
      exceptionApplied: false,
    ),
    validations: const {},
    qa: qa,
  );
  return ReleaseHandoffVerification(
    handoff: ReleaseHandoffManifest(
      version: bundle.version,
      targetSha: bundle.targetSha,
      canonicalEcosystemSha256: bundle.canonicalEcosystemSha256,
      toolchains: const {},
      platformBundles: const [],
    ),
    platformBundles: {'windows-x64': bundle},
  );
}
