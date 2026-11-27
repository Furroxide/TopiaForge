import 'dart:convert';
import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/release_policy.dart';

import 'release_metadata_source_fixture.dart';

void main() {
  var source = Directory.current.absolute;
  while (!File(p.join(source.path, 'TopiaForge.slnx')).existsSync()) {
    source = source.parent;
  }

  for (final version in ['0.2.0', '0.2.0-rc.1', '1.0.0', '1.0.0-rc.1']) {
    for (final withPin in [false, true]) {
      test('unsigned policy $version with certificate=$withPin', () async {
        final fixture = writeMetadataPrereleaseFixture(source.path);
        addTearDown(() => fixture.deleteSync(recursive: true));
        Map<String, dynamic> read(String path) =>
            jsonDecode(File(p.join(fixture.path, path)).readAsStringSync())
                as Map<String, dynamic>;
        void write(String path, Object value) => File(
          p.join(fixture.path, path),
        ).writeAsStringSync(jsonEncode(value));

        final policy = read('release/release-policy.json');
        (policy['versioning'] as Map)['productVersion'] = version;
        policy['signingIdentities'] = {
          'windowsDistribution': 'unsigned',
          if (withPin) 'windowsCertificateSha256': 'a' * 64,
        };
        write('release/release-policy.json', policy);
        final catalog = read('release/catalog.json');
        final release = ((catalog['releases'] as List).single as Map)
            .cast<String, dynamic>();
        release.addAll({
          'version': version,
          'tag': 'v$version',
          'prerelease': version.contains('-'),
        });
        write('release/catalog.json', catalog);

        final issues = await const ReleasePolicyValidator().validate(
          policy: TopiaForgeReleasePolicy.load(fixture.path),
          release: TopiaForgeReleaseCatalog.load(fixture.path).release(version),
          verifyArchiveHashes: false,
        );
        if (version.startsWith('0.') && !withPin) {
          expect(issues, isEmpty);
        }
        if (!version.startsWith('0.')) {
          expect(issues, contains(contains('only allowed on a 0.x line')));
        }
        if (withPin) {
          expect(issues, contains(contains('must not also pin a signing')));
        }
      });
    }
  }
}
