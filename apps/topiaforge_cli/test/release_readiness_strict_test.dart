import 'dart:convert';
import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/release_readiness.dart';

void main() {
  var root = Directory.current.absolute;
  while (!File(p.join(root.path, 'TopiaForge.slnx')).existsSync()) {
    root = root.parent;
  }
  final source = File(
    p.join(root.path, releaseReadinessPath),
  ).readAsStringSync();
  final schema = File(
    p.join(root.path, releaseReadinessSchemaPath),
  ).readAsBytesSync();
  for (final name in ['schemaVersion', 'status']) {
    test('readiness rejects duplicate $name before decoding loses it', () {
      final document = source.replaceFirst('{', '{"$name":null,');
      expect(
        () => ReleaseReadinessDecision.fromCandidateBlobs(
          readinessBytes: utf8.encode(document),
          schemaBytes: schema,
          targetSha: 'a' * 40,
          expectedReleaseVersion: '0.1.0-rc.1',
        ),
        throwsStateError,
      );
    });
  }
}
