// Independent V6 JSON Schema evaluation plus both readers' shared expectations.
// Every file is indexed, every operation is explicit, and normalization preserves
// all declaration fields and meaningful presence through serialization.

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
    for (final file in Directory(
      fixtureRoot,
    ).listSync(recursive: true).whereType<File>()) {
      final path = _relative(fixtureRoot, file.path);
      if (const {'index.json', 'fixture.schema.json'}.contains(path)) continue;
      expect(
        path.endsWith('.json'),
        isTrue,
        reason: '$path is an unexpected non-JSON fixture file',
      );
      onDisk.add(path);
      expect(
        path.contains('/') && channelRunners.containsKey(path.split('/').first),
        isTrue,
        reason: '$path is outside a known channel',
      );
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
        body['channel'],
        entry['channel'],
        reason: '$path disagrees with the index',
      );
      final directory = (body['kind']! as String).startsWith('manifest-')
          ? 'manifest'
          : 'launch-intent';
      expect(
        path.startsWith('${body['channel']}/$directory/'),
        isTrue,
        reason: '$path is misplaced for its kind',
      );
      expect(
        body['kind'],
        entry['kind'],
        reason: '$path declares a kind the index disagrees with',
      );
    }
  });

  test('equivalent operations require identical expectations', () {
    for (final entry in cases) {
      final path = entry['path']! as String;
      final body = _readCase(fixtureRoot, path);
      final expected = body['expect']! as Map<String, Object?>;
      expect(
        expected.keys.toSet(),
        channelRunners[entry['channel']!],
        reason: path,
      );
      if (body.containsKey('manifest')) {
        expect(
          expected['csharp'],
          expected['dart'],
          reason: '$path has divergent reader expectations',
        );
        expect(body.containsKey('divergenceReason'), isFalse, reason: path);
        if ((expected['dart']! as Map)['outcome'] == 'accept') {
          expect(
            (expected['dart']! as Map)['normalized'],
            isA<Map>(),
            reason: path,
          );
        }
      } else {
        expect(body['operations'], {
          'csharp': 'read-intent',
          'dart': 'write-intent',
        }, reason: path);
      }
    }
  });

  final schemaDocument =
      jsonDecode(
            File(
              _join(_repoRoot().path, [
                'schemas',
                'topiaforge.mod.v6.schema.json',
              ]),
            ).readAsStringSync(),
          )
          as Map<String, Object?>;
  final manifestSchema = JsonSchema.create(schemaDocument);
  test('normalization explicitly covers every contribution schema field', () {
    const definitions = {
      'worlds': 'worldDeclaration',
      'gamemodes': 'gamemodeDeclaration',
      'launchTargets': 'launchTargetDeclaration',
      'content': 'worldContent',
      'implementation': 'implementationBinding',
      'spawn': 'spawnPolicy',
      'worldRequirements': 'worldRequirements',
      'world': 'worldPolicy',
    };
    final schemaDefinitions = schemaDocument['definitions']! as Map;
    expect(
      contributionNormalizationFields.keys.toSet(),
      definitions.keys.toSet(),
    );
    for (final entry in definitions.entries) {
      final properties =
          (schemaDefinitions[entry.value]! as Map)['properties']! as Map;
      expect(
        contributionNormalizationFields[entry.key]!.toSet(),
        properties.keys.toSet(),
        reason: entry.key,
      );
    }
  });
  for (final entry in cases) {
    final path = entry['path']! as String;
    final body = _readCase(fixtureRoot, path);
    if (!body.containsKey('manifest')) continue;
    test('$path schema', () {
      expect(
        body['schemaOutcome'],
        anyOf('accept', 'reject'),
        reason: 'schemaOutcome is mandatory',
      );
      final result = manifestSchema.validate(body['manifest']);
      expect(
        result.isValid,
        body['schemaOutcome'] == 'accept',
        reason: result.errors.join('\n'),
      );
    });
    test('$path reader', () {
      final expected = (body['expect']! as Map)['dart']! as Map;
      final actual = runManifest(body);
      expect(
        actual.accepted,
        expected['outcome'] == 'accept',
        reason: actual.detail,
      );
      expect(
        actual.errorCodes,
        ((expected['errorCodes'] as List?) ?? const []).cast<String>().toSet(),
        reason: actual.detail,
      );
    });
  }

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
        expect(
          declarationDigest(body),
          normalized,
          reason: '$path parsed fields or presence differ',
        );
        expect(
          roundTripDigest(body),
          normalized,
          reason: '$path serialized fields or presence differ',
        );
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
    case 'manifest-model-rejects':
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
