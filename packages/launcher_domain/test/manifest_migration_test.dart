import 'dart:convert';
import 'dart:io';
import 'package:json_schema/json_schema.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';
import 'manifest_migration_fixture.dart';

void main() {
  const planner = ManifestMigrationPlanner();
  ManifestMigrationPlan plan(Map<String, Object?> json, {bool stub = false}) =>
      planner.plan(
        jsonEncode(json),
        sourceLabel: 'fixture/topiaforge.mod.json',
        mode: stub
            ? ManifestMigrationMode.stub
            : ManifestMigrationMode.automatic,
      );
  for (final version in [3, 4, 5]) {
    test('V$version preserves values and optional presence without modes', () {
      final input = migrationFixture(version)
        ..addAll({
          'dependencies': <String, Object?>{},
          'optionalDependencies': <String, Object?>{},
          'conflicts': <Object?>[],
          'description': '',
          'capabilities': <String>[],
          'tags': <String>[],
          'x-values': {
            'null': null,
            'flag': false,
            'integer': 9,
            'decimal': 1.5,
            'ordered': [3, 2, 1],
          },
        });
      final result = plan(input);
      expect(result.disposition, ManifestMigrationDisposition.migrated);
      final expected = {
        ...input,
        'schemaVersion': 6,
        r'$schema': ModManifest.canonicalSchemaUrl,
      };
      expect(result.outputJson, expected);
      expect(
        ModManifest.fromJson(
          result.outputJson!,
        ).validate().where((i) => i.isBlocking),
        isEmpty,
      );
      expect(
        () => result.outputJson!['x-values'] = null,
        throwsUnsupportedError,
      );
      expect(
        () => ((result.outputJson!['x-values'] as Map)['ordered'] as List)
            .clear(),
        throwsUnsupportedError,
      );
    });
    test('V$version stub keeps original mode metadata and remains invalid', () {
      final input = migrationFixture(version)
        ..['worldGamemodes'] = [
          {'id': 'example.mod.free', 'name': 'Free', 'description': ''},
          {'id': 'foreign.mode', 'name': 'Foreign'},
        ];
      expect(plan(input).disposition, ManifestMigrationDisposition.refused);
      final result = plan(input, stub: true);
      expect(result.disposition, ManifestMigrationDisposition.invalidStub);
      final modes =
          (result.outputJson!['contributions'] as Map)['gamemodes'] as List;
      expect(modes, input['worldGamemodes']);
      expect(result.outputJson, isNot(contains('worldGamemodes')));
      expect((result.outputJson!['contributions'] as Map).keys, ['gamemodes']);
      expect(
        result.diagnostics
            .where((d) => d.code == 'ownership')
            .single
            .originalIndex,
        1,
      );
      expect(
        result.diagnostics.where((d) => d.code == 'ownership').single.entryId,
        'foreign.mode',
      );
      expect(result.outputJson!['x-migration-todo'], isNotEmpty);
      expect(
        () => ModManifest.fromJson(result.outputJson!),
        throwsFormatException,
      );
      var dir = Directory.current;
      while (!File('${dir.path}/TopiaForge.slnx').existsSync()) {
        dir = dir.parent;
      }
      final schema = JsonSchema.create(
        jsonDecode(
          File(
            '${dir.path}/schemas/topiaforge.mod.v6.schema.json',
          ).readAsStringSync(),
        ),
      );
      expect(schema.validate(result.outputJson).isValid, isFalse);
      for (final declaration in modes.cast<Map>()) {
        expect(declaration, isNot(contains('worldRequirements')));
        expect(declaration, isNot(contains('implementation')));
      }
    });
    for (final stub in [false, true]) {
      test(
        'V$version malformed mode entry retains original index stub=$stub',
        () {
          final input = migrationFixture(version)
            ..['worldGamemodes'] = [
              {'id': 'example.mod.ok', 'name': 'OK'},
              {'id': 'example.mod.bad', 'name': 7},
            ];
          final result = plan(input, stub: stub);
          expect(result.disposition, ManifestMigrationDisposition.refused);
          expect(result.outputJson, isNull);
          final issue = result.diagnostics.firstWhere(
            (d) => d.originalIndex == 1,
          );
          expect(issue.entryId, 'example.mod.bad');
          expect(issue.toString(), contains('fixture/topiaforge.mod.json'));
        },
      );
    }
    test('V$version missing ranges require author values, never wildcard', () {
      final input = migrationFixture(version)
        ..remove('supportedGameVersionRange');
      expect(plan(input).disposition, ManifestMigrationDisposition.refused);
      final result = plan(input, stub: true);
      expect(result.disposition, ManifestMigrationDisposition.invalidStub);
      expect(result.outputJson, isNot(contains('supportedGameVersionRange')));
      expect(
        result.diagnostics.any(
          (d) => d.requiredAuthorFields.contains('supportedGameVersionRange'),
        ),
        isTrue,
      );
    });
  }
  test('valid V6 is an exact-byte no-op', () {
    final text = '  ${jsonEncode(migrationFixture(6))}\n';
    final result = planner.plan(text, sourceLabel: 'file');
    expect(result.disposition, ManifestMigrationDisposition.unchanged);
    expect(result.sourceText, text);
    expect(result.outputText, isNull);
  });
  for (final fragment in [
    '"schemaVersion":5.0',
    '"schemaVersion":5,"schemaVersion":5',
    r'"schemaVersion":5,"schem\u0061Version":5',
    '"schemaVersion":5,"x-number":1234567890123456789012345678901234567890',
  ]) {
    test('raw JSON cannot lose information $fragment', () {
      final text = jsonEncode(
        migrationFixture(5),
      ).replaceFirst('"schemaVersion":5', fragment);
      for (final mode in ManifestMigrationMode.values) {
        expect(
          planner.plan(text, sourceLabel: 'raw', mode: mode).disposition,
          ManifestMigrationDisposition.refused,
        );
      }
    });
  }
}
