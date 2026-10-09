import 'dart:convert';
import 'dart:io';

import 'package:topiaforge/src/sandbox_acceptance/sandbox_specification.dart';

String get sandboxRepositoryRoot {
  var directory = Directory.current.absolute;
  while (!File('${directory.path}/TopiaForge.slnx').existsSync()) {
    if (directory.parent.path == directory.path) {
      throw StateError('Repository root not found.');
    }
    directory = directory.parent;
  }
  return directory.path;
}

List<int> get sandboxSpecBytes => File(
  '$sandboxRepositoryRoot/tests/sandbox-workbench-acceptance-v1.json',
).readAsBytesSync();

List<int> sandboxJsonBytes(Object? value) => utf8.encode(jsonEncode(value));

Map<String, Object?> sandboxSpecJson() =>
    jsonDecode(utf8.decode(sandboxSpecBytes)) as Map<String, Object?>;

/// Synthetic verifier input only. Never stored as real execution evidence.
Map<String, Object?> sandboxObservations(SandboxSpecification spec) => {
  'schemaVersion': 1,
  'kind': 'sandbox-workbench-offline-observations-v1',
  'scope': 'supplementary-offline-contracts',
  'specSha256': spec.sha256Digest,
  'runId': 'synthetic-verifier-test',
  'sourceRevision': List.filled(40, 'a').join(),
  'observations': [
    for (final scenario in spec.scenarios)
      {
        'scenarioId': scenario.id,
        'samples': [
          for (var i = 0; i < scenario.requiredSamples; i++)
            {
              'sequence': i + 1,
              'before': {
                for (final oracle in scenario.oracles)
                  oracle.metric: sandboxBaseline(oracle),
              },
              'after': {
                for (final oracle in scenario.oracles)
                  oracle.metric: oracle.comparison == 'equals'
                      ? oracle.expected
                      : sandboxBaseline(oracle),
              },
            },
        ],
      },
  ],
};

Object sandboxBaseline(SandboxOracle oracle) => switch (oracle.type) {
  'count' => 0,
  'fingerprint' => List.filled(64, 'b').join(),
  'label' => 'baseline',
  _ => throw StateError('Unknown metric type.'),
};

Object sandboxDifferentValue(SandboxOracle oracle, Object? old) =>
    switch (oracle.type) {
      'count' => old == 0 ? 1 : 0,
      'fingerprint' => List.filled(
        64,
        old == List.filled(64, 'c').join() ? 'd' : 'c',
      ).join(),
      'label' => old == 'different' ? 'changed' : 'different',
      _ => throw StateError('Unknown metric type.'),
    };

List<Map<String, Object?>> sandboxRows(Map<String, Object?> report) =>
    (report['observations']! as List).cast<Map<String, Object?>>();

List<Map<String, Object?>> sandboxSamples(Map<String, Object?> row) =>
    (row['samples']! as List).cast<Map<String, Object?>>();
