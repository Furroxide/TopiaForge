part of '../local_launcher_repository.dart';

extension _PackageInstallationHelpers on LocalLauncherRepository {
  Future<PackageInstallPlan> _previewPackageInstallPlan(
    String packagePath,
    GameInstall install, {
    String expectedSha256 = '',
    String sourceId = '',
    String sourceName = '',
  }) async {
    final package = await _readPackage(
      packagePath,
      expectedSha256: expectedSha256,
    );
    final installed = await _loadInstalledMods(install);
    final sources = await _loadPackageSources();
    final registryMods = await _loadRegistryMods(installed, sources);
    return _dependencyPlanner.previewInstall(
      package.manifest,
      installed,
      packageSha256: package.sha256Hex,
      packageUrl: package.reference,
      sourceId: sourceId,
      sourceName: sourceName,
      availableMods: registryMods,
      loaderVersion: LocalLauncherRepository._loaderVersion,
      sdkVersion: LocalLauncherRepository._sdkVersion,
    );
  }

  Future<List<InstalledMod>> _installPackage(
    String packagePath,
    GameInstall install, {
    String expectedSha256 = '',
  }) async {
    final package = await _readPackage(
      packagePath,
      expectedSha256: expectedSha256,
    );
    final installed = await _loadInstalledMods(install);
    final sources = await _loadPackageSources();
    final registryMods = await _loadRegistryMods(installed, sources);
    final plan = _dependencyPlanner.previewInstall(
      package.manifest,
      installed,
      packageSha256: package.sha256Hex,
      packageUrl: package.reference,
      availableMods: registryMods,
      loaderVersion: LocalLauncherRepository._loaderVersion,
      sdkVersion: LocalLauncherRepository._sdkVersion,
    );
    final blocking = plan.issues.where((issue) => issue.isBlocking).toList();
    if (blocking.isNotEmpty) {
      throw StateError(blocking.map((issue) => issue.message).join(' '));
    }

    final state = await _readManagerState(install);
    for (final action in plan.installActions) {
      final actionPackage = action.root
          ? package
          : await _readPackage(
              action.packageUrl,
              expectedSha256: action.packageSha256,
            );
      _extractPackageToInstall(actionPackage, install);
      _upsertState(
        state,
        actionPackage.manifest,
        enabled: true,
        restartRequired: true,
        preserveExistingEnabled: true,
      );
    }
    await _saveManagerState(install, state);
    await _appendLauncherLog(
      'Installed ${plan.installActions.length} package(s) for ${package.manifest.id} from $packagePath.',
    );
    return _loadInstalledMods(install);
  }
}
