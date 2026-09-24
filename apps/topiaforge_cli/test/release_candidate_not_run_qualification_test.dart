import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/release_handoff.dart';

import 'release_candidate_acceptance_fixture.dart';
import 'release_candidate_fixture.dart';

/// Exact-candidate qualification when live game acceptance was not run under
/// the owner's P0-GAME-01 disposition. The candidate may reach `ready`, but the
/// summary must still show GAME blocked, and nothing may claim a run.
void main() {
  late CandidateFixture fixture;
  late Map<String, List<int>> original;
  setUpAll(() async {
    fixture = await CandidateFixture.create(liveGameAcceptanceRun: false);
    original = {
      for (final entity in fixture.assets.listSync())
        p.basename(entity.path): File(entity.path).readAsBytesSync(),
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

  Map<String, Object?> gameRow(Map<String, Object?> decision) =>
      (decision['gates']! as List).cast<Map<String, Object?>>().singleWhere(
        (gate) => gate['id'] == 'P0-GAME-01',
      );

  Matcher refusal(String message) => throwsA(
    isA<StateError>().having(
      (error) => error.message,
      'message',
      contains(message),
    ),
  );

  test(
    'a truthful not-run candidate qualifies with GAME still blocked',
    () async {
      expect(fixture.acceptance['result'], 'not-run');
      final result = await fixture.qualify();
      expect(result.isReady, isTrue);
      final gates = {for (final gate in result.gates) gate.id: gate};
      expect(gates['P0-GAME-01']!.status, 'blocked');
      expect(gates['P0-GAME-01']!.enforcement, 'advisory');
      expect(gates['P0-GAME-01']!.evidenceIds, isEmpty);
      expect(gates['P0-GAME-01']!.isSatisfied, isFalse);
      for (final id in ['P0-IP-01', 'P0-OSS-01', 'P0-PRIV-01', 'P0-CRED-01']) {
        expect(gates[id]!.status, 'approved', reason: id);
      }
      for (final id in ['P1-UX-01', 'P1-E2E-01']) {
        expect(gates[id]!.status, 'accepted-risk', reason: id);
      }
      final summary = gameRow(result.toPublicSummary());
      expect(summary['status'], 'blocked');
      expect(summary['reasonCode'], 'acceptance-evidence-missing');
    },
  );

  test('an approved GAME row cannot accompany a not-run record', () async {
    final decision = fixture.decision;
    gameRow(decision)
      ..['status'] = 'approved'
      ..remove('reasonCode')
      ..['evidenceIds'] = ['EVID-P0-GAME-01-0001'];
    fixture.writeDecision(decision);
    await expectLater(
      fixture.qualify(),
      refusal('advisory P0-GAME-01 row to remain blocked without evidence'),
    );
  });

  test('a changed GAME row is neither approval nor disposition', () async {
    final decision = fixture.decision;
    gameRow(decision)['reasonCode'] = 'approval-evidence-missing';
    fixture.writeDecision(decision);
    await expectLater(
      fixture.qualify(),
      refusal('Candidate game approval is required'),
    );
  });

  test('a passed record cannot qualify a not-run handoff', () async {
    final decision = fixture.decision;
    gameRow(decision)
      ..['status'] = 'approved'
      ..remove('reasonCode')
      ..['evidenceIds'] = ['EVID-P0-GAME-01-0001'];
    fixture.writeDecision(decision);
    final handoff = await _verifiedHandoff(fixture);
    final acceptance = fixture.acceptance;
    fixture.writeAcceptance(
      candidateAcceptanceFixture(
        decision: decision,
        gameMetadata: fixture.contract.object(
          '.github/robotopia-game-build.json',
        ),
        redesignInventory: fixture.contract.object(
          'tests/gamemode-release-acceptance.json',
        ),
        handoff: handoff,
        contractSha256: acceptance['contractSha256']! as String,
        handoffSha256: acceptance['handoffSha256']! as String,
        payloads: (acceptance['payloads']! as List)
            .cast<Map<String, Object?>>(),
      )..['gameEvidenceSha256'] = 'e' * 64,
    );
    await expectLater(
      fixture.qualify(),
      refusal('game receipt is stale, incomplete or failed'),
    );
  });

  test('the not-run acceptance digest is bound by the decision', () async {
    fixture.writeAcceptance(
      fixture.acceptance..['authoringEvidenceSha256'] = 'c' * 64,
      rebind: false,
    );
    await expectLater(
      fixture.qualify(),
      refusal('Candidate acceptance digest changed'),
    );
  });

  test('a stale not-run authoring digest is rejected', () async {
    fixture.writeAcceptance(
      fixture.acceptance..['authoringEvidenceSha256'] = 'c' * 64,
    );
    await expectLater(
      fixture.qualify(),
      refusal('authoring receipt is stale, incomplete or failed'),
    );
  });

  test('a not-run record cannot qualify a performed handoff', () async {
    final performed = await CandidateFixture.create();
    addTearDown(performed.dispose);
    final decision = performed.decision;
    final tracked = gameRow(fixture.decision);
    gameRow(decision)
      ..clear()
      ..addAll(tracked);
    performed.writeDecision(decision);
    final handoff = await _verifiedHandoff(performed);
    final acceptance = performed.acceptance;
    performed.writeAcceptance(
      candidateNotRunAcceptanceFixture(
        gameMetadata: performed.contract.object(
          '.github/robotopia-game-build.json',
        ),
        redesignInventory: performed.contract.object(
          'tests/gamemode-release-acceptance.json',
        ),
        handoff: handoff,
        contractSha256: acceptance['contractSha256']! as String,
        handoffSha256: acceptance['handoffSha256']! as String,
        payloads: (acceptance['payloads']! as List)
            .cast<Map<String, Object?>>(),
      ),
    );
    await expectLater(
      performed.qualify(),
      refusal('game receipt has missing or forbidden fields'),
    );
  });

  test(
    'a re-blocked release-fatal gate still stops a not-run candidate',
    () async {
      final blocked = await CandidateFixture.create(
        liveGameAcceptanceRun: false,
        mutateContracts: (root) {
          final file = File(
            p.join(root.path, 'release/release-readiness.json'),
          );
          final readiness = readObject(file);
          final cred = (readiness['gates'] as List).cast<Map>().singleWhere(
            (gate) => gate['id'] == 'P0-CRED-01',
          );
          cred
            ..['status'] = 'blocked'
            ..['reasonCode'] = 'rotation-evidence-missing'
            ..['evidenceIds'] = <String>[];
          writeObject(file, readiness..['status'] = 'blocked');
        },
      );
      addTearDown(blocked.dispose);
      await expectLater(
        blocked.qualify(),
        refusal('Required tracked approval is missing: P0-CRED-01'),
      );
    },
  );
}

/// The fixture's own handoff, reread through the public verifier.
Future<ReleaseHandoffVerification> _verifiedHandoff(CandidateFixture fixture) =>
    fixture.contract.withSnapshot(
      (snapshot) => const TopiaForgeReleaseHandoff().verify(
        repositoryRoot: snapshot,
        version: CandidateFixture.version,
        targetSha: fixture.sha,
        assetsDirectory: fixture.assets.path,
      ),
    );
