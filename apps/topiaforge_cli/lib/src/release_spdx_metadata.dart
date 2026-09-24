import 'dart:convert';

import 'package:crypto/crypto.dart';

import 'release_policy.dart';

void verifyReleaseSpdxSbom(
  Map<String, Object?> sbom,
  TopiaForgeReleaseCatalogEntry release,
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
    'TopiaForge',
    ...release.components.keys,
    ...release.vpmPackages.keys,
    ...release.mods.keys,
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
      'SPDXRef-Package-TopiaForge|CONTAINS|${entry['SPDXID']}',
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
          'SPDXRef-Package-TopiaForge|CONTAINS|${entry['SPDXID']}',
        )) {
      throw StateError(
        'SPDX SBOM file hashes or containment relationships are invalid.',
      );
    }
  }
}

Map<String, Object?> buildReleaseSpdxSbom({
  required TopiaForgeReleasePolicy policy,
  required TopiaForgeReleaseCatalogEntry release,
  required String targetSha,
  required List<Map<String, Object?>> artifacts,
}) {
  const rootId = 'SPDXRef-Package-TopiaForge';
  final packageEntries = <MapEntry<String, String>>[
    ...release.components.entries,
    ...release.vpmPackages.entries,
    ...release.mods.entries,
  ]..sort((left, right) => left.key.compareTo(right.key));
  // First-party surfaces carry the approved project license. Vendored
  // third-party components declare their own upstream terms and keep
  // NOASSERTION for the conclusion and the copyright: the project neither
  // concludes nor holds copyright on their behalf.
  final ownedLicense = policy.hasApprovedLicense
      ? policy.licenseExpression
      : 'NOASSERTION';
  final ownedCopyright = policy.hasApprovedLicense
      ? _ownedCopyrightText
      : 'NOASSERTION';
  Map<String, Object?> packageFor(MapEntry<String, String> entry) {
    final vendored = _vendoredComponentLicenses[entry.key];
    return _spdxPackage(
      name: entry.key,
      version: entry.value,
      spdxId: _spdxId('Package', entry.key),
      license: vendored ?? ownedLicense,
      licenseConcluded: vendored == null ? ownedLicense : 'NOASSERTION',
      copyrightText: vendored == null ? ownedCopyright : 'NOASSERTION',
    );
  }

  // Mod packages are first-party throughout. Platform archives bundle the
  // vendored loader payload and third-party fonts alongside owned code, so the
  // project concludes nothing about such an archive as a whole.
  Map<String, Object?> fileFor(Map<String, Object?> artifact) {
    final name = artifact['name'].toString();
    final owned = name.endsWith(_ownedArtifactExtension);
    return {
      'fileName': './$name',
      'SPDXID': _spdxId('File', name),
      'checksums': [
        {'algorithm': 'SHA256', 'checksumValue': artifact['sha256']},
      ],
      'licenseConcluded': owned ? ownedLicense : 'NOASSERTION',
      'copyrightText': owned ? ownedCopyright : 'NOASSERTION',
    };
  }

  final packages = <Map<String, Object?>>[
    _spdxPackage(
      name: 'TopiaForge',
      version: release.version,
      spdxId: rootId,
      license: ownedLicense,
      licenseConcluded: ownedLicense,
      copyrightText: ownedCopyright,
    ),
    for (final entry in packageEntries) packageFor(entry),
  ];
  final files = <Map<String, Object?>>[
    for (final artifact in artifacts) fileFor(artifact),
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
          '${entry.key}-${entry.value}.topiaforgemod',
        ),
      },
  ]..sort((left, right) => jsonEncode(left).compareTo(jsonEncode(right)));
  final namespaceSeed = sha256
      .convert(utf8.encode('${release.version}:$targetSha'))
      .toString();
  return {
    r'$schema':
        'https://raw.githubusercontent.com/furroxide/TopiaForge/main/schemas/topiaforge.release-spdx.schema.json',
    'spdxVersion': 'SPDX-2.3',
    'dataLicense': 'CC0-1.0',
    'SPDXID': 'SPDXRef-DOCUMENT',
    'name': 'TopiaForge-${release.version}',
    'documentNamespace':
        'https://furroxide.github.io/TopiaForge/spdx/${release.version}/$namespaceSeed',
    'creationInfo': {
      'created': '1970-01-01T00:00:00Z',
      'creators': ['Tool: TopiaForge CLI-${release.components['cli']}'],
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
  String licenseConcluded = 'NOASSERTION',
  String copyrightText = 'NOASSERTION',
}) => {
  'name': name,
  'SPDXID': spdxId,
  'versionInfo': version,
  'downloadLocation': 'NOASSERTION',
  'filesAnalyzed': false,
  'licenseConcluded': licenseConcluded,
  'licenseDeclared': license,
  'copyrightText': copyrightText,
};

/// Copyright line recorded for TopiaForge-owned SPDX packages and files.
const _ownedCopyrightText = 'Copyright (C) 2026 furroxide';

/// Release-catalog component keys that name vendored third-party software,
/// mapped to the terms their own authors declare. Keys must match
/// `release/catalog.json` exactly; anything absent here is treated as owned.
const _vendoredComponentLicenses = <String, String>{'bepInEx': 'MIT'};

/// Release artifacts built solely from first-party sources. Everything else is
/// a platform archive that also redistributes third-party payload.
const _ownedArtifactExtension = '.topiaforgemod';

String _spdxId(String kind, String value) {
  final safe = value.replaceAll(RegExp(r'[^A-Za-z0-9.-]'), '-');
  return 'SPDXRef-$kind-$safe';
}

bool _sameSet(Set<String> left, Set<String> right) =>
    left.length == right.length && left.containsAll(right);
