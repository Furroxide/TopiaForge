part of '../multiplayer.dart';

/// An archive installed in the game: its exact SHA-256 and the manifest its
/// install receipt recorded. The data layer lists only installs that
/// validate, so their manifests are trusted.
final class ExactInstalledArchive {
  ExactInstalledArchive({required String sha256, required this.manifest})
    : sha256 = _exactSha256(sha256);

  final String sha256;
  final ModManifest manifest;
}

/// One archive a registry source offers, from its current entry or from a
/// history row that carries a manifest: the exact SHA-256, the manifest the
/// source advertises for it, and where to download it.
final class ExactSourcePackage {
  ExactSourcePackage({
    required this.sourceId,
    required this.sourceName,
    required this.enabled,
    required this.manifest,
    required this.downloadUrl,
    required String sha256,
    this.sizeBytes,
  }) : sha256 = _exactSha256(sha256) {
    if (downloadUrl.trim().isEmpty) {
      throw ArgumentError('Required.', 'downloadUrl');
    }
    if (sizeBytes != null &&
        (sizeBytes! < 0 || sizeBytes! > ModRegistryFormat.maxPackageBytes)) {
      throw RangeError.range(
        sizeBytes!,
        0,
        ModRegistryFormat.maxPackageBytes,
        'sizeBytes',
      );
    }
  }

  final String sourceId;
  final String sourceName;

  /// Whether the player has the source switched on. Only enabled sources
  /// supply packages; a disabled one only explains why a join is blocked.
  final bool enabled;
  final ModManifest manifest;
  final String downloadUrl;
  final String sha256;
  final int? sizeBytes;
}

/// Everything the launcher knows about exact archives: what is installed,
/// and what each source offers, enabled or not, current versions and
/// history alike. Sources are listed in the player's order, which breaks
/// ties.
final class ExactPackageCatalog {
  ExactPackageCatalog({
    Iterable<ExactInstalledArchive> installed = const [],
    Iterable<ExactSourcePackage> sources = const [],
  }) : installed = List.unmodifiable(installed),
       sources = List.unmodifiable(sources);

  final List<ExactInstalledArchive> installed;
  final List<ExactSourcePackage> sources;

  /// What the launcher found for each of [descriptor]'s packages, keyed by
  /// the exact SHA-256 the descriptor pins, as [JoinPlanner.plan] takes it.
  Map<String, JoinPackageResolution> resolve(
    ServerDescriptorV1 descriptor, {
    required bool developerMode,
  }) => {
    for (final package in descriptor.packages)
      package.sha256: resolvePackage(
        package,
        developerMode: developerMode,
        hostServesPackages: descriptor.hostServesPackages,
      ),
  };

  /// Classifies one package by its exact archive, in this order:
  ///
  /// 1. **installed**: an install has the exact archive.
  /// 2. **installedAsOtherArchive**: this id and version are installed as
  ///    another archive. Both would need the same folder, and a join never
  ///    replaces an installed archive, so no source or host can supply it.
  /// 3. **available**: an enabled source offers the exact archive.
  /// 4. **inDisabledSource**: only a disabled source offers it.
  /// 5. **hostOffered**: in developer mode, when the host serves its
  ///    packages, any other package comes from the host, checked after
  ///    download. Registry archives always come first.
  /// 6. **hashMismatch**: an enabled source offers this id and version as
  ///    another archive.
  /// 7. **versionUnavailable**: enabled sources know the mod, not this
  ///    version.
  /// 8. **notInSources**: no enabled source knows the mod.
  ///
  /// Only installed and available carry a manifest, and both manifests come
  /// from the player's own receipts and sources, never from the host.
  JoinPackageResolution resolvePackage(
    DescriptorPackage package, {
    required bool developerMode,
    required bool hostServesPackages,
  }) {
    for (final archive in installed) {
      if (archive.sha256 == package.sha256) {
        return JoinPackageResolution.installed(archive.manifest);
      }
    }
    for (final archive in installed) {
      if (archive.manifest.id.toLowerCase() == package.id.toLowerCase() &&
          archive.manifest.version == package.version) {
        return JoinPackageResolution.unavailable(
          JoinPackageAvailability.installedAsOtherArchive,
        );
      }
    }
    final enabled = sources.where((item) => item.enabled).toList();
    for (final offer in enabled) {
      if (offer.sha256 == package.sha256) {
        return JoinPackageResolution.available(
          manifest: offer.manifest,
          sourceId: offer.sourceId,
          sourceName: offer.sourceName,
          downloadUrl: offer.downloadUrl,
          sizeBytes: offer.sizeBytes,
        );
      }
    }
    for (final offer in sources) {
      if (!offer.enabled && offer.sha256 == package.sha256) {
        return JoinPackageResolution.unavailable(
          JoinPackageAvailability.inDisabledSource,
          sourceName: offer.sourceName,
        );
      }
    }
    if (developerMode && hostServesPackages) {
      return JoinPackageResolution.hostOffered();
    }
    final id = package.id.toLowerCase();
    final known = enabled
        .where((offer) => offer.manifest.id.toLowerCase() == id)
        .toList();
    for (final offer in known) {
      if (offer.manifest.version == package.version) {
        return JoinPackageResolution.unavailable(
          JoinPackageAvailability.hashMismatch,
          sourceName: offer.sourceName,
        );
      }
    }
    if (known.isNotEmpty) {
      return JoinPackageResolution.unavailable(
        JoinPackageAvailability.versionUnavailable,
        sourceName: known.first.sourceName,
      );
    }
    return JoinPackageResolution.unavailable(
      JoinPackageAvailability.notInSources,
    );
  }
}

String _exactSha256(String sha256) {
  if (!WireText.isHex(sha256, 32)) {
    throw ArgumentError('A SHA-256 is 64 lowercase hexadecimal digits.');
  }
  return sha256;
}
