import 'package:launcher_domain/launcher_domain.dart';

void main() {
  final manifest = ModManifest(
    schemaVersion: 4,
    id: 'example.mod',
    name: 'Example Mod',
    version: '1.0.0',
    author: const ModAuthor(name: 'Example Author'),
    entryAssembly: 'Example.dll',
    entryType: 'Example.Entry',
    gameVersionRange: VersionRange.parse(TopiaForgeRuntimeVersions.gameVersion),
    loaderVersionRange: VersionRange.parse(
      TopiaForgeRuntimeVersions.loaderVersion,
    ),
    sdkVersionRange: VersionRange.parse(TopiaForgeRuntimeVersions.sdkVersion),
  );
  print(manifest.toJson());
}
