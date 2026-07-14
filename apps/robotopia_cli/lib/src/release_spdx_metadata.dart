import 'dart:convert';

import 'package:crypto/crypto.dart';

import 'release_policy.dart';

void verifyReleaseSpdxSbom(
  Map<String, Object?> sbom,
  RobotopiaReleaseCatalogEntry release,
) {
  final packages = (sbom['packages'] as List?)?.whereType<Map>().toList();
  final files = (sbom['files'] as List?)?.whereType<Map>().toList();
  final relationships = (sbom['relationships'] as List?)
      ?.whereType<Map>()
      .toList();
  if (packages == null || files == null || relationships == null) {
    throw StateError('SPDX SBOM is missing packages, files, or relationships.');
  }
  final expectedPackages = {
    'QuantumWorks',
    ...release.components.keys,
    ...release.vpmPackages.keys,
    ...release.mods.keys,
    'BepInEx',
  };
  final packageNames = packages
      .map((entry) => entry['name'])
      .whereType<String>()
      .toSet();
  if (!_sameSet(packageNames, expectedPackages)) {
    throw StateError('SPDX SBOM package inventory differs from the catalog.');
  }
  final fileByName = <String, Map>{
    for (final entry in files) entry['fileName'].toString(): entry,
  };
  final expectedFiles = release.artifacts.map((name) => './$name').toSet();
  if (!_sameSet(fileByName.keys.toSet(), expectedFiles)) {
    throw StateError('SPDX SBOM file inventory differs from release assets.');
  }
  final ids = <String>{};
  for (final entry in [...packages, ...files]) {
    final id = entry['SPDXID'];
    if (id is! String || !ids.add(id)) {
      throw StateError('SPDX SBOM contains a missing or duplicate SPDXID.');
    }
  }
  final relationshipsSet = relationships
      .map(
        (entry) =>
            '${entry['spdxElementId']}|${entry['relationshipType']}|${entry['relatedSpdxElement']}',
      )
      .toSet();
  for (final entry in packages.skip(1)) {
    if (!relationshipsSet.contains(
      'SPDXRef-Package-QuantumWorks|CONTAINS|${entry['SPDXID']}',
    )) {
      throw StateError('SPDX SBOM does not relate every nested package.');
    }
  }
  for (final entry in files) {
    final checksums = (entry['checksums'] as List?)?.whereType<Map>().toList();
    if (checksums?.length != 1 ||
        checksums!.single['algorithm'] != 'SHA256' ||
        !RegExp(
          r'^[0-9a-f]{64}$',
        ).hasMatch(checksums.single['checksumValue'].toString()) ||
        !relationshipsSet.contains(
          'SPDXRef-Package-QuantumWorks|CONTAINS|${entry['SPDXID']}',
        )) {
      throw StateError(
        'SPDX SBOM file hashes or containment relationships are invalid.',
      );
    }
  }
}

Map<String, Object?> buildReleaseSpdxSbom({
  required RobotopiaReleasePolicy policy,
  required RobotopiaReleaseCatalogEntry release,
  required String targetSha,
  required List<Map<String, Object?>> artifacts,
}) {
  const rootId = 'SPDXRef-Package-QuantumWorks';
  final packageEntries = <MapEntry<String, String>>[
    ...release.components.entries,
    ...release.vpmPackages.entries,
    ...release.mods.entries,
    MapEntry('BepInEx', policy.bepInExVersion),
  ]..sort((left, right) => left.key.compareTo(right.key));
  final packages = <Map<String, Object?>>[
    _spdxPackage(
      name: 'QuantumWorks',
      version: release.version,
      spdxId: rootId,
      license: policy.hasApprovedLicense
          ? policy.licenseExpression
          : 'NOASSERTION',
    ),
    for (final entry in packageEntries)
      _spdxPackage(
        name: entry.key,
        version: entry.value,
        spdxId: _spdxId('Package', entry.key),
        license: entry.key == 'BepInEx' ? 'MIT' : 'NOASSERTION',
      ),
  ];
  final files = <Map<String, Object?>>[
    for (final artifact in artifacts)
      {
        'fileName': './${artifact['name']}',
        'SPDXID': _spdxId('File', artifact['name'].toString()),
        'checksums': [
          {'algorithm': 'SHA256', 'checksumValue': artifact['sha256']},
        ],
        'licenseConcluded': 'NOASSERTION',
        'copyrightText': 'NOASSERTION',
      },
  ];
  final relationships = <Map<String, Object?>>[
    for (final entry in packageEntries)
      {
        'spdxElementId': rootId,
        'relationshipType': 'CONTAINS',
        'relatedSpdxElement': _spdxId('Package', entry.key),
      },
    for (final artifact in artifacts)
      {
        'spdxElementId': rootId,
        'relationshipType': 'CONTAINS',
        'relatedSpdxElement': _spdxId('File', artifact['name'].toString()),
      },
    for (final entry in release.mods.entries)
      {
        'spdxElementId': _spdxId('Package', entry.key),
        'relationshipType': 'CONTAINS',
        'relatedSpdxElement': _spdxId(
          'File',
          '${entry.key}-${entry.value}.robotopiamod',
        ),
      },
  ]..sort((left, right) => jsonEncode(left).compareTo(jsonEncode(right)));
  final namespaceSeed = sha256
      .convert(utf8.encode('${release.version}:$targetSha'))
      .toString();
  return {
    r'$schema':
        'https://raw.githubusercontent.com/furroxide/quantum-works/main/schemas/robotopia.release-spdx.schema.json',
    'spdxVersion': 'SPDX-2.3',
    'dataLicense': 'CC0-1.0',
    'SPDXID': 'SPDXRef-DOCUMENT',
    'name': 'QuantumWorks-${release.version}',
    'documentNamespace':
        'https://furroxide.github.io/quantum-works/spdx/${release.version}/$namespaceSeed',
    'creationInfo': {
      'created': '1970-01-01T00:00:00Z',
      'creators': ['Tool: Robotopia CLI-${release.components['cli']}'],
      'comment':
          'Reproducible SBOM for target $targetSha and Robotopia game build ${policy.gameBuildId}.',
    },
    'documentDescribes': [rootId],
    'packages': packages,
    'files': files,
    'relationships': relationships,
  };
}

Map<String, Object?> _spdxPackage({
  required String name,
  required String version,
  required String spdxId,
  required String license,
}) => {
  'name': name,
  'SPDXID': spdxId,
  'versionInfo': version,
  'downloadLocation': 'NOASSERTION',
  'filesAnalyzed': false,
  'licenseConcluded': 'NOASSERTION',
  'licenseDeclared': license,
  'copyrightText': 'NOASSERTION',
};

String _spdxId(String kind, String value) {
  final safe = value.replaceAll(RegExp(r'[^A-Za-z0-9.-]'), '-');
  return 'SPDXRef-$kind-$safe';
}

bool _sameSet(Set<String> left, Set<String> right) =>
    left.length == right.length && left.containsAll(right);
