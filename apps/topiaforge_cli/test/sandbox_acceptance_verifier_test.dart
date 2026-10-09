import 'dart:convert';

import 'package:test/test.dart';
import 'package:topiaforge/src/sandbox_acceptance/sandbox_specification.dart';
import 'package:topiaforge/src/sandbox_acceptance/sandbox_verifier.dart';

import 'sandbox_acceptance_fixture.dart';

void main() {
  final spec = SandboxSpecification.parse(sandboxSpecBytes);
  final verifier = SandboxObservationVerifier();
  SandboxVerification verify(Map<String, Object?> report) =>
      verifier.verify(spec, sandboxJsonBytes(report));

  test('nine synthetic offline results never qualify native acceptance', () {
    final result = verify(sandboxObservations(spec));
    expect(result.allOfflineChecksPassed, isTrue);
    expect(result.results.map((r) => r.scenarioId), sandboxScenarioIds);
    expect(result.qualifiesRelease, isFalse);
    expect(result.toJson()['qualifiesRelease'], isFalse);
    final remaining = result.toJson()['remainingRequirements']! as List;
    expect(
      remaining.length,
      spec.scenarios.fold<int>(
        0,
        (count, row) => count + row.residualRequirements.length,
      ),
    );
    expect(
      remaining.any((r) => (r as Map)['availability'] == 'not-implemented'),
      isTrue,
    );
  });

  for (final scenario in spec.scenarios) {
    for (final oracle in scenario.oracles) {
      test('${scenario.id}/${oracle.metric}: changed observed fact fails', () {
        final report = sandboxObservations(spec);
        final row = sandboxRows(
          report,
        ).singleWhere((r) => r['scenarioId'] == scenario.id);
        final after =
            sandboxSamples(row).first['after']! as Map<String, Object?>;
        after[oracle.metric] = sandboxDifferentValue(
          oracle,
          after[oracle.metric],
        );
        final result = verify(report);
        expect(result.allOfflineChecksPassed, isFalse);
        final failed = result.results.singleWhere(
          (r) => r.scenarioId == scenario.id,
        );
        expect(failed.status, SandboxOfflineStatus.failed);
        expect(failed.failures.single, contains(oracle.metric));
        // Failure is attributable to the corrupted measurement, not a parser error.
        expect(
          result.results
              .where((r) => r.status == SandboxOfflineStatus.failed)
              .length,
          1,
        );
      });
    }
  }

  test('every cycle is examined, not just final resource counts', () {
    final scenario = spec.scenarios.last;
    final oracle = scenario.oracles.firstWhere(
      (o) => o.comparison == 'unchanged',
    );
    for (var badCycle = 0; badCycle < 10; badCycle++) {
      final report = sandboxObservations(spec);
      final samples = sandboxSamples(sandboxRows(report).last);
      final after = samples[badCycle]['after']! as Map<String, Object?>;
      after[oracle.metric] = sandboxDifferentValue(
        oracle,
        after[oracle.metric],
      );
      expect(
        verify(report).results.last.failures,
        contains(
          'Sample ${badCycle + 1}: ${oracle.metric} violates unchanged.',
        ),
      );
    }
  });

  test('a moving baseline cannot hide per-cycle growth', () {
    final report = sandboxObservations(spec);
    final oracle = spec.scenarios.last.oracles.firstWhere(
      (o) => o.comparison == 'unchanged',
    );
    final sample = sandboxSamples(sandboxRows(report).last)[5];
    final before = sample['before']! as Map<String, Object?>;
    final after = sample['after']! as Map<String, Object?>;
    before[oracle.metric] = sandboxDifferentValue(
      oracle,
      before[oracle.metric],
    );
    after[oracle.metric] = before[oracle.metric];
    expect(
      verify(report).results.last.failures,
      contains('Sample 6: restoration baseline drifted.'),
    );
  });

  test('missing scenarios and truncated cycles remain incomplete', () {
    final report = sandboxObservations(spec);
    (report['observations']! as List).removeAt(0);
    (sandboxRows(report).last['samples']! as List).removeLast();
    final result = verify(report);
    expect(result.results.first.status, SandboxOfflineStatus.missing);
    expect(result.results.last.status, SandboxOfflineStatus.failed);
    report['observations'] = <Object?>[];
    expect(
      verify(
        report,
      ).results.every((r) => r.status == SandboxOfflineStatus.missing),
      isTrue,
    );
  });

  final invalidCases = <String, void Function(Map<String, Object?>)>{
    'driver PASS': (r) => r['passed'] = true,
    'native scope': (r) => r['scope'] = 'native-game',
    'unknown version': (r) => r['schemaVersion'] = 2,
    'wrong specification': (r) => r['specSha256'] = List.filled(64, '0').join(),
    'invalid source': (r) => r['sourceRevision'] = 'HEAD',
    'unsafe run id': (r) => r['runId'] = '../run',
    'unknown scenario': (r) =>
        sandboxRows(r).first['scenarioId'] = 'replacement',
    'duplicate scenario': (r) => sandboxRows(r).last['scenarioId'] = 'routing',
    'unknown row field': (r) => sandboxRows(r).first['result'] = 'PASS',
    'duplicate cycle': (r) =>
        sandboxSamples(sandboxRows(r).last)[1]['sequence'] = 1,
    'string sequence': (r) =>
        sandboxSamples(sandboxRows(r).first).first['sequence'] = '1',
    'missing sample field': (r) =>
        sandboxSamples(sandboxRows(r).first).first.remove('before'),
    'extra metric': (r) =>
        (sandboxSamples(sandboxRows(r).first).first['after']! as Map)['extra'] =
            0,
    'missing metric': (r) {
      final after = sandboxSamples(sandboxRows(r).first).first['after']! as Map;
      after.remove(after.keys.first);
    },
    'wrong metric type': (r) {
      final after = sandboxSamples(sandboxRows(r).first).first['after']! as Map;
      after[after.keys.first] = true;
    },
    'too many samples': (r) {
      final samples = sandboxRows(r).last['samples']! as List;
      samples.add(samples.first);
    },
  };
  for (final entry in invalidCases.entries) {
    test('refuses ${entry.key}', () {
      final report = sandboxObservations(spec);
      entry.value(report);
      expect(() => verify(report), throwsStateError);
    });
  }

  test('exact-byte spec binding detects whitespace-only substitution', () {
    final other = SandboxSpecification.parse([...sandboxSpecBytes, 32]);
    expect(
      () => verifier.verify(other, sandboxJsonBytes(sandboxObservations(spec))),
      throwsStateError,
    );
  });

  test('duplicate JSON properties are rejected before overwriting', () {
    final text = jsonEncode(sandboxObservations(spec));
    final repeated = text.replaceFirst(
      '"schemaVersion":1',
      '"schemaVersion":2,"schemaVersion":1',
    );
    expect(
      () => verifier.verify(spec, utf8.encode(repeated)),
      throwsStateError,
    );
  });
}
