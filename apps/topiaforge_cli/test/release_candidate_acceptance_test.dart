import 'dart:convert';
import 'dart:io';

import 'package:json_schema/json_schema.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/release_candidate_acceptance.dart';
import 'package:topiaforge/src/release_handoff.dart';
import 'package:topiaforge/src/release_handoff_models.dart';

import 'release_candidate_acceptance_fixture.dart';

void main() {
  final root = p.normalize(p.join(Directory.current.path, '..', '..'));
  final live = _json(p.join(root, 'tests/live-game-acceptance.json'));
  final redesign = _json(
    p.join(root, 'tests/gamemode-release-acceptance.json'),
  );
  final metadata = _json(p.join(root, '.github/robotopia-game-build.json'));
  final decision = <String, Object?>{
    'gates': [
      {
        'id': 'P0-GAME-01',
        'status': 'approved',
        'reviewerRoles': ['robotopia-owner', 'runtime-mod-qa'],
        'evidenceIds': ['EVID-P0-GAME-01-0001', 'EVID-P0-GAME-01-0002'],
      },
    ],
  };
  final handoff = _handoff(live, metadata);
  late Map<String, Object?> acceptance;
  setUp(() {
    acceptance = candidateAcceptanceFixture(
      decision: decision,
      gameMetadata: metadata,
      redesignInventory: redesign,
      handoff: handoff,
      contractSha256: 'a' * 64,
      handoffSha256: 'b' * 64,
      payloads: [handoff.platformBundles['windows-x64']!.archive.toJson()],
    );
  });
  void validate() => validateCandidateAcceptance(
    acceptance: acceptance,
    decision: decision,
    gameMetadata: metadata,
    liveInventory: live,
    redesignInventory: redesign,
    handoff: handoff,
  );
  test('complete exact candidate acceptance is valid', validate);
  test('acceptance reader preserves payload SemVer build metadata', () {
    const name =
        'io.github.furroxide.topiaforge.worlds-0.1.0-rc.1+build.01.topiaforgemod';
    ((acceptance['payloads'] as List).first as Map)['name'] = name;
    validate();
    expect(((acceptance['payloads'] as List).first as Map)['name'], name);
  });
  final schema = JsonSchema.create(
    _json(
      p.join(
        root,
        'schemas/topiaforge.release-candidate-acceptance-v1.schema.json',
      ),
    ),
  );
  for (final length in [128, 129, 180, 181]) {
    void setPayloadName() {
      ((acceptance['payloads'] as List).first as Map)['name'] =
          '${'a' * (length - 4)}.zip';
    }

    test('schema payload name boundary $length', () {
      setPayloadName();
      expect(schema.validate(acceptance).isValid, length <= 180);
    });
    test('reader payload name boundary $length', () {
      setPayloadName();
      if (length <= 180) {
        validate();
      } else {
        expect(validate, throwsStateError);
      }
    });
  }
  test('exact acceptance is schema valid and is not mutated', () {
    expect(schema.validate(acceptance).isValid, isTrue);
    final before = jsonEncode(acceptance);
    validate();
    expect(jsonEncode(acceptance), before);
  });
  test('isolated virtual machine is also allowed', () {
    (acceptance['isolation'] as Map)['kind'] = 'virtual-machine';
    expect(schema.validate(acceptance).isValid, isTrue);
    validate();
  });
  for (final mutation in <String, void Function(Map<String, Object?>)>{
    'null': (v) => v['result'] = null,
    'additional raw log': (v) => v['rawLogs'] = 'private',
    'fractional cycles': (v) => v['authoringCycles'] = 15.5,
    'string boolean': (v) =>
        (v['isolation'] as Map)['persistentDataIsolated'] = 'true',
    'zero proof': (v) => (v['isolation'] as Map)['evidenceSha256'] = '0' * 64,
    'empty cases': (v) => v['cases'] = <Object?>[],
    'scalar case': (v) => v['cases'] = ['passed'],
    'Unicode case ID': (v) =>
        ((v['cases'] as List).first as Map)['id'] = 'case.é',
    'unknown reviewer field': (v) =>
        ((v['reviewerEvidence'] as List).first as Map)['userPath'] = 'private',
    'empty reviewers': (v) => v['reviewerEvidence'] = <Object?>[],
    'empty payloads': (v) => v['payloads'] = <Object?>[],
    'fractional payload': (v) =>
        ((v['payloads'] as List).first as Map)['size'] = 12.5,
  }.entries) {
    test('schema rejects ${mutation.key}', () {
      mutation.value(acceptance);
      expect(schema.validate(acceptance).isValid, isFalse);
      expect(validate, throwsStateError);
    });
  }
  for (final mutation in <String, void Function(Map<String, Object?>)>{
    'missing SDK case': (qa) =>
        (qa['robotopia'] as Map)['passedCases'] = <String>[],
    'failed SDK suite': (qa) =>
        (qa['robotopia'] as Map)['failures'] = ['failure'],
    'changed game receipt': (qa) =>
        (qa['robotopia'] as Map)['evidenceSha256'] = 'f' * 64,
    'short authoring cycles': (qa) => (qa['unity'] as Map)['cycles'] = 15,
    'failed authoring smoke': (qa) =>
        (qa['unity'] as Map)['validatorSmoke'] = false,
  }.entries) {
    test('rejects handoff ${mutation.key}', () {
      final qa =
          (jsonDecode(jsonEncode(handoff.platformBundles['windows-x64']!.qa))
                  as Map)
              .cast<String, Object?>();
      mutation.value(qa);
      expect(
        () => validateCandidateAcceptance(
          acceptance: acceptance,
          decision: decision,
          gameMetadata: metadata,
          liveInventory: live,
          redesignInventory: redesign,
          handoff: _handoff(live, metadata, qa: qa),
        ),
        throwsStateError,
      );
    });
  }
  for (final mutation in <String, void Function(Map<String, Object?>)>{
    'missing case': (v) => (v['cases'] as List).removeLast(),
    'duplicate case': (v) =>
        (v['cases'] as List).add((v['cases'] as List).first),
    'unknown case': (v) =>
        ((v['cases'] as List).first as Map)['id'] = 'unknown',
    'failed case': (v) =>
        ((v['cases'] as List).first as Map)['result'] = 'failed',
    'unsorted cases': (v) =>
        v['cases'] = (v['cases'] as List).reversed.toList(),
    'missing case evidence': (v) =>
        ((v['cases'] as List).first as Map).remove('evidenceSha256'),
    'wrong game build': (v) => v['gameBuildId'] = 2408,
    'coerced game build': (v) => v['gameBuildId'] = '2409',
    'missing lifecycle cycle': (v) => v['gameCycles'] = 9,
    'fractional cycle': (v) => v['gameCycles'] = 10.0,
    'missing authoring cycle': (v) => v['authoringCycles'] = 15,
    'stale game receipt': (v) => v['gameEvidenceSha256'] = 'c' * 64,
    'stale authoring receipt': (v) => v['authoringEvidenceSha256'] = 'c' * 64,
    'normal user profile': (v) =>
        (v['isolation'] as Map)['kind'] = 'bepinex-profile',
    'shared persistent data': (v) =>
        (v['isolation'] as Map)['persistentDataIsolated'] = false,
    'normal data touched': (v) =>
        (v['isolation'] as Map)['normalUserDataAccessed'] = true,
    'missing isolation proof': (v) =>
        (v['isolation'] as Map).remove('evidenceSha256'),
    'missing reviewer': (v) => (v['reviewerEvidence'] as List).removeWhere(
      (e) => (e as Map)['role'] == 'runtime-mod-qa',
    ),
    'unbound evidence': (v) =>
        ((v['reviewerEvidence'] as List).first as Map)['evidenceId'] =
            'EVID-P0-GAME-01-9999',
    'missing gate evidence': (v) => (v['reviewerEvidence'] as List).removeWhere(
      (e) => (e as Map)['evidenceId'] == 'EVID-P0-GAME-01-0002',
    ),
    'duplicate reviewer evidence': (v) => (v['reviewerEvidence'] as List).add(
      (v['reviewerEvidence'] as List).first,
    ),
    'unknown role': (v) =>
        ((v['reviewerEvidence'] as List).first as Map)['role'] =
            'invented-role',
    'absolute reviewer path': (v) =>
        ((v['reviewerEvidence'] as List).first as Map)['reference'] =
            r'C:\Users\person\private.log',
    'credential URL': (v) =>
        ((v['reviewerEvidence'] as List).first as Map)['reference'] =
            'https://user:secret@example.com/log?token=secret',
    'null result': (v) => v['result'] = null,
    'raw logs': (v) => v['rawLogs'] = 'private logs',
  }.entries) {
    test('rejects ${mutation.key}', () {
      mutation.value(acceptance);
      expect(validate, throwsStateError);
    });
  }
}

Map<String, Object?> _json(String file) =>
    (jsonDecode(File(file).readAsStringSync()) as Map).cast<String, Object?>();

ReleaseHandoffVerification _handoff(
  Map<String, Object?> live,
  Map<String, Object?> metadata, {
  Map<String, Object?>? qa,
}) {
  final liveIds = [
    for (final item in live['cases'] as List) (item as Map)['id'],
  ]..sort();
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
    qa:
        qa ??
        {
          'gameBuildId': metadata['buildId'],
          'robotopia': {
            'result': 'pass',
            'suite': 'full',
            'evidenceSha256': 'd' * 64,
            'requiredCases': liveIds,
            'passedCases': liveIds,
            'missingCases': <String>[],
            'failures': <String>[],
          },
          'unity': {
            'result': 'pass',
            'cycles': 16,
            'validatorSmoke': true,
            'evidenceSha256': 'e' * 64,
          },
        },
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
