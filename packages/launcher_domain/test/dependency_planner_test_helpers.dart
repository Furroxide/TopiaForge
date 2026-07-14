part of 'dependency_planner_test.dart';

ModManifest _manifest(
  String id, {
  String version = '1.0.0',
  List<ModDependency> dependencies = const [],
  List<ModDependency> optionalDependencies = const [],
  List<ModConflict> conflicts = const [],
  List<String> loadAfter = const [],
  VersionRange gameVersionRange = const VersionRange.any(),
  VersionRange loaderVersionRange = const VersionRange.any(),
  VersionRange sdkVersionRange = const VersionRange.any(),
}) {
  return ModManifest(
    schemaVersion: 3,
    id: id,
    name: id,
    version: version,
    author: const ModAuthor(name: 'TopiaForge'),
    entryAssembly: '$id.dll',
    entryType: '$id.Entry',
    dependencies: dependencies,
    optionalDependencies: optionalDependencies,
    conflicts: conflicts,
    loadAfter: loadAfter,
    gameVersionRange: gameVersionRange,
    loaderVersionRange: loaderVersionRange,
    sdkVersionRange: sdkVersionRange,
  );
}

InstalledMod _installed(ModManifest manifest, {bool enabled = true}) {
  return InstalledMod(
    id: manifest.id,
    name: manifest.name,
    version: manifest.version,
    enabled: enabled,
    restartRequired: false,
    uninstallPending: false,
    packagePath: '/tmp/${manifest.id}',
    manifest: manifest,
  );
}

RegistryMod _registry(ModManifest manifest) => RegistryMod(
  manifest: manifest,
  downloadUrl: 'file:///${manifest.id}-${manifest.version}.topiaforgemod',
  packageSha256: _validSha,
);

final _validSha = List.filled(64, 'a').join();
