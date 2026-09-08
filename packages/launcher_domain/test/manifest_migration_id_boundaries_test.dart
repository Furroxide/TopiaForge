import 'dart:convert';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'manifest_migration_fixture.dart';

void main() {
  const planner = ManifestMigrationPlanner();
  String idAtLength(int length) => 'example.mod.${'a' * (length - 12)}';

  for (final version in [3, 4, 5]) {
    for (final mode in ManifestMigrationMode.values) {
      for (final length in [64, 65, 96, 97]) {
        test('V$version legacy ID length $length in ${mode.name}', () {
          final id = idAtLength(length);
          final originalModes = [
            {'id': 'example.mod.first', 'name': 'First'},
            {'id': id, 'name': 'Boundary', 'description': ''},
          ];
          final source = jsonEncode(
            migrationFixture(version)..['worldGamemodes'] = originalModes,
          );
          final result = planner.plan(
            source,
            sourceLabel: 'legacy-boundary.json',
            mode: mode,
          );
          expect(result.sourceText, source);
          final idErrors = result.diagnostics.where(
            (d) =>
                d.code == 'malformed-mode' &&
                d.jsonPointer == '/worldGamemodes/1/id',
          );
          if (length > 64) {
            expect(result.disposition, ManifestMigrationDisposition.refused);
            expect(result.outputJson, isNull);
            expect(result.outputText, isNull);
            expect(idErrors, hasLength(1));
            final diagnostic = idErrors.single;
            expect(diagnostic.originalIndex, 1);
            expect(diagnostic.entryId, id);
            expect(diagnostic.sourceLabel, 'legacy-boundary.json');
            expect(diagnostic.message, contains('2–64'));
          } else {
            expect(idErrors, isEmpty);
            expect(
              result.diagnostics.any(
                (d) =>
                    d.code == 'missing-implementation' &&
                    d.originalIndex == 1 &&
                    d.entryId == id,
              ),
              isTrue,
            );
            if (mode == ManifestMigrationMode.stub) {
              expect(
                result.disposition,
                ManifestMigrationDisposition.invalidStub,
              );
              final contributions = result.outputJson!['contributions'] as Map;
              expect(contributions.keys, ['gamemodes']);
              expect(contributions['gamemodes'], originalModes);
              for (final entry in contributions['gamemodes'] as List) {
                expect(entry, isNot(contains('implementation')));
                expect(entry, isNot(contains('worldRequirements')));
              }
            } else {
              expect(result.disposition, ManifestMigrationDisposition.refused);
              expect(result.outputText, isNull);
            }
          }
        });
      }
    }
  }

  for (final mode in ManifestMigrationMode.values) {
    for (final length in [64, 65, 96, 97]) {
      test('V6 declaration ID length $length in ${mode.name}', () {
        final source = jsonEncode(
          migrationFixture(6)..addAll({
            'capabilities': ['world-service'],
            'contributions': {
              'gamemodes': [
                {
                  'id': idAtLength(length),
                  'name': 'Boundary',
                  'implementation': {'type': 'Example.Factory'},
                },
              ],
            },
          }),
        );
        final result = planner.plan(
          source,
          sourceLabel: 'v6-boundary.json',
          mode: mode,
        );
        expect(result.sourceText, source);
        expect(result.outputText, isNull);
        expect(
          result.disposition,
          length <= 96
              ? ManifestMigrationDisposition.unchanged
              : ManifestMigrationDisposition.refused,
        );
        expect(result.diagnostics, length <= 96 ? isEmpty : isNotEmpty);
      });
    }
  }
}
