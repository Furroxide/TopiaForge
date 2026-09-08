import 'dart:convert';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';
import 'manifest_migration_fixture.dart';

void main() {
  const planner = ManifestMigrationPlanner();
  ManifestMigrationPlan plan(Map<String, Object?> value, {bool stub = false}) =>
      planner.plan(
        jsonEncode(value),
        sourceLabel: 'normalization.json',
        mode: stub
            ? ManifestMigrationMode.stub
            : ManifestMigrationMode.automatic,
      );
  test('V3 dependency forms normalize without widening available ranges', () {
    final result = plan(
      migrationFixture(3)..addAll({
        'vpmDependencies': {'required.one': '^1.2.3'},
        'dependencies': [
          {'id': 'required.two', 'versionRange': '>=2.0.0'},
          {'id': 'optional.one', 'version': '~1.2.0', 'optional': true},
        ],
        'optionalDependencies': [
          {'id': 'optional.two', 'version': '^0.0.3'},
        ],
        'permissions': ['physics', 'input'],
        'capabilities': ['input', 'hud'],
        'conflicts': [
          {'id': 'other.mod', 'version': '<1.0.0', 'reason': ''},
        ],
      }),
    );
    expect(
      result.disposition,
      ManifestMigrationDisposition.migrated,
      reason: result.diagnostics.join('\n'),
    );
    expect(result.outputJson!['dependencies'], {
      'required.one': '>=1.2.3 <2.0.0',
      'required.two': '>=2.0.0',
    });
    expect(result.outputJson!['optionalDependencies'], {
      'optional.one': '>=1.2.0 <1.3.0',
      'optional.two': '>=0.0.3 <0.0.4',
    });
    expect(result.outputJson!['capabilities'], ['input', 'hud', 'physics']);
    expect(result.outputJson!['conflicts'], [
      {'id': 'other.mod', 'versionRange': '<1.0.0', 'reason': ''},
    ]);
    expect(result.outputJson, isNot(contains('permissions')));
    expect(result.outputJson, isNot(contains('vpmDependencies')));
  });
  final badDependencies = <Object?>[
    null,
    7,
    'all',
    [null],
    [7],
    [{}],
    [
      {'id': 7, 'version': '*'},
    ],
    [
      {'id': 'some.mod'},
    ],
    [
      {'id': 'some.mod', 'version': null},
    ],
    [
      {'id': 'some.mod', 'version': 7},
    ],
    [
      {'id': 'some.mod', 'version': '*', 'optional': null},
    ],
    [
      {'id': 'some.mod', 'version': '*', 'optional': 'true'},
    ],
    [
      {'id': 'some.mod', 'version': '*', 'versionRange': '>=1.0.0'},
    ],
    [
      {'id': 'some.mod', 'version': '*', 'extra': 'information'},
    ],
    [
      {'id': 'some.mod', 'version': '*'},
      {'id': 'SOME.MOD', 'version': '*'},
    ],
    {'some.mod': null},
    {'some.mod': 7},
    {'some.mod': ''},
  ];
  for (var index = 0; index < badDependencies.length; index++) {
    test('V3 malformed dependency $index refuses both modes', () {
      for (final stub in [false, true]) {
        final result = plan(
          migrationFixture(3)..['dependencies'] = badDependencies[index],
          stub: stub,
        );
        expect(result.disposition, ManifestMigrationDisposition.refused);
        expect(result.outputJson, isNull);
        expect(result.diagnostics, isNotEmpty);
      }
    });
  }
  test('V3 required optional collision cannot discard a declaration', () {
    final result = plan(
      migrationFixture(3)..addAll({
        'dependencies': {'same.mod': '*'},
        'optionalDependencies': {'same.mod': '*'},
      }),
      stub: true,
    );
    expect(result.disposition, ManifestMigrationDisposition.refused);
    expect(result.diagnostics.any((d) => d.entryId == 'same.mod'), isTrue);
  });
  test('V3 malformed dependency retains original index', () {
    final result = plan(
      migrationFixture(3)
        ..['dependencies'] = [
          {'id': 'valid.mod', 'version': '*'},
          {'id': 'bad.mod', 'optional': false},
        ],
      stub: true,
    );
    expect(result.disposition, ManifestMigrationDisposition.refused);
    expect(
      result.diagnostics.any(
        (d) => d.originalIndex == 1 && d.entryId == 'bad.mod',
      ),
      isTrue,
    );
  });
  for (final version in [3, 4, 5]) {
    final malformed = <Object?>[
      null,
      7,
      'modes',
      [null],
      [7],
      [{}],
      [
        {'id': 'example.mod.mode'},
      ],
      [
        {'name': 'Mode'},
      ],
      [
        {'id': 7, 'name': 'Mode'},
      ],
      [
        {'id': 'example.mod.mode', 'name': null},
      ],
      [
        {'id': 'example.mod.mode', 'name': 'Mode', 'description': null},
      ],
      [
        {'id': 'example.mod.mode', 'name': 'Mode', 'extra': true},
      ],
      [
        {'id': 'example.mod.mode', 'name': 'One'},
        {'id': 'EXAMPLE.MOD.MODE', 'name': 'Two'},
      ],
    ];
    for (var index = 0; index < malformed.length; index++) {
      test('V$version malformed modes $index refuse both modes', () {
        for (final stub in [false, true]) {
          final result = plan(
            migrationFixture(version)..['worldGamemodes'] = malformed[index],
            stub: stub,
          );
          expect(result.disposition, ManifestMigrationDisposition.refused);
          expect(result.outputJson, isNull);
        }
      });
    }
    test('V$version empty retired array is removed mechanically', () {
      final result = plan(migrationFixture(version)..['worldGamemodes'] = []);
      expect(result.disposition, ManifestMigrationDisposition.migrated);
      expect(result.outputJson, isNot(contains('worldGamemodes')));
      expect(result.outputJson, isNot(contains('contributions')));
      expect(result.outputJson, isNot(contains('capabilities')));
    });
    for (final field in [
      'description',
      'author',
      'supportedSdkVersionRange',
      'conflicts',
      'capabilities',
    ]) {
      test('V$version explicit null $field refuses both modes', () {
        for (final stub in [false, true]) {
          expect(
            plan(
              migrationFixture(version)..[field] = null,
              stub: stub,
            ).disposition,
            ManifestMigrationDisposition.refused,
          );
        }
      });
    }
  }
  test('stub retains17 modes and reports V6 collection bound', () {
    final modes = List.generate(
      17,
      (i) => {'id': 'example.mod.mode$i', 'name': 'Mode $i'},
    );
    final result = plan(
      migrationFixture(5)..['worldGamemodes'] = modes,
      stub: true,
    );
    expect(result.disposition, ManifestMigrationDisposition.invalidStub);
    expect((result.outputJson!['contributions'] as Map)['gamemodes'], modes);
    expect(
      result.diagnostics.any((d) => d.code == 'contribution-limit'),
      isTrue,
    );
  });
  test('stub cannot overwrite original author notes', () {
    final result = plan(
      migrationFixture(5)..addAll({
        'x-migration-todo': {'author': true},
        'worldGamemodes': [
          {'id': 'example.mod.mode', 'name': 'Mode'},
        ],
      }),
      stub: true,
    );
    expect(result.disposition, ManifestMigrationDisposition.refused);
    expect(result.outputJson, isNull);
  });
  test('nested extension values preserve equivalent number spellings', () {
    final raw = jsonEncode(migrationFixture(5));
    final valid =
        '${raw.substring(0, raw.length - 1)},"x-values":[1e0,1.00,true,null]}';
    expect(
      planner.plan(valid, sourceLabel: 'numbers').outputJson!['x-values'],
      [1, 1, true, null],
    );
  });
}
