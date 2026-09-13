import '../release_strict_json.dart';
import 'sandbox_json.dart';
import 'sandbox_specification.dart';

enum SandboxOfflineStatus { passed, failed, missing }

class SandboxScenarioResult {
  SandboxScenarioResult(this.scenarioId, this.status, Iterable<String> failures)
    : failures = List.unmodifiable(failures);
  final String scenarioId;
  final SandboxOfflineStatus status;
  final List<String> failures;
}

class SandboxVerification {
  SandboxVerification._(
    this.specification,
    this.runId,
    this.reportedSourceRevision,
    Iterable<SandboxScenarioResult> results,
  ) : results = List.unmodifiable(results);
  final SandboxSpecification specification;
  final String runId, reportedSourceRevision;
  final List<SandboxScenarioResult> results;
  bool get allOfflineChecksPassed =>
      results.every((r) => r.status == SandboxOfflineStatus.passed);

  /// Offline facts are not authenticated process, input, image or game evidence.
  bool get qualifiesRelease => false;

  Map<String, Object?> toJson() => {
    'scope': 'supplementary-offline-contracts',
    'runId': runId,
    'reportedSourceRevision': reportedSourceRevision,
    'specSha256': specification.sha256Digest,
    'allOfflineChecksPassed': allOfflineChecksPassed,
    'qualifiesRelease': false,
    'provenance':
        'Unauthenticated offline observations; no native execution claim.',
    'results': [
      for (final r in results)
        {
          'scenarioId': r.scenarioId,
          'status': r.status.name,
          'failures': r.failures,
        },
    ],
    'remainingRequirements': [
      for (final s in specification.scenarios)
        for (final r in s.residualRequirements)
          {
            'scenarioId': s.id,
            'id': r.id,
            'lane': r.lane,
            'availability': r.availability,
          },
    ],
  };
}

/// Recomputes contract outcomes from typed before/after measurements.
/// Driver PASS labels are not part of the input grammar. Native claims are
/// deliberately unsupported until the separately admitted broker is implemented.
class SandboxObservationVerifier {
  SandboxVerification verify(SandboxSpecification spec, List<int> bytes) {
    final json = sandboxDocument(bytes, 'Sandbox offline observations');
    sandboxFields(json, {
      'schemaVersion',
      'kind',
      'scope',
      'specSha256',
      'runId',
      'sourceRevision',
      'observations',
    }, 'Sandbox offline observations');
    if (json['schemaVersion'] is! int ||
        json['schemaVersion'] != 1 ||
        json['kind'] != 'sandbox-workbench-offline-observations-v1' ||
        json['scope'] != 'supplementary-offline-contracts' ||
        json['specSha256'] != spec.sha256Digest) {
      throw StateError(
        'Unsupported observation scope, version or exact spec digest.',
      );
    }
    final revision = sandboxText(json['sourceRevision'], 'source revision');
    if (!RegExp(r'^[a-f0-9]{40}$').hasMatch(revision)) {
      throw StateError('Invalid source revision.');
    }
    final runId = sandboxId(json['runId'], 'run id');
    final rows = json['observations'];
    if (rows is! List<Object?> || rows.length > 9) {
      throw StateError('Invalid observation count.');
    }
    final byId = <String, Map<String, Object?>>{};
    for (final value in rows) {
      final row = sandboxObject(value, 'observation');
      sandboxFields(row, {'scenarioId', 'samples'}, 'observation');
      final id = sandboxId(row['scenarioId'], 'observed scenario id');
      if (!sandboxScenarioIds.contains(id) || byId.containsKey(id)) {
        throw StateError('Unknown or repeated observed scenario.');
      }
      byId[id] = row;
    }
    final results = <SandboxScenarioResult>[];
    for (final scenario in spec.scenarios) {
      final row = byId[scenario.id];
      if (row == null) {
        results.add(
          SandboxScenarioResult(
            scenario.id,
            SandboxOfflineStatus.missing,
            const ['No observations supplied.'],
          ),
        );
      } else {
        results.add(_verifyScenario(scenario, row['samples']));
      }
    }
    return SandboxVerification._(spec, runId, revision, results);
  }

  SandboxScenarioResult _verifyScenario(
    SandboxScenario scenario,
    Object? value,
  ) {
    final samples = sandboxList(value, 'samples', maximum: 10);
    final failures = <String>[];
    if (samples.length != scenario.requiredSamples) {
      failures.add(
        'Expected ${scenario.requiredSamples} complete samples; got ${samples.length}.',
      );
    }
    String? baseline;
    for (var index = 0; index < samples.length; index++) {
      final sample = sandboxObject(samples[index], 'sample');
      sandboxFields(sample, {'sequence', 'before', 'after'}, 'sample');
      if (sample['sequence'] != index + 1 || sample['sequence'] is! int) {
        throw StateError(
          'Samples must have contiguous, unique sequence numbers.',
        );
      }
      final before = sandboxObject(sample['before'], 'baseline measurements');
      final after = sandboxObject(
        sample['after'],
        'postcondition measurements',
      );
      final fields = scenario.oracles.map((o) => o.metric).toSet();
      sandboxFields(before, fields, 'baseline measurements');
      sandboxFields(after, fields, 'postcondition measurements');
      for (final oracle in scenario.oracles) {
        oracle.validateValue(before[oracle.metric]);
        oracle.validateValue(after[oracle.metric]);
        if (oracle.type == 'count' &&
            oracle.comparison == 'equals' &&
            (oracle.expected! as int) > 0 &&
            before[oracle.metric] != 0) {
          failures.add(
            'Sample ${index + 1}: ${oracle.metric} exercise counter must start at zero.',
          );
        }
        final expected = oracle.comparison == 'unchanged'
            ? before[oracle.metric]
            : oracle.expected;
        if (after[oracle.metric] != expected) {
          failures.add(
            'Sample ${index + 1}: ${oracle.metric} violates ${oracle.comparison}.',
          );
        }
      }
      final restoredBaseline = canonicalReleaseJson({
        for (final o in scenario.oracles)
          if (o.comparison == 'unchanged') o.metric: before[o.metric],
      });
      baseline ??= restoredBaseline;
      if (baseline != restoredBaseline) {
        failures.add('Sample ${index + 1}: restoration baseline drifted.');
      }
    }
    return SandboxScenarioResult(
      scenario.id,
      failures.isEmpty
          ? SandboxOfflineStatus.passed
          : SandboxOfflineStatus.failed,
      failures,
    );
  }
}
