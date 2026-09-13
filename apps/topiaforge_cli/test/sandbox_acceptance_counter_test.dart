import 'package:test/test.dart';
import 'package:topiaforge/src/sandbox_acceptance/sandbox_specification.dart';
import 'package:topiaforge/src/sandbox_acceptance/sandbox_verifier.dart';

import 'sandbox_acceptance_fixture.dart';

void main() {
  final spec = SandboxSpecification.parse(sandboxSpecBytes);
  for (final scenario in spec.scenarios) {
    test('${scenario.id}: stale exercise counters do not prove action', () {
      final report = sandboxObservations(spec);
      final row = sandboxRows(
        report,
      ).singleWhere((r) => r['scenarioId'] == scenario.id);
      final positive = scenario.oracles.firstWhere(
        (o) =>
            o.type == 'count' &&
            o.comparison == 'equals' &&
            (o.expected! as int) > 0,
      );
      for (final sample in sandboxSamples(row)) {
        (sample['before']! as Map)[positive.metric] = positive.expected;
      }
      final result = SandboxObservationVerifier().verify(
        spec,
        sandboxJsonBytes(report),
      );
      final failed = result.results.singleWhere(
        (r) => r.scenarioId == scenario.id,
      );
      expect(failed.status, SandboxOfflineStatus.failed);
      expect(failed.failures.length, scenario.requiredSamples);
      expect(
        failed.failures.every((f) => f.contains('must start at zero')),
        isTrue,
      );
    });
  }
}
