import 'dart:convert';

import 'package:crypto/crypto.dart';

import 'release_policy.dart';
import 'release_spdx_third_party.dart';
import 'spdx_expression.dart';

/// Verifies an SBOM against the catalog and the third-party inventory the
/// candidate source declares: exactly those packages, the declared upstream
/// terms for every third-party package, a `CONTAINS` relationship from each
/// component that ships one, and no single concluded licence for a component
/// that mixes owned and third-party material.
void verifyReleaseSpdxSbom(
  Map<String, Object?> sbom,
  TopiaForgeReleaseCatalogEntry release,
  ReleaseSpdxThirdPartyInventory thirdParty,
) {
  final packages = _objects(sbom['packages']);
  final files = _objects(sbom['files']);
  final relationships = _objects(sbom['relationships']);
  if (packages == null || files == null || relationships == null) {
    throw StateError('SPDX SBOM is missing packages, files, or relationships.');
  }
  final ids = <String>{};
  for (final entry in [...packages, ...files]) {
    final id = entry['SPDXID'];
    if (id is! String || !_spdxIdPattern.hasMatch(id) || !ids.add(id)) {
      throw StateError(
        'SPDX SBOM contains a missing, invalid, or duplicate SPDXID.',
      );
    }
  }
  final packageById = {
    for (final entry in packages) entry['SPDXID'] as String: entry,
  };
  _verifyPackageInventory(packageById, release, thirdParty);
  _verifyPackageTerms(sbom, packageById, thirdParty);
  final relationshipKeys = {
    for (final entry in relationships)
      '${entry['spdxElementId']}|${entry['relationshipType']}|'
          '${entry['relatedSpdxElement']}',
  };
  _verifyContainment(relationshipKeys, relationships, release, thirdParty);
  final fileByName = <String, Map>{
    for (final entry in files) entry['fileName'].toString(): entry,
  };
  final expectedFiles = release.artifacts.map((name) => './$name').toSet();
  if (!_sameSet(fileByName.keys.toSet(), expectedFiles)) {
    throw StateError('SPDX SBOM file inventory differs from release assets.');
  }
  for (final entry in files) {
    final checksums = (entry['checksums'] as List?)?.whereType<Map>().toList();
    if (checksums?.length != 1 ||
        checksums!.single['algorithm'] != 'SHA256' ||
        !RegExp(
          r'^[0-9a-f]{64}$',
        ).hasMatch(checksums.single['checksumValue'].toString()) ||
        !relationshipKeys.contains('$_rootId|CONTAINS|${entry['SPDXID']}')) {
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
  required ReleaseSpdxThirdPartyInventory thirdParty,
}) {
  final packageEntries = _catalogEntries(release);
  // First-party surfaces carry the approved project license. Vendored
  // third-party components declare their own upstream terms and keep
  // NOASSERTION for the conclusion and the copyright: the project neither
  // concludes nor holds copyright on their behalf.
  final ownedLicense = policy.hasApprovedLicense
      ? policy.licenseExpression
      : _noAssertion;
  final ownedCopyright = policy.hasApprovedLicense
      ? _ownedCopyrightText
      : _noAssertion;
  // A TopiaForge component that ships third-party packages still declares the
  // project license for its own code, but concludes nothing about the package
  // as a whole, for the same reason fileFor() concludes nothing about a
  // platform archive. Each third-party package states its own terms below.
  // Only a purely first-party component concludes the project license.
  final mixed = thirdParty.containers;
  Map<String, Object?> packageFor(MapEntry<String, String> entry) {
    final vendored = releaseVendoredComponents[entry.key];
    if (vendored != null) {
      return _spdxPackage(
        name: entry.key,
        version: entry.value,
        spdxId: releaseSpdxId('Package', entry.key),
        license: vendored.license,
        downloadLocation: vendored.downloadLocation,
      );
    }
    final owned = !mixed.contains(entry.key);
    return _spdxPackage(
      name: entry.key,
      version: entry.value,
      spdxId: releaseSpdxId('Package', entry.key),
      license: ownedLicense,
      licenseConcluded: owned ? ownedLicense : _noAssertion,
      copyrightText: owned ? ownedCopyright : _noAssertion,
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
      'SPDXID': releaseSpdxId('File', name),
      'checksums': [
        {'algorithm': 'SHA256', 'checksumValue': artifact['sha256']},
      ],
      'licenseConcluded': owned ? ownedLicense : _noAssertion,
      'copyrightText': owned ? ownedCopyright : _noAssertion,
    };
  }

  // The release root contains every package below, third-party ones included.
  final ownsEverything = thirdParty.packages.isEmpty;
  final packages = <Map<String, Object?>>[
    _spdxPackage(
      name: 'TopiaForge',
      version: release.version,
      spdxId: _rootId,
      license: ownedLicense,
      licenseConcluded: ownsEverything ? ownedLicense : _noAssertion,
      copyrightText: ownsEverything ? ownedCopyright : _noAssertion,
    ),
    for (final entry in packageEntries) packageFor(entry),
    for (final entry in thirdParty.packages) entry.toSpdx(),
  ];
  final files = <Map<String, Object?>>[
    for (final artifact in artifacts) fileFor(artifact),
  ];
  final relationships = <Map<String, Object?>>[
    for (final entry in packageEntries)
      _contains(_rootId, releaseSpdxId('Package', entry.key)),
    for (final artifact in artifacts)
      _contains(_rootId, releaseSpdxId('File', artifact['name'].toString())),
    for (final entry in release.mods.entries)
      _contains(
        releaseSpdxId('Package', entry.key),
        releaseSpdxId('File', '${entry.key}-${entry.value}.topiaforgemod'),
      ),
    for (final entry in thirdParty.packages)
      for (final component in entry.shippedBy)
        _contains(releaseSpdxId('Package', component), entry.spdxId),
  ]..sort((left, right) => jsonEncode(left).compareTo(jsonEncode(right)));
  final extracted = thirdParty.extractedLicensingInfos;
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
    'documentDescribes': [_rootId],
    'packages': packages,
    'files': files,
    'relationships': relationships,
    if (extracted.isNotEmpty) 'hasExtractedLicensingInfos': extracted,
  };
}

void _verifyPackageInventory(
  Map<String, Map> packageById,
  TopiaForgeReleaseCatalogEntry release,
  ReleaseSpdxThirdPartyInventory thirdParty,
) {
  final expected = <String, (String, String?)>{
    _rootId: ('TopiaForge', release.version),
    for (final entry in _catalogEntries(release))
      releaseSpdxId('Package', entry.key): (entry.key, entry.value),
    for (final entry in thirdParty.packages)
      entry.spdxId: (entry.name, entry.version),
  };
  for (final id in packageById.keys) {
    if (!expected.containsKey(id)) {
      throw StateError(
        'SPDX SBOM declares the unknown package $id, which neither the '
        'catalog nor the third-party inventory contains.',
      );
    }
  }
  for (final entry in expected.entries) {
    final actual = packageById[entry.key];
    if (actual == null) {
      throw StateError('SPDX SBOM is missing the package ${entry.key}.');
    }
    if (actual['name'] != entry.value.$1 ||
        actual['versionInfo'] != entry.value.$2) {
      throw StateError(
        'SPDX SBOM names or versions the package ${entry.key} differently.',
      );
    }
  }
}

void _verifyPackageTerms(
  Map<String, Object?> sbom,
  Map<String, Map> packageById,
  ReleaseSpdxThirdPartyInventory thirdParty,
) {
  for (final entry in thirdParty.packages) {
    final actual = packageById[entry.spdxId]!;
    final expected = entry.toSpdx();
    for (final field in {...expected.keys, ...actual.keys}) {
      if (jsonEncode(actual[field]) != jsonEncode(expected[field])) {
        throw StateError(
          'SPDX SBOM misstates $field for the third-party package '
          '${entry.name}.',
        );
      }
    }
  }
  for (final vendored in releaseVendoredComponents.entries) {
    final actual = packageById[releaseSpdxId('Package', vendored.key)];
    if (actual != null &&
        (actual['licenseDeclared'] != vendored.value.license ||
            actual['downloadLocation'] != vendored.value.downloadLocation ||
            actual['licenseConcluded'] != _noAssertion ||
            actual['copyrightText'] != _noAssertion)) {
      throw StateError(
        'SPDX SBOM misstates the terms of the vendored ${vendored.key}.',
      );
    }
  }
  for (final id in [
    if (thirdParty.packages.isNotEmpty) _rootId,
    for (final component in thirdParty.containers)
      releaseSpdxId('Package', component),
  ]) {
    final actual = packageById[id]!;
    if (actual['licenseConcluded'] != _noAssertion ||
        actual['copyrightText'] != _noAssertion) {
      throw StateError(
        'SPDX SBOM concludes one license or copyright for $id, which also '
        'ships third-party packages.',
      );
    }
  }
  final references = <String>{};
  for (final entry in packageById.values) {
    for (final field in const ['licenseDeclared', 'licenseConcluded']) {
      final value = entry[field];
      if (value is! String ||
          (value != _noAssertion &&
              SpdxExpressionValidator.validate(value) != null)) {
        throw StateError(
          'SPDX SBOM records an invalid $field for ${entry['SPDXID']}.',
        );
      }
      references.addAll(
        _licenseReference.allMatches(value).map((match) => match[0]!),
      );
    }
  }
  final extracted = thirdParty.extractedLicensingInfos;
  if (jsonEncode(sbom['hasExtractedLicensingInfos'] ?? const []) !=
          jsonEncode(extracted) ||
      !_sameSet(references, {
        for (final entry in extracted) entry['licenseId'] as String,
      })) {
    throw StateError(
      'SPDX SBOM license references differ from its extracted licensing '
      'information.',
    );
  }
}

void _verifyContainment(
  Set<String> relationshipKeys,
  List<Map> relationships,
  TopiaForgeReleaseCatalogEntry release,
  ReleaseSpdxThirdPartyInventory thirdParty,
) {
  for (final entry in _catalogEntries(release)) {
    if (!relationshipKeys.contains(
      '$_rootId|CONTAINS|${releaseSpdxId('Package', entry.key)}',
    )) {
      throw StateError('SPDX SBOM does not relate every nested package.');
    }
  }
  final shippersById = {
    for (final entry in thirdParty.packages)
      entry.spdxId: {
        for (final component in entry.shippedBy)
          releaseSpdxId('Package', component),
      },
  };
  for (final entry in thirdParty.packages) {
    for (final shipper in shippersById[entry.spdxId]!) {
      if (!relationshipKeys.contains('$shipper|CONTAINS|${entry.spdxId}')) {
        throw StateError(
          'SPDX SBOM does not record that $shipper contains ${entry.name}.',
        );
      }
    }
  }
  for (final relationship in relationships) {
    final shippers = shippersById[relationship['relatedSpdxElement']];
    if (shippers != null &&
        (relationship['relationshipType'] != 'CONTAINS' ||
            !shippers.contains(relationship['spdxElementId']))) {
      throw StateError(
        'SPDX SBOM relates ${relationship['relatedSpdxElement']} to '
        '${relationship['spdxElementId']}, which does not ship it.',
      );
    }
  }
}

List<MapEntry<String, String>> _catalogEntries(
  TopiaForgeReleaseCatalogEntry release,
) => [
  ...release.components.entries,
  ...release.vpmPackages.entries,
  ...release.mods.entries,
]..sort((left, right) => left.key.compareTo(right.key));

Map<String, Object?> _contains(String container, String contained) => {
  'spdxElementId': container,
  'relationshipType': 'CONTAINS',
  'relatedSpdxElement': contained,
};

Map<String, Object?> _spdxPackage({
  required String name,
  required String version,
  required String spdxId,
  required String license,
  String licenseConcluded = _noAssertion,
  String copyrightText = _noAssertion,
  String downloadLocation = _noAssertion,
}) => {
  'name': name,
  'SPDXID': spdxId,
  'versionInfo': version,
  'downloadLocation': downloadLocation,
  'filesAnalyzed': false,
  'licenseConcluded': licenseConcluded,
  'licenseDeclared': license,
  'copyrightText': copyrightText,
};

List<Map>? _objects(Object? value) =>
    value is List ? value.whereType<Map>().toList() : null;

const _rootId = 'SPDXRef-Package-TopiaForge';
const _noAssertion = 'NOASSERTION';
final _spdxIdPattern = RegExp(r'^SPDXRef-[A-Za-z0-9.-]+$');
final _licenseReference = RegExp(r'LicenseRef-[A-Za-z0-9.-]+');

/// Copyright line recorded for TopiaForge-owned SPDX packages and files.
const _ownedCopyrightText = 'Copyright (C) 2026 furroxide';

/// Release artifacts built solely from first-party sources. Everything else is
/// a platform archive that also redistributes third-party payload.
const _ownedArtifactExtension = '.topiaforgemod';

bool _sameSet(Set<String> left, Set<String> right) =>
    left.length == right.length && left.containsAll(right);
