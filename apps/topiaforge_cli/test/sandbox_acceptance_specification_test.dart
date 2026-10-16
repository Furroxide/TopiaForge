import 'dart:convert';
import 'dart:io';

import 'package:json_schema/json_schema.dart';
import 'package:test/test.dart';
import 'package:topiaforge/src/sandbox_acceptance/sandbox_specification.dart';

import 'sandbox_acceptance_fixture.dart';

void main() {
  test('nine scenarios retain explicit native and human limitations', () {
    final spec = SandboxSpecification.parse(sandboxSpecBytes);
    expect(spec.scenarios.map((s) => s.id), sandboxScenarioIds);
    expect(spec.scenarios.last.requiredSamples, 10);
    final residuals = spec.scenarios
        .expand((s) => s.residualRequirements)
        .toList();
    expect(
      residuals.map((r) => r.lane).toSet(),
      containsAll(['editor', 'game', 'os-input', 'physical-device', 'human']),
    );
    expect(residuals.any((r) => r.availability == 'not-implemented'), isTrue);
    expect(() => spec.scenarios.clear(), throwsUnsupportedError);
    expect(() => spec.scenarios.first.oracles.clear(), throwsUnsupportedError);
    expect(() => spec.scenarios.first.actions.clear(), throwsUnsupportedError);
  });

  List<Map<String, Object?>> scenarios(Map<String, Object?> json) =>
      (json['scenarios']! as List).cast<Map<String, Object?>>();
  final invalid = <String, void Function(Map<String, Object?>)>{
    'unknown root field': (j) => j['passed'] = true,
    'missing root field': (j) => j.remove('scope'),
    'native scope': (j) => j['scope'] = 'native-game',
    'unknown version': (j) => j['schemaVersion'] = 2,
    'floating version': (j) => j['schemaVersion'] = 1.0,
    'wrong game': (j) => j['gameBuild'] = 2408,
    'missing scenario': (j) => (j['scenarios']! as List).removeLast(),
    'extra scenario': (j) => (j['scenarios']! as List).add(scenarios(j).first),
    'unknown scenario': (j) => scenarios(j).first['id'] = 'unknown',
    'duplicate scenario': (j) => scenarios(j).last['id'] = 'routing',
    'reordered scenarios': (j) {
      final rows = j['scenarios']! as List;
      final first = rows[0];
      rows[0] = rows[1];
      rows[1] = first;
    },
    'fewer cycles': (j) => scenarios(j).last['requiredSamples'] = 9,
    'missing cleanup': (j) => scenarios(j).first['cleanup'] = <Object?>[],
    'missing negative tests': (j) =>
        scenarios(j).first['negativeTests'] = <Object?>[],
    'missing residuals': (j) =>
        scenarios(j).first['residualRequirements'] = <Object?>[],
    'unknown scenario field': (j) => scenarios(j).first['nativePassed'] = true,
    'duplicate oracle': (j) {
      final oracles = scenarios(j).first['oracles']! as List;
      oracles.add(oracles.first);
    },
    'no exercise count': (j) {
      final oracles = scenarios(j).first['oracles']! as List;
      oracles.removeWhere((o) => (o as Map)['comparison'] == 'equals');
    },
    'no restoration check': (j) {
      final oracles = scenarios(j).first['oracles']! as List;
      oracles.removeWhere((o) => (o as Map)['comparison'] == 'unchanged');
    },
    'negative count': (j) {
      final oracles = scenarios(j).first['oracles']! as List;
      final count = oracles.cast<Map>().firstWhere(
        (o) => o['comparison'] == 'equals' && o['type'] == 'count',
      );
      count['expected'] = -1;
    },
    'unknown comparison': (j) =>
        ((scenarios(j).first['oracles']! as List).first as Map)['comparison'] =
            'trust-driver',
    'unknown oracle field': (j) =>
        ((scenarios(j).first['oracles']! as List).first as Map)['result'] =
            'PASS',
    'duplicate residual': (j) {
      final residuals = scenarios(j).first['residualRequirements']! as List;
      residuals.add(residuals.first);
    },
    'unsupported residual lane': (j) =>
        ((scenarios(j).first['residualRequirements']! as List).first
                as Map)['lane'] =
            'offline',
    'human result substituted': (j) {
      final residual =
          (scenarios(j).first['residualRequirements']! as List).first as Map;
      residual['lane'] = 'human';
      residual['availability'] = 'requires-environment';
    },
  };
  for (final entry in invalid.entries) {
    test('spec refuses ${entry.key}', () {
      final json = sandboxSpecJson();
      entry.value(json);
      expect(
        () => SandboxSpecification.parse(sandboxJsonBytes(json)),
        throwsStateError,
      );
    });
  }

  test('strict JSON rejects duplicates, invalid UTF-8, oversize and depth', () {
    final text = utf8
        .decode(sandboxSpecBytes)
        .replaceFirst(
          '"schemaVersion": 1',
          '"schemaVersion": 2, "schemaVersion": 1',
        );
    expect(
      () => SandboxSpecification.parse(utf8.encode(text)),
      throwsStateError,
    );
    expect(() => SandboxSpecification.parse([0xff]), throwsStateError);
    expect(
      () => SandboxSpecification.parse(List.filled(256 * 1024 + 1, 32)),
      throwsStateError,
    );
    Object nested = 0;
    for (var i = 0; i < 18; i++) {
      nested = [nested];
    }
    expect(
      () => SandboxSpecification.parse(sandboxJsonBytes({'nested': nested})),
      throwsStateError,
    );
  });

  test('schemas accept fixtures and reject unknown properties', () {
    JsonSchema schema(String file) => JsonSchema.create(
      jsonDecode(
        File('$sandboxRepositoryRoot/schemas/$file').readAsStringSync(),
      ),
    );
    final contract = schema(
      'topiaforge.sandbox-workbench-acceptance-v1.schema.json',
    );
    final observations = schema(
      'topiaforge.sandbox-workbench-offline-observations-v1.schema.json',
    );
    final json = sandboxSpecJson();
    expect(contract.validate(json).isValid, isTrue);
    final report = sandboxObservations(
      SandboxSpecification.parse(sandboxSpecBytes),
    );
    expect(observations.validate(report).isValid, isTrue);
    json['extra'] = 'forbidden';
    report['passed'] = true;
    expect(contract.validate(json).isValid, isFalse);
    expect(observations.validate(report).isValid, isFalse);
  });
}
