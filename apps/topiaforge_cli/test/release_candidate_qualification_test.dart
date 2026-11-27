import 'dart:convert';
import 'dart:io';

import 'package:test/test.dart';
import 'package:topiaforge/src/release_candidate_contract.dart';

import 'release_candidate_fixture.dart';

void main() {
  late CandidateFixture fixture;
  late Map<String, List<int>> original;
  setUpAll(() async {
    fixture = await CandidateFixture.create();
    original = {
      for (final entity in fixture.assets.listSync())
        entity.uri.pathSegments.last: fixture
            .asset(entity.uri.pathSegments.last)
            .readAsBytesSync(),
    };
  });
  tearDown(() {
    for (final entity in fixture.assets.listSync()) {
      entity.deleteSync();
    }
    for (final entry in original.entries) {
      fixture.asset(entry.key).writeAsBytesSync(entry.value);
    }
  });
  tearDownAll(() => fixture.dispose());

  test('qualifies exact payloads with only game approval detached', () async {
    final result = await fixture.qualify();
    expect(result.isReady, isTrue);
    expect(result.gates, hasLength(12));
    expect(
      result.toPublicSummary()['contractSha256'],
      fixture.contract.contractSha256,
    );
    expect(result.toBomJson()['binding'], 'detached-candidate-at-target-sha');
  });
  test('returned JSON cannot mutate qualification', () async {
    final result = await fixture.qualify();
    final summary = result.toPublicSummary();
    (summary['payloads'] as List).clear();
    expect(result.toPublicSummary()['payloads'], isNotEmpty);
    expect(() => result.gates.clear(), throwsUnsupportedError);
  });
  for (final field in [
    'repository',
    'releaseVersion',
    'targetSha',
    'baseReadinessSha256',
    'baseSchemaSha256',
    'policySha256',
    'catalogSha256',
    'contractSha256',
    'handoffSha256',
    'acceptanceSha256',
  ]) {
    test('refuses mismatched $field', () async {
      fixture.writeDecision(
        fixture.decision
          ..[field] = field.endsWith('Sha256') ? '0' * 64 : 'wrong',
      );
      await expectLater(fixture.qualify(), throwsStateError);
    });
  }
  test(
    'rejects changed payload bytes without accepting an old receipt',
    () async {
      fixture.asset('TopiaForge-windows-x64.zip').writeAsStringSync('changed');
      await expectLater(fixture.qualify(), throwsStateError);
    },
  );
  test('rejects missing payload', () async {
    fixture.asset('TopiaForge-windows-x64.zip').deleteSync();
    await expectLater(fixture.qualify(), throwsA(anything));
  });
  test('rejects an added payload', () async {
    fixture.asset('unexpected.zip').writeAsStringSync('extra');
    await expectLater(fixture.qualify(), throwsStateError);
  });
  test('rejects changed handoff bytes', () async {
    fixture.asset('release-handoff-v1.json').writeAsStringSync('{}');
    await expectLater(fixture.qualify(), throwsStateError);
  });
  test('rejects changed acceptance bytes before semantic validation', () async {
    fixture.writeAcceptance(
      fixture.acceptance..['gameCycles'] = 9,
      rebind: false,
    );
    await expectLater(fixture.qualify(), throwsStateError);
  });
  test('rejects a changed non-game gate even if it remains advisory', () async {
    final decision = fixture.decision;
    (decision['gates'] as List).firstWhere(
      (gate) => gate['id'] == 'P0-WIN-01',
    )['reasonCode'] = 'approval-evidence-missing';
    fixture.writeDecision(decision);
    await expectLater(fixture.qualify(), throwsStateError);
  });
  test('rejects duplicate JSON properties before information loss', () async {
    final file = fixture.asset(candidateReadinessName);
    file.writeAsStringSync(
      file.readAsStringSync().replaceFirst('{', '{"status":"blocked",'),
    );
    await expectLater(fixture.qualify(), throwsStateError);
  });
  test('rejects a fractional payload integer', () async {
    final decision = fixture.decision;
    (decision['payloads'] as List).first['size'] = 1.5;
    fixture.writeDecision(decision);
    await expectLater(fixture.qualify(), throwsStateError);
  });
  test('rejects unsorted or duplicated payload inventory', () async {
    final decision = fixture.decision;
    decision['payloads'] = (decision['payloads'] as List).reversed.toList();
    fixture.writeDecision(decision);
    await expectLater(fixture.qualify(), throwsStateError);
  });
  test('ignores uncommitted policy and schema edits', () async {
    final files = ['release/release-policy.json', candidateReadinessSchemaPath];
    for (final path in files) {
      final file = File.fromUri(fixture.root.uri.resolve(path));
      file.writeAsStringSync('{}');
    }
    final result = await fixture.qualify();
    expect(jsonEncode(result.toPublicSummary()), contains(fixture.sha));
  });
}
