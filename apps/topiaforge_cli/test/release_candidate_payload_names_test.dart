import 'dart:convert';
import 'dart:io';

import 'package:json_schema/json_schema.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/release_candidate_payloads.dart';

void main() {
  final root = p.normalize(p.join(Directory.current.path, '..', '..'));
  Map<String, Object?> schema(String name) =>
      (jsonDecode(File(p.join(root, 'schemas', name)).readAsStringSync())
              as Map)
          .cast<String, Object?>();
  final payloadSchemas = {
    for (final kind in ['acceptance', 'readiness'])
      kind: JsonSchema.create(
        (schema(
              'topiaforge.release-candidate-$kind-v1.schema.json',
            )['properties']
            as Map)['payloads'],
      ),
  };
  const version = '0.1.0-rc.1+build.01';
  const names = [
    'TopiaForge.Mods.Abstractions.$version.nupkg',
    'io.github.furroxide.topiaforge.worlds-$version.topiaforgemod',
  ];
  List<Map<String, Object?>> payloads(Iterable<Object?> names) => [
    for (final name in names) {'name': name, 'size': 1, 'sha256': 'a' * 64},
  ];

  test('existing catalog and BOM filename contracts allow build metadata', () {
    expect(SemanticVersion.tryParse(version), isNotNull);
    final catalog = schema('topiaforge.release-catalog.schema.json');
    final artifactSchema = JsonSchema.create(
      (((catalog['properties'] as Map)['releases'] as Map)['items']
          as Map)['properties']['artifacts'],
    );
    final bom = schema('topiaforge.release-bom.schema.json');
    final bomName = JsonSchema.create((bom['definitions'] as Map)['safeName']);
    expect(artifactSchema.validate(names).isValid, isTrue);
    for (final name in names) {
      expect(bomName.validate(name).isValid, isTrue, reason: name);
    }
  });

  test('candidate names accept build metadata without changing spelling', () {
    expect(candidateNames(names, 'Catalog payloads'), names);
  });
  for (final entry in payloadSchemas.entries) {
    test('${entry.key} payload schema accepts build metadata names', () {
      expect(entry.value.validate(payloads(names)).isValid, isTrue);
    });
  }

  test('payload names retain strict ASCII and 180-character bounds', () {
    final legal = ['a', 'A-._+9', 'a+${'b' * 178}'];
    for (final name in legal) {
      expect(candidateNames([name], 'payloads'), [name]);
      for (final entry in payloadSchemas.entries) {
        expect(
          entry.value.validate(payloads([name])).isValid,
          isTrue,
          reason: '${entry.key}: $name',
        );
      }
    }
    final invalid = <Object?>[
      '',
      '+leading.zip',
      '.hidden',
      '-switch',
      '_prefix',
      '../outside.zip',
      r'a\b.zip',
      'a/b.zip',
      'a:b.zip',
      'a%2fb.zip',
      'a b.zip',
      'a\t.zip',
      'a\n.zip',
      'a.zip\n',
      'a.zip\r\n',
      'a\u0000.zip',
      'aé.zip',
      'a😀.zip',
      'a+${'b' * 179}',
      null,
      1,
      true,
      <String>[],
      <String, Object?>{},
    ];
    for (final name in invalid) {
      expect(
        () => candidateNames([name], 'payloads'),
        throwsStateError,
        reason: jsonEncode(name),
      );
      for (final entry in payloadSchemas.entries) {
        expect(
          entry.value.validate(payloads([name])).isValid,
          isFalse,
          reason: '${entry.key}: ${jsonEncode(name)}',
        );
      }
    }
  });

  test('candidate names retain collection and collision restrictions', () {
    for (final raw in <Object?>[
      null,
      'a.zip',
      [],
      List.filled(257, 'a.zip'),
      ['a+1.zip', 'a+1.zip'],
      ['a+1.zip', 'A+1.zip'],
    ]) {
      expect(() => candidateNames(raw, 'payloads'), throwsStateError);
    }
    final maximum = List.generate(256, (index) => 'a+$index.zip');
    expect(candidateNames(maximum, 'payloads'), hasLength(256));
    for (final entry in payloadSchemas.entries) {
      expect(entry.value.validate(payloads(maximum)).isValid, isTrue);
      expect(
        entry.value.validate(payloads([...maximum, 'extra.zip'])).isValid,
        isFalse,
      );
    }
  });
}
