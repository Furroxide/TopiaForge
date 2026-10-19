part of '../local_launcher_repository.dart';

/// Builds the [ExactPackageCatalog] a join plan resolves against: every
/// valid installed archive with its receipt's manifest, and every archive the
/// player's sources offer, history rows included.
///
/// Enabled sources are read as the catalog reads them. A disabled source is
/// read only when it is local, so the launcher never contacts a remote
/// source the player switched off; a package only such a source offers is
/// then reported as not in any source rather than in a disabled one.
extension _ExactPackageCatalogLoading on LocalLauncherRepository {
  Future<ExactPackageCatalog> _loadExactPackageCatalog(
    GameInstall install,
  ) async {
    final installed = <ExactInstalledArchive>[];
    final versions = await _loadInstalledVersionCatalog(install);
    // Empty while it cannot be used: every install then reads as the
    // player's, which only words a conflict more cautiously.
    final record = (await _serverContentStore(install).load()).record;
    // What a join that failed or stopped part way left in place goes at the
    // next sweep, so no plan counts it as installed: the join downloads it
    // instead, and its install waits until that sweep can take it out.
    final unsettled = {
      for (final journal in await _unsettledJoinJournals(install))
        for (final package in journal.packages)
          '${_versionKey(package.id, package.version)} '
              '${package.receiptSha256}',
    };
    for (final candidates in versions.values) {
      for (final version in candidates) {
        final manifest = version.manifest;
        final sha256 = version.sourceSha256.trim().toLowerCase();
        if (manifest == null ||
            !version.isValid ||
            version.uninstallPending ||
            !_exactSha256Pattern.hasMatch(sha256)) {
          continue;
        }
        if (unsettled.isNotEmpty &&
            unsettled.contains(
              '${_versionKey(manifest.id, manifest.version)} '
              '${await _receiptSha256(Directory(version.packagePath))}',
            )) {
          continue;
        }
        final entry = record.entryFor(manifest.id, manifest.version);
        installed.add(
          ExactInstalledArchive(
            sha256: sha256,
            manifest: manifest,
            serverOnly:
                entry != null &&
                entry.hidden &&
                entry.receiptSha256 ==
                    await _receiptSha256(Directory(version.packagePath)),
          ),
        );
      }
    }

    final offers = <ExactSourcePackage>[];
    for (final source in await _loadPackageSources()) {
      if (!source.enabled && _isRemoteSource(source)) continue;
      final _RegistrySourceMods loaded;
      try {
        loaded = await _loadRegistrySource(source);
      } on Object catch (error) {
        await _appendLauncherLogBestEffort(
          'Package source ${source.id} failed: $error',
        );
        continue;
      }
      for (final mod in [...loaded.current, ...loaded.history]) {
        final sha256 = mod.packageSha256.trim().toLowerCase();
        if (mod.downloadUrl.trim().isEmpty ||
            !_exactSha256Pattern.hasMatch(sha256) ||
            mod.manifest.validate().any((issue) => issue.isBlocking)) {
          continue;
        }
        offers.add(
          ExactSourcePackage(
            sourceId: source.id,
            sourceName: source.name,
            enabled: source.enabled,
            manifest: mod.manifest,
            downloadUrl: mod.downloadUrl,
            sha256: sha256,
            sizeBytes: _statedSize(mod.packageBytes),
          ),
        );
      }
    }
    return ExactPackageCatalog(installed: installed, sources: offers);
  }
}

final _exactSha256Pattern = RegExp(r'^[0-9a-f]{64}$');

int? _statedSize(int? bytes) =>
    bytes != null && bytes >= 0 && bytes <= ModRegistryFormat.maxPackageBytes
    ? bytes
    : null;
