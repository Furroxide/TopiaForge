// The Dart half of the cross-language gamemode-contract fixture harness. The C#
// half is tests/TopiaForge.ModManager.Tests/GamemodeContractConformanceTests.cs
// and makes the same five assertions, so neither language can quietly stop
// executing a case the other still runs.
//
// This side additionally owns the `schema` channel and validates every case file
// against fixture.schema.json, because C# has no JSON Schema validator at all --
// `grep -rn "topiaforge.mod.schema.json" --include=*.cs` returns nothing. The
// schema constrains Dart alone, which is exactly why the fixtures, not the
// schema, are what hold the two readers to one contract.

import 'dart:convert';
import 'dart:io';

import 'package:json_schema/json_schema.dart';
import 'package:test/test.dart';

import 'gamemode_contract_conformance_cases.dart';

const _runnerName = 'dart';

void main() {
  final fixtureRoot = _join(_repoRoot().path, [
    'tests',
    'fixtures',
    'gamemode-v6',
  ]);
  final index =
      jsonDecode(File(_join(fixtureRoot, ['index.json'])).readAsStringSync())
          as Map<String, Object?>;
  final channelRunners = <String, Set<String>>{
    for (final entry
        in (index['channelRunners']! as Map<String, Object?>).entries)
      entry.key: (entry.value! as List).cast<String>().toSet(),
  };
  final cases = (index['cases']! as List).cast<Map<String, Object?>>();

  test('the fixture index is closed over the fixtures on disk', () {
    expect(cases, isNotEmpty, reason: 'an empty index asserts nothing');
    final onDisk = <String>{};
    for (final channel in channelRunners.keys) {
      final directory = Directory(_join(fixtureRoot, [channel]));
      if (!directory.existsSync()) {
        continue;
      }
      for (final file
          in directory.listSync(recursive: true).whereType<File>()) {
        if (!file.path.endsWith('.json')) {
          continue;
        }
        onDisk.add(_relative(fixtureRoot, file.path));
      }
    }

    final indexed = cases.map((item) => item['path']! as String).toSet();
    expect(
      onDisk.difference(indexed),
      isEmpty,
      reason:
          'these fixtures are on disk but no runner executes them. Run '
          'python3 .github/scripts/check_fixture_index.py --write',
    );
    expect(
      indexed.difference(onDisk),
      isEmpty,
      reason: 'the index lists fixtures that are not on disk',
    );
  });

  test('every fixture satisfies the fixture contract', () {
    final schema = JsonSchema.create(
      jsonDecode(
            File(
              _join(fixtureRoot, ['fixture.schema.json']),
            ).readAsStringSync(),
          )
          as Map<String, Object?>,
    );
    for (final entry in cases) {
      final path = entry['path']! as String;
      final body = _readCase(fixtureRoot, path);
      final result = schema.validate(body);
      expect(
        result.isValid,
        isTrue,
        reason: '$path\n${result.errors.join('\n')}',
      );
      expect(body['id'], entry['id'], reason: '$path disagrees with the index');
      expect(
        body['kind'],
        entry['kind'],
        reason: '$path declares a kind the index disagrees with',
      );
    }
  });

  test('a divergence between the two readers is stated, not implied', () {
    for (final entry in cases) {
      final path = entry['path']! as String;
      final body = _readCase(fixtureRoot, path);
      final expected = body['expect']! as Map<String, Object?>;
      final obliged = channelRunners[entry['channel']!]!;
      for (final runner in obliged) {
        expect(
          expected.containsKey(runner),
          isTrue,
          reason: '$path is missing the expectation for obliged $runner',
        );
      }

      // A divergence is one side accepting what the other rejects. Differing
      // error codes for the same verdict are ordinary: a kind may give each
      // runner a different operation to perform.
      final verdicts = obliged
          .map((runner) => (expected[runner]! as Map)['outcome'] as String)
          .toSet();
      final explained = (body['divergenceReason'] as String? ?? '').isNotEmpty;
      expect(
        verdicts.length == 1 || explained,
        isTrue,
        reason:
            '$path expects different verdicts per runner without a '
            'divergenceReason. A divergence between the two readers is a '
            'finding, not a detail.',
      );
      expect(
        verdicts.length > 1 || !explained,
        isTrue,
        reason:
            '$path carries a divergenceReason but every runner reaches the '
            'same verdict; delete it so a real divergence stays visible.',
      );
    }
  });

  test('this runner executes every case the index obliges it to', () {
    final executed = <String, int>{};
    for (final entry in cases) {
      final channel = entry['channel']! as String;
      if (!channelRunners[channel]!.contains(_runnerName)) {
        continue;
      }

      final path = entry['path']! as String;
      final body = _readCase(fixtureRoot, path);
      final expected =
          (body['expect']! as Map<String, Object?>)[_runnerName]!
              as Map<String, Object?>;
      final actual = _execute(entry['kind']! as String, body, path);

      expect(
        actual.accepted,
        expected['outcome'] == 'accept',
        reason: '$path: ${actual.detail}',
      );
      expect(
        actual.errorCodes,
        ((expected['errorCodes'] as List?) ?? const []).cast<String>().toSet(),
        reason: '$path: ${actual.detail}',
      );
      final normalized = expected['normalized'] as Map<String, Object?>?;
      if (normalized != null) {
        final digest = declarationDigest(body);
        for (final kind in const ['worlds', 'gamemodes', 'launchTargets']) {
          expect(
            digest[kind],
            (normalized[kind]! as List).cast<String>(),
            reason:
                '$path: $kind parsed differently from what the fixture pins',
          );
        }
      }
      executed[channel] = (executed[channel] ?? 0) + 1;
    }

    final obliged = channelRunners.entries
        .where((entry) => entry.value.contains(_runnerName))
        .map((entry) => entry.key)
        .toSet();
    expect(
      obliged,
      isNotEmpty,
      reason:
          'no channel obliges $_runnerName, so this harness asserts nothing',
    );
    for (final channel in channelRunners.keys) {
      final available = cases
          .where((item) => item['channel'] == channel)
          .length;
      expect(
        executed[channel] ?? 0,
        obliged.contains(channel) ? available : 0,
        reason: 'channel $channel executed the wrong number of cases',
      );
    }
  });
}

/// Dispatches exhaustively. A kind this runner does not implement fails the
/// suite rather than falling through as a pass, which is how a fixture becomes
/// decorative.
ConformanceOutcome _execute(
  String kind,
  Map<String, Object?> body,
  String path,
) {
  switch (kind) {
    case 'launch-intent-round-trip':
      return runLaunchIntentRoundTrip(body);
    case 'launch-intent-hostile':
      return runLaunchIntentHostile(body);
    case 'manifest-accepts':
    case 'manifest-rejects':
      return runManifest(body);
    default:
      fail(
        '$path has kind "$kind", which the Dart conformance runner does '
        'not implement.',
      );
  }
}

Map<String, Object?> _readCase(String fixtureRoot, String path) =>
    jsonDecode(File(_join(fixtureRoot, path.split('/'))).readAsStringSync())
        as Map<String, Object?>;

String _relative(String fixtureRoot, String path) {
  final normalized = path.replaceAll(r'\', '/');
  final root = fixtureRoot.replaceAll(r'\', '/');
  return normalized.startsWith('$root/')
      ? normalized.substring(root.length + 1)
      : normalized;
}

Directory _repoRoot() {
  var directory = Directory.current.absolute;
  while (true) {
    if (File(_join(directory.path, ['TopiaForge.slnx'])).existsSync()) {
      return directory;
    }
    final parent = directory.parent;
    if (parent.path == directory.path) {
      throw StateError('Could not locate TopiaForge.slnx.');
    }
    directory = parent;
  }
}

String _join(String root, List<String> parts) =>
    [root, ...parts].join(Platform.pathSeparator);
