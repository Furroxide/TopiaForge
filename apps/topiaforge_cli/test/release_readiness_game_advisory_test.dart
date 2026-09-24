import 'dart:convert';
import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/release_prerequisites.dart';
import 'package:topiaforge/src/release_readiness.dart';

/// Pins the owner's 2026-09-24 disposition of P0-GAME-01: advisory, still
/// blocked, and never a substitute for the four release-fatal approvals.
void main() {
  const blocking = ['P0-IP-01', 'P0-OSS-01', 'P0-PRIV-01', 'P0-CRED-01'];
  late List<int> schemaBytes;
  late Map<String, Object?> tracked;

  setUpAll(() {
    var root = Directory.current.absolute;
    while (!File(p.join(root.path, 'TopiaForge.slnx')).existsSync()) {
      root = root.parent;
    }
    schemaBytes = File(
      p.join(root.path, releaseReadinessSchemaPath),
    ).readAsBytesSync();
    tracked =
        (jsonDecode(
                  File(
                    p.join(root.path, releaseReadinessPath),
                  ).readAsStringSync(),
                )
                as Map)
            .cast<String, Object?>();
  });

  Map<String, Object?> register({
    Iterable<String> approvals = const [],
    required String status,
  }) {
    final copy = (jsonDecode(jsonEncode(tracked)) as Map)
        .cast<String, Object?>();
    for (final gate in (copy['gates']! as List).cast<Map>()) {
      if (!approvals.contains(gate['id'])) continue;
      gate['status'] = 'approved';
      gate.remove('reasonCode');
      gate['evidenceIds'] = ['EVID-${gate['id']}-0001'];
    }
    return copy..['status'] = status;
  }

  ReleaseReadinessDecision parse(Map<String, Object?> readiness) =>
      ReleaseReadinessDecision.fromCandidateBlobs(
        readinessBytes: utf8.encode(jsonEncode(readiness)),
        schemaBytes: schemaBytes,
        targetSha: 'a' * 40,
        expectedReleaseVersion: '0.1.0-rc.1',
      );

  Matcher refusal(String message) => throwsA(
    isA<StateError>().having(
      (error) => error.message,
      'message',
      contains(message),
    ),
  );

  test('a register that declares P0-GAME-01 blocking is refused', () {
    // The contract pins the disposition, so the decision file cannot restore
    // blocking on its own any more than it can relax a gate.
    final restored = register(status: 'blocked');
    (restored['gates']! as List).cast<Map>().singleWhere(
      (gate) => gate['id'] == 'P0-GAME-01',
    )['enforcement'] = 'blocking';
    expect(() => parse(restored), refusal('wrong identity'));
  });

  test('the four approvals reach ready with P0-GAME-01 still blocked', () {
    final decision = parse(register(approvals: blocking, status: 'ready'));
    expect(decision.isReady, isTrue);
    final game = decision.gates.singleWhere((gate) => gate.id == 'P0-GAME-01');
    expect(game.enforcement, 'advisory');
    expect(game.status, 'blocked');
    expect(game.isSatisfied, isFalse);
    expect(game.blocksRelease, isFalse);
    final summary = decision.toPublicSummary()['gates']! as List;
    expect(summary.cast<Map>().singleWhere((g) => g['id'] == 'P0-GAME-01'), {
      'id': 'P0-GAME-01',
      'priority': 'P0',
      'enforcement': 'advisory',
      'status': 'blocked',
      'reasonCode': 'acceptance-evidence-missing',
      'reviewerRoles': ['robotopia-owner', 'runtime-mod-qa'],
      'evidenceIds': <String>[],
    });
    final prerequisites = ReleasePrerequisites(decision);
    expect(prerequisites.isEligible, isTrue);
    expect(prerequisites.toPublicSummary()['deferredGateIds'], isEmpty);
  });

  for (final held in blocking) {
    test('$held still blocked keeps the decision blocked', () {
      final approvals = blocking.where((id) => id != held);
      final decision = parse(register(approvals: approvals, status: 'blocked'));
      expect(decision.isReady, isFalse);
      expect(
        decision.gates.where((gate) => gate.blocksRelease).map((g) => g.id),
        [held],
      );
      expect(ReleasePrerequisites(decision).isEligible, isFalse);
      expect(
        () => parse(register(approvals: approvals, status: 'ready')),
        refusal('status does not match'),
      );
    });
  }

  test('the tracked register defers nothing and stays ineligible', () {
    final prerequisites = ReleasePrerequisites(parse(tracked));
    expect(prerequisites.isEligible, isFalse);
    expect(prerequisites.toPublicSummary()['deferredGateIds'], isEmpty);
  });
}
