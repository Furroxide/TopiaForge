import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

ModManifest _manifest({
  String id = 'author.jetpack',
  String version = '1.2.0',
  Map<String, Object?> extra = const {},
}) {
  return ModManifest.fromJson({
    'schemaVersion': 2,
    'name': id,
    'displayName': 'Jetpack',
    'version': version,
    'author': {'name': 'Author'},
    'entryAssembly': 'Jetpack.dll',
    'entryType': 'Author.Jetpack.JetpackMod',
    'description': 'Fly around.',
    'vpmDependencies': {'robotopia.robotkit': '>=0.7.0'},
    ...extra,
  });
}

const _sha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';

void main() {
  group('RegistryIndexEntry', () {
    test('toJson round-trips through RegistryMod.fromJson', () {
      final entry = RegistryIndexEntry(
        manifest: _manifest(),
        downloadUrl: 'https://example.com/author.jetpack-1.2.0.robotopiamod',
        packageSha256: _sha.toUpperCase(),
        changelog: 'Added fuel gauge.',
        origin: 'community',
        history: [
          RegistryVersionRef(
            version: '1.1.0',
            downloadUrl:
                'https://example.com/author.jetpack-1.1.0.robotopiamod',
            packageSha256: _sha,
          ),
        ],
      );

      final mod = RegistryMod.fromJson(entry.toJson());

      expect(mod.manifest.id, 'author.jetpack');
      expect(mod.manifest.version, '1.2.0');
      expect(mod.manifest.dependencies, hasLength(1));
      expect(mod.manifest.dependencies.single.id, 'robotopia.robotkit');
      expect(
        mod.downloadUrl,
        'https://example.com/author.jetpack-1.2.0.robotopiamod',
      );
      expect(mod.packageSha256, _sha, reason: 'sha is normalized lowercase');
      expect(mod.changelog, 'Added fuel gauge.');
    });

    test('fromJson tolerates unknown keys and missing optionals', () {
      final entry = RegistryIndexEntry.fromJson({
        'manifest': _manifest().toJson(),
        'downloadUrl': 'https://example.com/pkg.robotopiamod',
        'packageSha256': _sha,
        'someFutureField': {'nested': true},
      });

      expect(entry.manifest.id, 'author.jetpack');
      expect(entry.origin, isEmpty);
      expect(entry.history, isEmpty);
      expect(entry.toJson()['someFutureField'], {'nested': true});
    });

    test('preserves unknown index and history fields immutably', () {
      final entry = RegistryIndexEntry.fromJson({
        'manifest': _manifest().toJson(),
        'downloadUrl': 'https://example.com/pkg.robotopiamod',
        'packageSha256': _sha,
        'futureIndex': {
          'channels': ['stable'],
        },
        'history': [
          {
            'version': '1.1.0',
            'downloadUrl': 'https://example.com/old.robotopiamod',
            'packageSha256': _sha,
            'futureHistory': {'provenance': true},
          },
        ],
      });

      final rewritten = entry.toJson();
      expect(rewritten['futureIndex'], {
        'channels': ['stable'],
      });
      expect((rewritten['history'] as List).single['futureHistory'], {
        'provenance': true,
      });
      expect(() => entry.extraFields['mutate'] = true, throwsUnsupportedError);
      final nested = entry.extraFields['futureIndex'] as Map;
      expect(() => nested['mutate'] = true, throwsUnsupportedError);
      expect(
        () => (nested['channels'] as List).add('nightly'),
        throwsUnsupportedError,
      );
    });
  });

  group('RegistryEntryFile.validate', () {
    RegistryEntryFile entryWith({
      String id = 'author.jetpack',
      int formatVersion = 1,
      List<RegistryEntryVersion>? versions,
    }) {
      return RegistryEntryFile(
        id: id,
        formatVersion: formatVersion,
        versions:
            versions ??
            [
              RegistryEntryVersion(
                version: '1.2.0',
                downloadUrl: 'https://example.com/pkg.robotopiamod',
                packageSha256: _sha,
                manifest: _manifest(),
              ),
            ],
      );
    }

    test('accepts a well-formed entry', () {
      expect(entryWith().validate(), isEmpty);
    });

    test('round-trips through toJson/fromJson', () {
      final restored = RegistryEntryFile.fromJson(entryWith().toJson());
      expect(restored.id, 'author.jetpack');
      expect(restored.versions.single.manifest?.id, 'author.jetpack');
      expect(restored.validate(), isEmpty);
    });

    test('preserves nested unknown entry and version fields', () {
      final source = {
        r'$schema': ModRegistryFormat.canonicalEntrySchemaUrl,
        'formatVersion': 1,
        'id': 'author.jetpack',
        'futureEntry': {
          'maintainers': ['author'],
        },
        'versions': [
          {
            'version': '1.2.0',
            'downloadUrl': 'https://example.com/pkg.robotopiamod',
            'packageSha256': _sha,
            'futureVersion': {'trust': 'verified'},
            'manifest': _manifest(
              extra: {
                'futureManifest': {'flag': true},
              },
            ).toJson(),
          },
        ],
      };

      final rewritten = RegistryEntryFile.fromJson(source).toJson();
      final version = (rewritten['versions'] as List).single as Map;
      expect(rewritten['futureEntry'], {
        'maintainers': ['author'],
      });
      expect(version['futureVersion'], {'trust': 'verified'});
      expect((version['manifest'] as Map)['futureManifest'], {'flag': true});
    });

    test('canonical fields take precedence over supplied extras', () {
      final version = RegistryEntryVersion(
        version: '1.2.0',
        downloadUrl: 'https://example.com/pkg.robotopiamod',
        packageSha256: _sha,
        manifest: _manifest(),
        extraFields: {'version': 'evil', 'changelog': 'evil', 'future': true},
      );
      final entry = RegistryEntryFile(
        id: 'author.jetpack',
        versions: [version],
        extraFields: {
          r'$schema': 'https://evil.invalid/schema',
          'id': 'evil',
          'versions': const [],
          'future': true,
        },
      ).toJson();

      expect(entry[r'$schema'], ModRegistryFormat.canonicalEntrySchemaUrl);
      expect(entry['id'], 'author.jetpack');
      expect(entry['future'], isTrue);
      final rewrittenVersion = (entry['versions'] as List).single as Map;
      expect(rewrittenVersion['version'], '1.2.0');
      expect(rewrittenVersion.containsKey('changelog'), isFalse);
      expect(rewrittenVersion['future'], isTrue);
    });

    test('rejects wrong formatVersion, bad id, and empty versions', () {
      final issues = entryWith(
        id: '!',
        formatVersion: 2,
        versions: const [],
      ).validate();
      final messages = issues.map((issue) => issue.message).join(' ');
      expect(messages, contains('formatVersion'));
      expect(messages, contains('id must be'));
      expect(messages, contains('at least one entry'));
    });

    test('rejects duplicate versions, bad sha, and non-https URLs', () {
      RegistryEntryVersion version(String url, {String sha = _sha}) {
        return RegistryEntryVersion(
          version: '1.2.0',
          downloadUrl: url,
          packageSha256: sha,
          manifest: _manifest(),
        );
      }

      final issues = entryWith(
        versions: [
          version('http://example.com/pkg.robotopiamod', sha: 'zz'),
          version('https://example.com/pkg.robotopiamod'),
        ],
      ).validate();
      final messages = issues.map((issue) => issue.message).join(' ');
      expect(messages, contains('absolute https URL'));
      expect(messages, contains('64 hex characters'));
      expect(messages, contains('duplicate version'));
    });

    test('rejects credentials, query, fragment, and loopback HTTP', () {
      for (final url in [
        'http://127.0.0.1:8123/pkg.robotopiamod',
        'https://user:secret@example.com/pkg.robotopiamod',
        'https://example.com/pkg.robotopiamod?token=secret',
        'https://example.com/pkg.robotopiamod#latest',
      ]) {
        final issues = entryWith(
          versions: [
            RegistryEntryVersion(
              version: '1.2.0',
              downloadUrl: url,
              packageSha256: _sha,
              manifest: _manifest(),
            ),
          ],
        ).validate();
        expect(
          issues.where((issue) => issue.isBlocking),
          isNotEmpty,
          reason: url,
        );
      }
    });

    test('rejects an unsafe public homepage URL', () {
      final entry = RegistryEntryFile(
        id: 'author.jetpack',
        homepage: 'https://example.com/mod?token=secret',
        versions: entryWith().versions,
      );
      expect(
        entry.validate().map((issue) => issue.message),
        contains(contains('homepage')),
      );
    });

    test('requires the inline manifest to match id and version', () {
      final issues = entryWith(
        versions: [
          RegistryEntryVersion(
            version: '9.9.9',
            downloadUrl: 'https://example.com/pkg.robotopiamod',
            packageSha256: _sha,
            manifest: _manifest(id: 'other.mod', version: '1.0.0'),
          ),
          RegistryEntryVersion(
            version: '1.0.0',
            downloadUrl: 'https://example.com/pkg2.robotopiamod',
            packageSha256: _sha,
          ),
        ],
      ).validate();
      final messages = issues.map((issue) => issue.message).join(' ');
      expect(messages, contains('does not match the entry id'));
      expect(messages, contains('does not match the entry version'));
      expect(messages, contains('manifest is required inline'));
    });
  });

  test('sortedVersions orders newest first with unparseable last', () {
    RegistryEntryVersion version(String value) {
      return RegistryEntryVersion(
        version: value,
        downloadUrl: 'https://example.com/$value.robotopiamod',
        packageSha256: _sha,
        manifest: _manifest(version: value),
      );
    }

    final entry = RegistryEntryFile(
      id: 'author.jetpack',
      versions: [version('1.0.0'), version('nonsense'), version('2.1.0')],
    );

    expect(entry.sortedVersions.map((item) => item.version).toList(), [
      '2.1.0',
      '1.0.0',
      'nonsense',
    ]);
  });
}
