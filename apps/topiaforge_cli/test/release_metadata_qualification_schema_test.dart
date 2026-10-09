import 'dart:convert';
import 'dart:io';

import 'package:json_schema/json_schema.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  final root = Directory.current.parent.parent.path;
  final bom =
      jsonDecode(
            File(
              p.join(root, 'schemas/topiaforge.release-bom.schema.json'),
            ).readAsStringSync(),
          )
          as Map;
  final schema = JsonSchema.create({
    ...Map<String, Object?>.from(bom['definitions']['readiness'] as Map),
    'definitions': bom['definitions'],
  });
  final gates =
      (jsonDecode(
            File(
              p.join(root, 'release/release-readiness.json'),
            ).readAsStringSync(),
          )
          as Map)['gates'];
  final digest = List.filled(64, 'a').join();
  final sha = List.filled(40, 'b').join();
  Map<String, Object?> qualified() => {
    'binding': 'detached-candidate-at-target-sha',
    'path': 'release-candidate-readiness-v1.json',
    'schemaPath':
        'schemas/topiaforge.release-candidate-readiness-v1.schema.json',
    'acceptancePath': 'release-candidate-acceptance-v1.json',
    'status': 'ready',
    'blobSha256': digest,
    'summary': {
      'schema': 'release-candidate-readiness-summary-v1',
      'repository': 'Furroxide/TopiaForge',
      'releaseVersion': '0.1.0-rc.1',
      'targetSha': sha,
      'status': 'ready',
      for (final name in [
        'decisionSha256',
        'acceptanceSha256',
        'handoffSha256',
        'baseReadinessSha256',
        'baseSchemaSha256',
        'policySha256',
        'catalogSha256',
        'contractSha256',
      ])
        name: digest,
      'payloads': [
        {'name': 'TopiaForge-windows-x64.zip', 'size': 1, 'sha256': digest},
      ],
      'gates': gates,
    },
  };
  test('BOM rejects the retired tracked-only publication binding', () {
    final legacy = {
      'binding': 'git-blob-at-target-sha',
      'path': 'release/release-readiness.json',
      'schemaPath': 'schemas/topiaforge.release-readiness-v1.schema.json',
      'status': 'ready',
      'blobSha256': digest,
      'summary': {
        'schema': 'topiaforge-release-readiness-summary-v1',
        'releaseVersion': '0.1.0-rc.1',
        'targetSha': sha,
        'readinessBlobSha256': digest,
        'status': 'ready',
        'gates': gates,
      },
    };
    expect(schema.validate(legacy).isValid, isFalse);
  });
  test('BOM requires all detached qualification digests and exact shape', () {
    final valid = qualified();
    final result = schema.validate(valid);
    expect(result.isValid, isTrue, reason: result.errors.join('\n'));
    for (final name in [
      'decisionSha256',
      'acceptanceSha256',
      'handoffSha256',
      'baseReadinessSha256',
      'baseSchemaSha256',
      'policySha256',
      'catalogSha256',
      'contractSha256',
    ]) {
      final missing = qualified();
      (missing['summary'] as Map).remove(name);
      expect(schema.validate(missing).isValid, isFalse, reason: name);
      final empty = qualified();
      (empty['summary'] as Map)[name] = null;
      expect(schema.validate(empty).isValid, isFalse, reason: name);
    }
    final extras = qualified()..['rawLog'] = 'private';
    expect(schema.validate(extras).isValid, isFalse);
  });
}
