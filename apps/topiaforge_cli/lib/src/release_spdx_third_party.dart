import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;

import 'release_dart_runtime_packages.dart';
import 'release_package_payload.dart'
    show releaseGameCompatRuntimeVersion, releaseMetadataLoadContextVersion;
import 'release_policy.dart';
import 'spdx_ids_3_28.g.dart' show spdxLicenseListVersion;

part 'release_spdx_third_party_records.dart';

/// SPDX element identifier for a release package or file.
String releaseSpdxId(String kind, String value) {
  final safe = value.replaceAll(RegExp(r'[^A-Za-z0-9.-]'), '-');
  return 'SPDXRef-$kind-$safe';
}

/// A third-party component shipped inside one or more release-catalog
/// components.
///
/// [licenseDeclared] is the upstream licence exactly as this repository
/// records it for this release of the component, or `NOASSERTION` when it
/// records none. The project neither concludes a licence for nor holds the
/// copyright in third-party code, so both of those are always `NOASSERTION`.
final class ReleaseSpdxThirdPartyPackage {
  const ReleaseSpdxThirdPartyPackage({
    required this.name,
    required this.version,
    required this.licenseDeclared,
    required this.shippedBy,
    this.ecosystem = '',
    this.downloadLocation = 'NOASSERTION',
    this.comment,
  });

  final String name;

  /// Null when the repository records no version.
  final String? version;
  final String licenseDeclared;

  /// Release-catalog component keys whose shipped bytes contain this package.
  final Set<String> shippedBy;

  /// Package-URL type for packages taken from a registry: `pub` or `nuget`.
  final String ecosystem;
  final String downloadLocation;
  final String? comment;

  String get spdxId => releaseSpdxId(
    'Package',
    [
      'ThirdParty',
      if (ecosystem.isNotEmpty) ecosystem,
      name,
      ?version,
    ].join('-'),
  );

  String? get purl => ecosystem.isEmpty || version == null
      ? null
      : 'pkg:$ecosystem/${Uri.encodeComponent(name)}'
            '@${Uri.encodeComponent(version!)}';

  ReleaseSpdxThirdPartyPackage _alsoShippedBy(Set<String> components) =>
      ReleaseSpdxThirdPartyPackage(
        name: name,
        version: version,
        licenseDeclared: licenseDeclared,
        shippedBy: {...shippedBy, ...components},
        ecosystem: ecosystem,
        downloadLocation: downloadLocation,
        comment: comment,
      );

  Map<String, Object?> toSpdx() => {
    'name': name,
    'SPDXID': spdxId,
    if (version != null) 'versionInfo': version,
    'downloadLocation': downloadLocation,
    'filesAnalyzed': false,
    'licenseConcluded': 'NOASSERTION',
    'licenseDeclared': licenseDeclared,
    'copyrightText': 'NOASSERTION',
    if (purl != null)
      'externalRefs': [
        {
          'referenceCategory': 'PACKAGE-MANAGER',
          'referenceType': 'purl',
          'referenceLocator': purl,
        },
      ],
    if (comment != null) 'comment': comment,
  };
}

/// Every third-party component a release ships, and which catalog component
/// ships it.
final class ReleaseSpdxThirdPartyInventory {
  ReleaseSpdxThirdPartyInventory._(this.packages);

  /// Third-party packages, ordered by SPDX identifier.
  final List<ReleaseSpdxThirdPartyPackage> packages;

  /// Catalog components that ship at least one third-party package.
  Set<String> get containers => {
    for (final entry in packages) ...entry.shippedBy,
  };

  /// SPDX `hasExtractedLicensingInfos` entries for the licence references the
  /// packages declare, ordered by identifier.
  List<Map<String, Object?>> get extractedLicensingInfos {
    final references = {
      for (final entry in packages)
        ...RegExp(
          r'LicenseRef-[A-Za-z0-9.-]+',
        ).allMatches(entry.licenseDeclared).map((match) => match[0]!),
    }.toList()..sort();
    return [
      for (final id in references)
        {
          'licenseId': id,
          ...(_licenseReferences[id] ??
              (throw StateError('No licensing information records $id.'))),
        },
    ];
  }

  /// Builds the inventory from the tracked lockfiles, the pinned runtime
  /// inputs, and the vendored records in release_spdx_third_party_records.dart.
  /// Fails closed when a pinned input moved past the release those records
  /// were verified against.
  static ReleaseSpdxThirdPartyInventory load({
    required String repositoryRoot,
    required TopiaForgeReleasePolicy policy,
    required TopiaForgeReleaseCatalogEntry release,
  }) {
    _requireRecordedPins(policy, release);
    final merged = <String, ReleaseSpdxThirdPartyPackage>{};
    void add(ReleaseSpdxThirdPartyPackage entry) {
      merged[entry.spdxId] =
          merged[entry.spdxId]?._alsoShippedBy(entry.shippedBy) ?? entry;
    }

    _vendoredPackages.forEach(add);
    final dart = policy.toolchains['dart'];
    final flutter = policy.toolchains['flutter'];
    if (dart == null || flutter == null) {
      throw StateError('The release policy pins no Dart or Flutter toolchain.');
    }
    // The CLI embeds the Dart runtime directly and the launcher through
    // Flutter, whose release pins the same Dart SDK
    // (.github/actions/setup-flutter/releases.json).
    add(_recorded('Dart SDK', dart, shippedBy: {'cli', 'launcher'}));
    add(
      ReleaseSpdxThirdPartyPackage(
        name: 'Flutter',
        version: flutter,
        licenseDeclared: 'NOASSERTION',
        shippedBy: {'launcher'},
        comment:
            'Flutter engine and framework; Flutter writes their notices '
            'into the launcher NOTICES.Z.',
      ),
    );
    add(
      _recorded(
        '.NET Runtime',
        releaseGameCompatRuntimeVersion,
        shippedBy: {'gameCompatExtractor'},
        comment: 'Self-contained runtime inside the GameCompat extractor.',
      ),
    );
    add(
      _recorded(
        'System.Reflection.MetadataLoadContext',
        releaseMetadataLoadContextVersion,
        shippedBy: {'gameCompatExtractor'},
        ecosystem: 'nuget',
      ),
    );
    // Installed beside the loader. Packaging rejects a pinned package whose
    // signed nuspec does not declare MIT (release_package_notices.dart), and
    // THIRD_PARTY_NOTICES.md records MIT for both.
    for (final assembly in topiaForgeRuntimeLoaderAssemblies) {
      if (!assembly.isPinnedPackage) continue;
      add(
        ReleaseSpdxThirdPartyPackage(
          name: assembly.packageId,
          version: assembly.packageVersion,
          licenseDeclared: 'MIT',
          shippedBy: {'loader'},
          ecosystem: 'nuget',
          downloadLocation:
              'git+https://github.com/dotnet/dotnet@${assembly.repositoryCommit}',
        ),
      );
    }
    for (final shipment in releaseDartShipments.entries) {
      final versions = readLockedHostedVersions(
        File(p.join(repositoryRoot, shipment.value.lockfile)),
        shipment.value.packages,
      );
      for (final entry in versions.entries) {
        add(
          _recorded(
            entry.key,
            entry.value,
            shippedBy: {shipment.key},
            ecosystem: 'pub',
          ),
        );
      }
    }
    final unknown = {
      for (final entry in merged.values) ...entry.shippedBy,
    }.difference(release.components.keys.toSet());
    if (unknown.isNotEmpty) {
      throw StateError(
        'Third-party SBOM records name components missing from the release '
        'catalog: ${(unknown.toList()..sort()).join(', ')}.',
      );
    }
    return ReleaseSpdxThirdPartyInventory._(
      List.unmodifiable(
        merged.values.toList()
          ..sort((left, right) => left.spdxId.compareTo(right.spdxId)),
      ),
    );
  }
}

ReleaseSpdxThirdPartyPackage _recorded(
  String name,
  String version, {
  required Set<String> shippedBy,
  String ecosystem = '',
  String? comment,
}) => ReleaseSpdxThirdPartyPackage(
  name: name,
  version: version,
  licenseDeclared:
      releaseSpdxRecordedLicenses['$name@$version'] ?? 'NOASSERTION',
  shippedBy: shippedBy,
  ecosystem: ecosystem,
  comment: comment,
);

/// The vendored records describe these exact pins. A pin that moves
/// must be re-verified here before the SBOM may describe it.
void _requireRecordedPins(
  TopiaForgeReleasePolicy policy,
  TopiaForgeReleaseCatalogEntry release,
) {
  if (policy.targetsMacOS) {
    throw StateError(
      'The release SBOM records the Windows BepInEx payload only; the macOS '
      'UnityDoorstop stub also links BSD-licensed plthook code. Record it in '
      'release_spdx_third_party_records.dart before describing a macOS '
      'release.',
    );
  }
  final pins = <String, (String?, String)>{
    'BepInEx': (release.components['bepInEx'], '5.4.23.5'),
    'UnityDoorstop': (policy.unityDoorstopVersion, '4.5.0'),
    'UnityDoorstop commit': (
      policy.unityDoorstopCommit,
      '33dab9a6733862eb81869ff08431d9478b28784b',
    ),
    'SPDX License List Data': (spdxLicenseListVersion, '3.28.0'),
  };
  for (final pin in pins.entries) {
    if (pin.value.$1 != pin.value.$2) {
      throw StateError(
        'The release SBOM records ${pin.key} ${pin.value.$2}, but this '
        'release pins ${pin.value.$1}. Re-verify its third-party records in '
        'release_spdx_third_party_records.dart.',
      );
    }
  }
}
