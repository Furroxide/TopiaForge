import 'dart:convert';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';
import 'manifest_migration_fixture.dart';

void main() {
  const planner = ManifestMigrationPlanner();
  ManifestMigrationPlan plan(Map<String, Object?> input, bool stub) =>
      planner.plan(
        jsonEncode(input),
        sourceLabel: 'indexed.json',
        mode: stub
            ? ManifestMigrationMode.stub
            : ManifestMigrationMode.automatic,
      );
  for (final version in [3, 4, 5]) {
    for (final value in ['', '  ']) {
      test('V$version empty available range cannot widen to any: "$value"', () {
        for (final field in [
          'supportedGameVersionRange',
          'supportedLoaderVersionRange',
          'supportedSdkVersionRange',
        ]) {
          for (final stub in [false, true]) {
            final result = plan(
              migrationFixture(version)..[field] = value,
              stub,
            );
            expect(result.disposition, ManifestMigrationDisposition.refused);
            expect(result.outputText, isNull);
            expect(
              result.diagnostics.any((d) => d.jsonPointer == '/$field'),
              isTrue,
            );
          }
        }
      });
    }
    final conflicts = <Object?>[
      null,
      7,
      {},
      {'id': 7},
      {'id': 'other.mod', 'reason': 7},
      {'id': 'other.mod', 'versionRange': null},
      {'id': 'other.mod', 'extra': true},
    ];
    for (var i = 0; i < conflicts.length; i++) {
      test(
        'V$version malformed conflict $i reports original index and identity',
        () {
          for (final stub in [false, true]) {
            final bad = conflicts[i];
            final result = plan(
              migrationFixture(version)
                ..['conflicts'] = [
                  {'id': 'valid.mod'},
                  bad,
                ],
              stub,
            );
            expect(result.disposition, ManifestMigrationDisposition.refused);
            final issues = result.diagnostics.where(
              (d) => d.originalIndex == 1,
            );
            expect(issues, isNotEmpty);
            if (bad is Map && bad['id'] is String) {
              expect(issues.any((d) => d.entryId == bad['id']), isTrue);
            }
            expect(
              issues.every((d) => d.sourceLabel == 'indexed.json'),
              isTrue,
            );
          }
        },
      );
    }
    for (final field in [
      'loadAfter',
      'loadBefore',
      'tags',
      'capabilities',
      'screenshots',
    ]) {
      test('V$version malformed $field reports original array index', () {
        final result = plan(
          migrationFixture(version)..[field] = ['good', 7],
          true,
        );
        expect(result.disposition, ManifestMigrationDisposition.refused);
        expect(
          result.diagnostics.any(
            (d) => d.originalIndex == 1 && d.jsonPointer == '/$field/1',
          ),
          isTrue,
        );
      });
    }
  }
  test('V3 whitespace dependency range refuses instead of inventing any', () {
    for (final stub in [false, true]) {
      final result = plan(
        migrationFixture(3)
          ..['dependencies'] = [
            {'id': 'other.mod', 'version': '  '},
          ],
        stub,
      );
      expect(result.disposition, ManifestMigrationDisposition.refused);
      expect(
        result.diagnostics.any(
          (d) => d.originalIndex == 0 && d.entryId == 'other.mod',
        ),
        isTrue,
      );
    }
  });
  test('legacy schema pointer cannot erase a malformed original value', () {
    for (final version in [3, 4, 5]) {
      for (final value in [null, 7, 'x' * 513]) {
        for (final stub in [false, true]) {
          final result = plan(
            migrationFixture(version)..[r'$schema'] = value,
            stub,
          );
          expect(result.disposition, ManifestMigrationDisposition.refused);
          expect(result.outputText, isNull);
          expect(
            result.diagnostics.any((d) => d.jsonPointer == r'/$schema'),
            isTrue,
          );
        }
      }
    }
  });
  test(
    'stub preserves capabilities and explains required world-service addition',
    () {
      final result = plan(
        migrationFixture(5)..addAll({
          'capabilities': ['input', 'hud'],
          'worldGamemodes': [
            {'id': 'example.mod.mode', 'name': 'Mode'},
          ],
        }),
        true,
      );
      expect(result.disposition, ManifestMigrationDisposition.invalidStub);
      expect(result.outputJson!['capabilities'], [
        'input',
        'hud',
        'world-service',
      ]);
      expect(
        result.diagnostics.any((d) => d.message.contains('world-service')),
        isTrue,
      );
    },
  );
}
