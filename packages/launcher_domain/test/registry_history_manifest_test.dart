import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_join_support.dart';

void main() {
  final manifest = testManifest('example.sandbox', version: '0.9.0');

  test('a history row carries its manifest and size additively', () {
    final ref = RegistryVersionRef(
      version: '0.9.0',
      downloadUrl: 'https://example.invalid/sandbox-0.9.0.topiaforgemod',
      packageSha256: 'A' * 64,
      manifest: manifest,
      packageBytes: 4096,
    );
    final json = ref.toJson();
    expect(json['packageSha256'], 'a' * 64);
    expect(json['packageBytes'], 4096);
    expect((json['manifest']! as Map)['name'], 'example.sandbox');
    final read = RegistryVersionRef.fromJson(json);
    expect(read.manifest!.version, '0.9.0');
    expect(read.packageBytes, 4096);
    expect(read.extraFields, isEmpty);
  });

  test('a row without them reads and writes as before', () {
    final json = {
      'version': '0.8.0',
      'downloadUrl': 'https://example.invalid/sandbox-0.8.0.topiaforgemod',
      'packageSha256': 'b' * 64,
      'mirror': 'kept',
    };
    final read = RegistryVersionRef.fromJson(json);
    expect(read.manifest, isNull);
    expect(read.packageBytes, isNull);
    expect(read.toJson(), {
      'mirror': 'kept',
      'version': '0.8.0',
      'downloadUrl': 'https://example.invalid/sandbox-0.8.0.topiaforgemod',
      'packageSha256': 'b' * 64,
    });
  });

  test(
    'an unreadable manifest or size leaves the row out of exact installs',
    () {
      for (final bad in <Object?>[
        {'schemaVersion': 5, 'name': 'example.sandbox'},
        {'name': 'example.sandbox'},
        // Wrong member types never escape as errors.
        {'schemaVersion': 6, 'name': 5, 'version': '0.9.0'},
        {'schemaVersion': 6, 'name': 'example.sandbox', 'version': <Object?>[]},
      ]) {
        final read = RegistryVersionRef.fromJson({
          'version': '0.9.0',
          'downloadUrl': 'https://example.invalid/x.topiaforgemod',
          'packageSha256': 'c' * 64,
          'manifest': bad,
        });
        expect(read.manifest, isNull);
      }
      for (final bytes in <Object?>[
        -1,
        ModRegistryFormat.maxPackageBytes + 1,
        1.5,
        '12',
      ]) {
        final read = RegistryVersionRef.fromJson({
          'version': '0.9.0',
          'downloadUrl': 'https://example.invalid/x.topiaforgemod',
          'packageSha256': 'c' * 64,
          'packageBytes': bytes,
        });
        expect(read.packageBytes, isNull, reason: '$bytes');
      }
    },
  );

  test('an index entry round-trips history manifests', () {
    final entry = RegistryIndexEntry(
      manifest: testManifest('example.sandbox'),
      downloadUrl: 'https://example.invalid/sandbox-1.0.0.topiaforgemod',
      packageSha256: 'd' * 64,
      history: [
        RegistryVersionRef(
          version: '0.9.0',
          downloadUrl: 'https://example.invalid/sandbox-0.9.0.topiaforgemod',
          packageSha256: 'e' * 64,
          manifest: manifest,
          packageBytes: 10,
        ),
      ],
    );
    final read = RegistryIndexEntry.fromJson(entry.toJson());
    expect(read.history.single.manifest!.version, '0.9.0');
    expect(read.history.single.packageBytes, 10);
    expect(RegistryMod.fromJson(entry.toJson()).manifest.version, '1.0.0');
  });
}
