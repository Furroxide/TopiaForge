import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/src/launch_storage_keys.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  final fixture = File(
    p.join('..', '..', 'tests', 'fixtures', 'launch-storage-keys.json'),
  );
  final document =
      jsonDecode(fixture.readAsStringSync()) as Map<String, Object?>;
  test('shared storage corpus uses its supported version', () {
    expect(document['schemaVersion'], 1);
  });
  for (final raw in (document['cases'] as List).cast<Map<String, Object?>>()) {
    test('portable storage key ${raw['kind']} ${raw['filename']}', () {
      final String actual;
      if (raw['kind'] == 'request') {
        actual = LaunchStorageKeys.request(raw['requestId'] as String);
      } else {
        expect(raw['kind'], 'observation');
        actual = LaunchStorageKeys.observation(
          LaunchObservationEnvelope(
            profileId: raw['profileId'] as String,
            profileRevision: raw['profileRevision'] as int,
            producer: PackageIdentity.fromJson(raw['producer']),
            packageSetDigest: raw['packageSetDigest'] as String,
            observationRevision: 0,
          ),
        );
      }
      expect(actual, raw['key']);
      expect(actual, matches(RegExp(r'^[0-9a-f]{64}$')));
      final prefix = raw['kind'] == 'request'
          ? 'launch-profile'
          : 'runtime-observation';
      expect('$prefix-$actual.json', raw['filename']);
    });
  }
}
