import 'dart:convert';
import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/release_policy.dart';
import 'package:topiaforge/src/release_spdx_metadata.dart';
import 'package:topiaforge/src/release_spdx_third_party.dart';

/// `verifyReleaseSpdxSbom` is what release metadata verification runs on the
/// published SBOM bytes. Each case alters one fact a generated SBOM states and
/// requires verification to reject it.
void main() {
  late TopiaForgeReleaseCatalogEntry release;
  late ReleaseSpdxThirdPartyInventory thirdParty;
  late Map<String, Object?> sbom;

  setUpAll(() {
    final root = _repositoryRoot();
    final policy = TopiaForgeReleasePolicy.load(root);
    release = TopiaForgeReleaseCatalog.load(root).release('0.1.0-rc.1');
    thirdParty = ReleaseSpdxThirdPartyInventory.load(
      repositoryRoot: root,
      policy: policy,
      release: release,
    );
    sbom = buildReleaseSpdxSbom(
      policy: policy,
      release: release,
      targetSha: 'a' * 40,
      artifacts: [
        for (final name in release.artifacts.toList()..sort())
          {'name': name, 'sha256': 'b' * 64},
      ],
      thirdParty: thirdParty,
    );
  });

  void rejects(String reason, void Function(_Sbom sbom) change) {
    final copy = _Sbom(jsonDecode(jsonEncode(sbom)) as Map<String, Object?>);
    change(copy);
    expect(
      () => verifyReleaseSpdxSbom(copy.json, release, thirdParty),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          contains(reason),
        ),
      ),
    );
  }

  test('accepts the SBOM generated from the same inventory', () {
    verifyReleaseSpdxSbom(
      jsonDecode(jsonEncode(sbom)) as Map<String, Object?>,
      release,
      thirdParty,
    );
  });

  test('rejects a package outside the catalog and third-party inventory', () {
    rejects('unknown package SPDXRef-Package-ThirdParty-pub-left-pad', (sbom) {
      sbom.packages.add({
        ...sbom.package('cross_file'),
        'name': 'left_pad',
        'SPDXID': 'SPDXRef-Package-ThirdParty-pub-left-pad-1.0.0',
        'versionInfo': '1.0.0',
      });
      sbom.relationships.add({
        'spdxElementId': 'SPDXRef-Package-launcher',
        'relationshipType': 'CONTAINS',
        'relatedSpdxElement': 'SPDXRef-Package-ThirdParty-pub-left-pad-1.0.0',
      });
    });
  });

  test('rejects an SBOM missing a declared third-party package', () {
    rejects('missing the package SPDXRef-Package-ThirdParty-Mono.Cecil', (
      sbom,
    ) {
      final id = sbom.package('Mono.Cecil')['SPDXID'];
      sbom.packages.removeWhere((entry) => entry['SPDXID'] == id);
      sbom.relationships.removeWhere(
        (entry) => entry['relatedSpdxElement'] == id,
      );
    });
  });

  test('rejects a missing CONTAINS relationship', () {
    rejects('does not record that SPDXRef-Package-bepInEx contains '
        'UnityDoorstop', (sbom) {
      final id = sbom.package('UnityDoorstop')['SPDXID'];
      sbom.relationships.removeWhere(
        (entry) =>
            entry['spdxElementId'] == 'SPDXRef-Package-bepInEx' &&
            entry['relatedSpdxElement'] == id,
      );
    });
  });

  test('rejects containment by a component that does not ship it', () {
    rejects('which does not ship it', (sbom) {
      sbom.relationships.add({
        'spdxElementId': 'SPDXRef-Package-TopiaForge',
        'relationshipType': 'CONTAINS',
        'relatedSpdxElement': sbom.package('UnityDoorstop')['SPDXID'],
      });
    });
  });

  test('rejects the LGPL component declared MIT', () {
    rejects(
      'misstates licenseDeclared for the third-party package UnityDoorstop',
      (sbom) => sbom.package('UnityDoorstop')['licenseDeclared'] = 'MIT',
    );
  });

  test('rejects a third-party package concluded under the project licence', () {
    rejects(
      'misstates licenseConcluded for the third-party package Mono.Cecil',
      (sbom) =>
          sbom.package('Mono.Cecil')['licenseConcluded'] = 'AGPL-3.0-or-later',
    );
  });

  test('rejects a moved third-party version', () {
    rejects(
      'names or versions the package '
      'SPDXRef-Package-ThirdParty-UnityDoorstop-4.5.0 differently',
      (sbom) => sbom.package('UnityDoorstop')['versionInfo'] = '4.6.0',
    );
  });

  test('rejects BepInEx concluded as wholly MIT', () {
    rejects(
      'misstates the terms of the vendored bepInEx',
      (sbom) => sbom.package('bepInEx')['licenseConcluded'] = 'MIT',
    );
  });

  test('rejects a mixed owned component concluding the project licence', () {
    rejects(
      'concludes one license or copyright for SPDXRef-Package-launcher',
      (sbom) =>
          sbom.package('launcher')['licenseConcluded'] = 'AGPL-3.0-or-later',
    );
  });

  test('rejects a licence reference the document does not define', () {
    rejects(
      'license references differ from its extracted licensing information',
      (sbom) => sbom.json.remove('hasExtractedLicensingInfos'),
    );
  });

  test('rejects an invalid SPDX licence expression', () {
    rejects(
      'invalid licenseDeclared for SPDXRef-Package-sdk',
      (sbom) => sbom.package('sdk')['licenseDeclared'] = 'Not-A-License',
    );
  });

  test('rejects an invalid or duplicate SPDXID', () {
    rejects('missing, invalid, or duplicate SPDXID', (sbom) {
      sbom.package('cross_file')['SPDXID'] = 'SPDXRef-Package-cross_file';
    });
    rejects('missing, invalid, or duplicate SPDXID', (sbom) {
      sbom.package('cross_file')['SPDXID'] = sbom.package('nested')['SPDXID'];
    });
  });
}

/// Mutable view of a decoded SBOM for one test case.
final class _Sbom {
  _Sbom(this.json);

  final Map<String, Object?> json;

  List<Map<String, Object?>> get packages =>
      (json['packages'] as List).cast<Map<String, Object?>>();

  List<Map<String, Object?>> get relationships =>
      (json['relationships'] as List).cast<Map<String, Object?>>();

  /// The package with [name]; every name used here occurs once.
  Map<String, Object?> package(String name) =>
      packages.singleWhere((entry) => entry['name'] == name);
}

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
