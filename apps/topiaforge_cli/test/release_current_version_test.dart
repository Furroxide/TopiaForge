import 'dart:convert';
import 'dart:io';

import 'package:json_schema/json_schema.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/release_policy.dart';

void main() {
  var directory = Directory.current.absolute;
  while (!File(p.join(directory.path, 'TopiaForge.slnx')).existsSync()) {
    directory = directory.parent;
  }
  final root = directory.path;
  Map<String, Object?> read(String path) =>
      (jsonDecode(File(p.join(root, path)).readAsStringSync()) as Map)
          .cast<String, Object?>();

  test(
    'current release versions align while publication stays blocked',
    () async {
      final policy = TopiaForgeReleasePolicy.load(root);
      final catalog = TopiaForgeReleaseCatalog.load(root);
      final release = catalog.release('0.2.0');
      expect(policy.productVersion, '0.2.0');
      expect(release.tag, 'v0.2.0');
      expect(release.prerelease, isFalse);
      expect(release.status, 'blocked');
      expect(
        TopiaForgeRuntimeVersions.loaderVersion,
        release.components['loader'],
      );
      expect(TopiaForgeRuntimeVersions.sdkVersion, release.components['sdk']);
      expect(
        JsonSchema.create(
          read('schemas/topiaforge.release-catalog.schema.json'),
        ).validate(read('release/catalog.json')).isValid,
        isTrue,
      );
      final issues = await const ReleasePolicyValidator().validate(
        policy: policy,
        release: release,
        verifyArchiveHashes: false,
      );
      expect(
        issues,
        unorderedEquals([
          'Release 0.2.0 is blocked; set its manually reviewed catalog status to ready.',
          'Release 0.2.0 must target only Windows x64 packages.',
          'An unsigned Windows distribution is only allowed for a prerelease.',
        ]),
      );
    },
  );

  test('historical and independent versions retain their identity', () {
    final catalog = TopiaForgeReleaseCatalog.load(root);
    final historical = catalog.release('0.1.0-rc.1');
    final current = catalog.release('0.2.0');
    expect(historical.tag, 'v0.1.0-rc.1');
    expect(historical.prerelease, isTrue);
    expect(historical.components['sdk'], '0.1.0-rc.1');
    expect(current.vpmPackages, historical.vpmPackages);
    for (final component in [
      'gameCompatExtractor',
      'gameCompatSurface',
      'bepInEx',
    ]) {
      expect(current.components[component], historical.components[component]);
    }
  });
}
