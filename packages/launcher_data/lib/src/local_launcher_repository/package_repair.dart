part of '../local_launcher_repository.dart';

extension _PackageRepairHelpers on LocalLauncherRepository {
  Future<List<InstalledMod>> _repairInstalledMod(
    GameInstall install,
    InstalledMod mod,
  ) async {
    _requireSafeModId(mod.id);
    final version = mod.repairableVersion;
    if (version == null) {
      throw StateError('${mod.id} has no damaged installed version to repair.');
    }
    if (SemanticVersion.tryParse(version) == null) {
      throw StateError(
        'Cannot repair ${mod.id}: installed version $version is not valid SemVer.',
      );
    }

    final sources = await _loadPackageSources();
    final candidates = await _loadRegistryCandidates(const [], sources);
    RegistryMod? candidate;
    for (final item in candidates) {
      if (item.manifest.id.toLowerCase() == mod.id.toLowerCase() &&
          item.manifest.version == version &&
          _isTrustedRepairCandidate(item)) {
        candidate = item;
        break;
      }
    }

    if (candidate != null) {
      final installed = await _installPackage(
        candidate.downloadUrl,
        install,
        expectedSha256: candidate.packageSha256.toLowerCase(),
        sourceId: candidate.sourceId,
      );
      await _appendLauncherLogBestEffort(
        'Repaired ${mod.id} $version from trusted source ${candidate.sourceId}.',
      );
      return installed;
    }

    final provenance = _repairProvenance(mod, version);
    final sourceSha256 = provenance.sourceSha256.toLowerCase();
    if (provenance.trust == _sha256VerifiedPackageTrust &&
        _isSha256(sourceSha256)) {
      final cached = File(
        p.join(_packageCache.path, '$sourceSha256.topiaforgemod'),
      );
      if (FileSystemEntity.typeSync(cached.path, followLinks: false) ==
          FileSystemEntityType.file) {
        final cachedPackage = await _readPackage(
          cached.path,
          expectedSha256: sourceSha256,
        );
        if (cachedPackage.manifest.id.toLowerCase() != mod.id.toLowerCase() ||
            cachedPackage.manifest.version != version) {
          throw StateError(
            'Verified cache entry $sourceSha256 contains '
            '${cachedPackage.manifest.id} ${cachedPackage.manifest.version}, '
            'not ${mod.id} $version. Re-enable a trusted package source.',
          );
        }
        final installed = await _installPackage(
          cached.path,
          install,
          expectedSha256: sourceSha256,
          rootSourceKind: 'cache',
        );
        await _appendLauncherLogBestEffort(
          'Repaired ${mod.id} $version from its verified package cache entry.',
        );
        return installed;
      }
    }

    throw StateError(
      'No trusted registry package or verified cache entry is available for '
      '${mod.id} $version. Re-enable its package source or reinstall from a '
      'trusted package.',
    );
  }
}

bool _isTrustedRepairCandidate(RegistryMod candidate) {
  final reference = Uri.tryParse(candidate.downloadUrl.trim());
  if (reference == null || !reference.hasScheme) return false;
  final trustedReference =
      reference.scheme == 'file' ||
      (reference.scheme == 'https' && isPublicHttpsUri(reference));
  return trustedReference && _isSha256(candidate.packageSha256.toLowerCase());
}

({String sourceSha256, String trust}) _repairProvenance(
  InstalledMod mod,
  String version,
) {
  for (final status in mod.installedVersions) {
    if (status.version == version) {
      return (sourceSha256: status.sourceSha256, trust: status.trust);
    }
  }
  return (sourceSha256: mod.sourceSha256, trust: mod.trust);
}

bool _isSha256(String value) => RegExp(r'^[0-9a-f]{64}$').hasMatch(value);
