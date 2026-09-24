import 'package:crypto/crypto.dart';

import 'sandbox_game_build.dart';
import 'sandbox_json.dart';

const sandboxScenarioIds = [
  'routing',
  'catalog-editing',
  'borrowed-robot',
  'source-unload',
  'hide-reopen',
  'persistence-refusal',
  'graph-rollback',
  'lifecycle-routes',
  'ten-cycles',
];

/// Reviewed scenario inventory. This is separate from every release gate schema.
class SandboxSpecification {
  SandboxSpecification._(this.sha256Digest, this.scenarios);

  factory SandboxSpecification.parse(List<int> bytes) {
    final json = sandboxDocument(bytes, 'Sandbox specification');
    sandboxFields(json, {
      'schemaVersion',
      'kind',
      'gameBuild',
      'scope',
      'scenarios',
    }, 'Sandbox specification');
    if (json['schemaVersion'] is! int ||
        json['schemaVersion'] != 1 ||
        json['kind'] != 'sandbox-workbench-acceptance-v1' ||
        json['scope'] != 'supplementary-offline-contracts' ||
        json['gameBuild'] is! int ||
        json['gameBuild'] != sandboxGameBuild) {
      throw StateError('Unsupported Sandbox specification identity.');
    }
    final rows = sandboxList(json['scenarios'], 'scenarios', maximum: 9);
    if (rows.length != 9) throw StateError('All nine scenarios are required.');
    final scenarios = <SandboxScenario>[];
    final requirementIds = <String>{};
    for (var index = 0; index < rows.length; index++) {
      final scenario = SandboxScenario._parse(rows[index]);
      if (scenario.id != sandboxScenarioIds[index]) {
        throw StateError(
          'Scenario inventory is reordered, duplicate or unknown.',
        );
      }
      if (scenario.requiredSamples != (scenario.id == 'ten-cycles' ? 10 : 1)) {
        throw StateError(
          'Scenario sample count changes the fixed cycle contract.',
        );
      }
      for (final requirement in scenario.residualRequirements) {
        if (!requirementIds.add(requirement.id)) {
          throw StateError('Residual requirement identifiers must be unique.');
        }
      }
      scenarios.add(scenario);
    }
    return SandboxSpecification._(
      sha256.convert(bytes).toString(),
      List.unmodifiable(scenarios),
    );
  }

  final String sha256Digest;
  final List<SandboxScenario> scenarios;
}

class SandboxScenario {
  SandboxScenario._({
    required this.id,
    required this.title,
    required this.fixture,
    required this.requiredSamples,
    required this.actions,
    required this.cleanup,
    required this.negativeTests,
    required this.oracles,
    required this.residualRequirements,
  });

  factory SandboxScenario._parse(Object? value) {
    final json = sandboxObject(value, 'scenario');
    sandboxFields(json, {
      'id',
      'title',
      'fixture',
      'requiredSamples',
      'actions',
      'cleanup',
      'negativeTests',
      'oracles',
      'residualRequirements',
    }, 'scenario');
    final samples = json['requiredSamples'];
    if (samples is! int || samples < 1 || samples > 10) {
      throw StateError('Invalid sample count.');
    }
    final oracles = sandboxList(
      json['oracles'],
      'oracles',
      maximum: 32,
    ).map(SandboxOracle._parse).toList(growable: false);
    if (oracles.map((o) => o.metric).toSet().length != oracles.length) {
      throw StateError('A scenario repeats an oracle metric.');
    }
    // Every scenario must observe both an exercised action and retained state.
    if (!oracles.any(
          (o) =>
              o.comparison == 'equals' &&
              o.type == 'count' &&
              o.expected is int &&
              (o.expected! as int) > 0,
        ) ||
        !oracles.any((o) => o.comparison == 'unchanged')) {
      throw StateError(
        'An exercise count and restoration oracle are required.',
      );
    }
    return SandboxScenario._(
      id: sandboxId(json['id'], 'scenario id'),
      title: sandboxText(json['title'], 'scenario title'),
      fixture: sandboxText(json['fixture'], 'fixture'),
      requiredSamples: samples,
      actions: sandboxTexts(json['actions'], 'actions'),
      cleanup: sandboxTexts(json['cleanup'], 'cleanup'),
      negativeTests: sandboxTexts(json['negativeTests'], 'negative tests'),
      oracles: List.unmodifiable(oracles),
      residualRequirements: List.unmodifiable(
        sandboxList(
          json['residualRequirements'],
          'residual requirements',
          maximum: 16,
        ).map(SandboxResidualRequirement._parse),
      ),
    );
  }

  final String id, title, fixture;
  final int requiredSamples;
  final List<String> actions, cleanup, negativeTests;
  final List<SandboxOracle> oracles;
  final List<SandboxResidualRequirement> residualRequirements;
}

class SandboxOracle {
  SandboxOracle._(this.metric, this.type, this.comparison, this.expected);

  factory SandboxOracle._parse(Object? value) {
    final json = sandboxObject(value, 'oracle');
    sandboxFields(json, {'metric', 'type', 'comparison', 'expected'}, 'oracle');
    final type = sandboxText(json['type'], 'metric type');
    final comparison = sandboxText(json['comparison'], 'comparison');
    if (!const {'count', 'fingerprint', 'label'}.contains(type) ||
        !const {'equals', 'unchanged'}.contains(comparison)) {
      throw StateError('Unknown oracle type or comparison.');
    }
    final result = SandboxOracle._(
      sandboxId(json['metric'], 'metric'),
      type,
      comparison,
      json['expected'],
    );
    if (comparison == 'unchanged') {
      if (result.expected != null) {
        throw StateError('Restoration has no literal target.');
      }
    } else {
      result.validateValue(result.expected);
    }
    return result;
  }

  final String metric, type, comparison;
  final Object? expected;

  void validateValue(Object? value) {
    final valid = switch (type) {
      'count' => value is int && value >= 0 && value <= 1000000,
      'fingerprint' =>
        value is String && RegExp(r'^[a-f0-9]{64}$').hasMatch(value),
      'label' =>
        value is String && RegExp(r'^[a-z][a-z0-9-]{0,79}$').hasMatch(value),
      _ => false,
    };
    if (!valid) {
      throw StateError('Metric $metric has an invalid $type observation.');
    }
  }
}

class SandboxResidualRequirement {
  SandboxResidualRequirement._(
    this.id,
    this.lane,
    this.availability,
    this.oracle,
    this.negativeTest,
    this.cleanup,
  );

  factory SandboxResidualRequirement._parse(Object? value) {
    final json = sandboxObject(value, 'residual requirement');
    sandboxFields(json, {
      'id',
      'lane',
      'availability',
      'oracle',
      'negativeTest',
      'cleanup',
    }, 'residual requirement');
    final lane = sandboxText(json['lane'], 'lane');
    final availability = sandboxText(json['availability'], 'availability');
    if (!const {
          'editor',
          'game',
          'os-input',
          'physical-device',
          'human',
        }.contains(lane) ||
        !const {
          'requires-environment',
          'not-implemented',
          'requires-human',
        }.contains(availability) ||
        (lane == 'human') != (availability == 'requires-human')) {
      throw StateError('Invalid residual lane or availability.');
    }
    return SandboxResidualRequirement._(
      sandboxId(json['id'], 'requirement id'),
      lane,
      availability,
      sandboxText(json['oracle'], 'native/human oracle'),
      sandboxText(json['negativeTest'], 'negative test'),
      sandboxText(json['cleanup'], 'cleanup'),
    );
  }

  final String id, lane, availability, oracle, negativeTest, cleanup;
}
