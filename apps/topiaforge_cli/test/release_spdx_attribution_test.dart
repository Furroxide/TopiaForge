import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/release_policy.dart';
import 'package:topiaforge/src/release_spdx_metadata.dart';

/// Regression guard for who the release SBOM attributes payload to.
///
/// The SBOM is hashed into `SHA256SUMS`, is an attestation subject, and is
/// published as an immutable release asset, while `verifyReleaseSpdxSbom`
/// deliberately checks inventory, identifiers and hashes rather than licence
/// fields. Nothing else in the suite would notice the project asserting its own
/// terms and copyright over vendored third-party code.
void main() {
  const ownedCopyright = 'Copyright (C) 2026 furroxide';
  const targetSha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';

  late String root;
  late TopiaForgeReleasePolicy policy;
  late TopiaForgeReleaseCatalogEntry release;
  late Map<String, Object?> sbom;

  setUp(() {
    root = _repositoryRoot();
    policy = TopiaForgeReleasePolicy.load(root);
    release = TopiaForgeReleaseCatalog.load(root).release('0.1.0-rc.1');
    sbom = buildReleaseSpdxSbom(
      policy: policy,
      release: release,
      targetSha: targetSha,
      artifacts: [
        for (final name in release.artifacts)
          {'name': name, 'sha256': 'a' * 64},
      ],
    );
  });

  test('the recorded copyright line matches the shipped LICENSE', () {
    expect(
      File(p.join(root, 'LICENSE')).readAsStringSync(),
      contains(ownedCopyright),
    );
  });

  test('each catalog component yields exactly one SPDX package', () {
    final packages = _packages(sbom);
    final names = packages.map((entry) => entry['name'] as String).toList();
    expect(names.toSet(), hasLength(names.length));
    expect(
      names.where((name) => name.toLowerCase() == 'bepinex'),
      hasLength(1),
      reason: 'the vendored loader must not be described twice',
    );
  });

  test('vendored components keep their own terms and copyright', () {
    final bepInEx = _byName(sbom)['bepInEx'];
    expect(bepInEx, isNotNull, reason: 'catalog component must be described');
    expect(bepInEx!['licenseDeclared'], 'MIT');
    expect(bepInEx['licenseConcluded'], 'NOASSERTION');
    expect(bepInEx['copyrightText'], 'NOASSERTION');
  });

  test('owned components carry the approved expression and copyright', () {
    final byName = _byName(sbom);
    for (final name in const ['TopiaForge', 'cli', 'launcher', 'sdk']) {
      final entry = byName[name];
      expect(entry, isNotNull, reason: name);
      expect(entry!['licenseDeclared'], policy.licenseExpression, reason: name);
      expect(entry['licenseConcluded'], policy.licenseExpression, reason: name);
      expect(entry['copyrightText'], ownedCopyright, reason: name);
    }
  });

  test('only first-party artifacts are concluded as owned', () {
    for (final entry in _files(sbom)) {
      final fileName = entry['fileName'] as String;
      if (fileName.endsWith('.topiaforgemod')) {
        expect(
          entry['licenseConcluded'],
          policy.licenseExpression,
          reason: fileName,
        );
        expect(entry['copyrightText'], ownedCopyright, reason: fileName);
      } else {
        // Platform archives redistribute the vendored loader payload and
        // third-party fonts, so the project concludes nothing about them.
        expect(entry['licenseConcluded'], 'NOASSERTION', reason: fileName);
        expect(entry['copyrightText'], 'NOASSERTION', reason: fileName);
      }
    }
  });
}

List<Map<String, Object?>> _packages(Map<String, Object?> sbom) =>
    (sbom['packages'] as List).cast<Map<String, Object?>>();

List<Map<String, Object?>> _files(Map<String, Object?> sbom) =>
    (sbom['files'] as List).cast<Map<String, Object?>>();

Map<String, Map<String, Object?>> _byName(Map<String, Object?> sbom) => {
  for (final entry in _packages(sbom)) entry['name'] as String: entry,
};

String _repositoryRoot() {
  var directory = Directory.current.absolute;
  while (!File(p.join(directory.path, 'TopiaForge.slnx')).existsSync()) {
    if (directory.parent.path == directory.path) {
      throw StateError('Repository root not found.');
    }
    directory = directory.parent;
  }
  return directory.path;
}
