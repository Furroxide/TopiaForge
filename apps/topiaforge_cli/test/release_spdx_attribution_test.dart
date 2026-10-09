import 'dart:convert';
import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/release_policy.dart';
import 'package:topiaforge/src/release_spdx_metadata.dart';
import 'package:topiaforge/src/release_spdx_third_party.dart';

/// Regression guard for who the release SBOM attributes payload to.
///
/// The SBOM is hashed into `SHA256SUMS`, is an attestation subject, and is
/// published as an immutable release asset. These tests pin the licence fields
/// themselves, so neither the project asserting its own terms and copyright
/// over vendored third-party code nor a third-party package losing its own
/// upstream terms can go unnoticed.
void main() {
  const ownedCopyright = 'Copyright (C) 2026 furroxide';
  const targetSha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';

  late String root;
  late TopiaForgeReleasePolicy policy;
  late TopiaForgeReleaseCatalogEntry release;
  late ReleaseSpdxThirdPartyInventory thirdParty;
  late Map<String, Object?> sbom;

  Map<String, Object?> build() => buildReleaseSpdxSbom(
    policy: policy,
    release: release,
    targetSha: targetSha,
    artifacts: [
      for (final name in release.artifacts.toList()..sort())
        {'name': name, 'sha256': 'a' * 64},
    ],
    thirdParty: thirdParty,
  );

  setUp(() {
    root = _repositoryRoot();
    policy = TopiaForgeReleasePolicy.load(root);
    release = TopiaForgeReleaseCatalog.load(root).release('0.1.0-rc.1');
    thirdParty = ReleaseSpdxThirdPartyInventory.load(
      repositoryRoot: root,
      policy: policy,
      release: release,
    );
    sbom = build();
  });

  test('the recorded copyright line matches the shipped LICENSE', () {
    expect(
      File(p.join(root, 'LICENSE')).readAsStringSync(),
      contains(ownedCopyright),
    );
  });

  test('the same inputs produce byte-identical SBOMs that verify', () {
    expect(jsonEncode(build()), jsonEncode(sbom));
    verifyReleaseSpdxSbom(
      jsonDecode(jsonEncode(sbom)) as Map<String, Object?>,
      release,
      thirdParty,
    );
  });

  test('each catalog component yields exactly one SPDX package', () {
    final packages = _packages(sbom);
    final ids = packages.map((entry) => entry['SPDXID'] as String).toList();
    expect(ids.toSet(), hasLength(ids.length));
    final releases = packages
        .map((entry) => '${entry['name']}@${entry['versionInfo']}')
        .toList();
    expect(releases.toSet(), hasLength(releases.length));
    final catalogNames = packages
        .where((entry) => !_isThirdParty(entry))
        .map((entry) => entry['name'] as String)
        .toList();
    expect(catalogNames.toSet(), {
      'TopiaForge',
      ...release.components.keys,
      ...release.vpmPackages.keys,
      ...release.mods.keys,
    });
    expect(catalogNames.toSet(), hasLength(catalogNames.length));
    expect(
      packages.where((entry) => '${entry['name']}'.toLowerCase() == 'bepinex'),
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

  test('the LGPL loader stub is its own package inside BepInEx', () {
    final doorstop = _byName(sbom)['UnityDoorstop']!;
    expect(doorstop['versionInfo'], '4.5.0');
    expect(doorstop['licenseDeclared'], 'LGPL-2.1-only');
    expect(doorstop['licenseConcluded'], 'NOASSERTION');
    expect(doorstop['copyrightText'], 'NOASSERTION');
    expect(_containers(sbom, doorstop['SPDXID'] as String), {
      'SPDXRef-Package-bepInEx',
    });
    for (final entry in _packages(sbom)) {
      if ('${entry['licenseDeclared']}'.contains('MIT')) {
        expect(
          entry['name'],
          isNot('UnityDoorstop'),
          reason: 'LGPL code must never sit under an MIT declaration',
        );
      }
    }
  });

  test('every third-party package states only its own terms', () {
    final thirdPartyPackages = _packages(sbom).where(_isThirdParty).toList();
    expect(thirdPartyPackages, hasLength(thirdParty.packages.length));
    for (final entry in thirdPartyPackages) {
      final name = entry['name'];
      expect(entry['licenseConcluded'], 'NOASSERTION', reason: '$name');
      expect(entry['copyrightText'], 'NOASSERTION', reason: '$name');
      expect(
        entry['licenseDeclared'],
        isNot(policy.licenseExpression),
        reason: 'the project licence is never declared for $name',
      );
      expect(
        _containers(sbom, entry['SPDXID'] as String),
        isNotEmpty,
        reason: '$name must be contained by the component that ships it',
      );
    }
  });

  test('owned components that ship third-party material conclude nothing as a '
      'whole', () {
    final byName = _byName(sbom);
    for (final name in const [
      'TopiaForge',
      'cli',
      'gameCompatExtractor',
      'launcher',
      'launcherUi',
      'loader',
      'unityUi',
    ]) {
      final entry = byName[name];
      expect(entry, isNotNull, reason: name);
      expect(entry!['licenseDeclared'], policy.licenseExpression, reason: name);
      expect(entry['licenseConcluded'], 'NOASSERTION', reason: name);
      expect(entry['copyrightText'], 'NOASSERTION', reason: name);
    }
  });

  test('purely first-party packages carry the approved expression and '
      'copyright', () {
    final byName = _byName(sbom);
    final pure = [
      'gameCompatSurface',
      'launcherData',
      'launcherDomain',
      'loaderCore',
      'sdk',
      ...release.vpmPackages.keys,
      ...release.mods.keys,
    ];
    expect(pure, hasLength(20));
    for (final name in pure) {
      final entry = byName[name];
      expect(entry, isNotNull, reason: name);
      expect(entry!['licenseDeclared'], policy.licenseExpression, reason: name);
      expect(entry['licenseConcluded'], policy.licenseExpression, reason: name);
      expect(entry['copyrightText'], ownedCopyright, reason: name);
      expect(
        _contained(sbom, entry['SPDXID'] as String),
        everyElement(isNot(contains('ThirdParty'))),
        reason: '$name ships no third-party package',
      );
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

  test('a licence without an SPDX identifier is defined in the document', () {
    final tmp = _byName(sbom)['TextMesh Pro essential resources']!;
    expect(tmp['licenseDeclared'], 'LicenseRef-Unity-Companion-License');
    final extracted = (sbom['hasExtractedLicensingInfos'] as List)
        .cast<Map<String, Object?>>();
    expect(extracted.single['licenseId'], 'LicenseRef-Unity-Companion-License');
    expect(extracted.single['name'], 'Unity Companion License');
    expect(extracted.single['extractedText'], contains('THIRD_PARTY_NOTICES'));
  });
}

bool _isThirdParty(Map<String, Object?> entry) =>
    (entry['SPDXID'] as String).startsWith('SPDXRef-Package-ThirdParty-');

Set<String> _containers(Map<String, Object?> sbom, String id) => {
  for (final entry in (sbom['relationships'] as List).cast<Map>())
    if (entry['relatedSpdxElement'] == id &&
        entry['relationshipType'] == 'CONTAINS')
      entry['spdxElementId'] as String,
};

Set<String> _contained(Map<String, Object?> sbom, String id) => {
  for (final entry in (sbom['relationships'] as List).cast<Map>())
    if (entry['spdxElementId'] == id) entry['relatedSpdxElement'] as String,
};

List<Map<String, Object?>> _packages(Map<String, Object?> sbom) =>
    (sbom['packages'] as List).cast<Map<String, Object?>>();

List<Map<String, Object?>> _files(Map<String, Object?> sbom) =>
    (sbom['files'] as List).cast<Map<String, Object?>>();

/// Packages by name. Third-party names repeat across versions, so callers use
/// this only for names that occur once.
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
