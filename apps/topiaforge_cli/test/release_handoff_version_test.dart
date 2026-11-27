import 'dart:convert';
import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/release_handoff.dart';
import 'package:topiaforge/src/release_handoff_models.dart';

import 'release_handoff_policy_fixture.dart';
import 'release_handoff_qa_fixture.dart';

void main() {
  const version = '0.1.0-rc.1';
  const targetSha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
  const ecosystemSha =
      'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';
  late Directory temp;
  late String root;

  setUp(() {
    temp = Directory.systemTemp.createTempSync('topiaforge-handoff-version-');
    root = writeSignedReleaseHandoffRoot(temp, version: version);
    File(
      p.join(temp.path, 'TopiaForge-windows-x64.zip'),
    ).writeAsStringSync('archive');
    writeReleaseQaFixtures(
      repositoryRoot: root,
      assets: temp,
      version: version,
      targetSha: targetSha,
      ecosystemSha: ecosystemSha,
    );
  });

  tearDown(() {
    if (temp.existsSync()) temp.deleteSync(recursive: true);
  });

  Future<String> buildBundle() =>
      const TopiaForgeReleaseHandoff().buildPlatformBundle(
        repositoryRoot: root,
        version: version,
        targetSha: targetSha,
        platform: 'windows-x64',
        archivePath: p.join(temp.path, 'TopiaForge-windows-x64.zip'),
        canonicalEcosystemSha256: ecosystemSha,
        evidenceSha256: releaseQaEvidenceFor(
          temp,
          'windows-x64',
          ecosystemSha: ecosystemSha,
        ),
        qaPath: releaseQaPath(temp, 'windows-x64'),
        outputPath: p.join(
          temp.path,
          releasePlatformBundleFileName('windows-x64'),
        ),
      );

  for (final mismatch in ['policy version', 'catalog tag']) {
    test('handoff rejects a mismatched $mismatch', () async {
      // Establish that every other input is valid before changing one identity.
      final output = File(await buildBundle());
      final original = output.readAsBytesSync();
      if (mismatch == 'policy version') {
        _editJson(File(p.join(root, 'release', 'release-policy.json')), (json) {
          (json['versioning'] as Map)['productVersion'] = '$version+fixture';
        });
      } else {
        _editJson(File(p.join(root, 'release', 'catalog.json')), (json) {
          final release = (json['releases'] as List).cast<Map>().singleWhere(
            (entry) => entry['version'] == version,
          );
          release['tag'] = 'v$version+fixture';
        });
      }
      await expectLater(
        buildBundle(),
        throwsA(
          isA<StateError>().having(
            (error) => error.message,
            'message',
            'Handoff version must exactly match release policy and catalog.',
          ),
        ),
      );
      expect(output.readAsBytesSync(), original);
    });
  }
}

void _editJson(File file, void Function(Map<String, Object?>) edit) {
  final json = (jsonDecode(file.readAsStringSync()) as Map)
      .cast<String, Object?>();
  edit(json);
  file.writeAsStringSync('${jsonEncode(json)}\n');
}
