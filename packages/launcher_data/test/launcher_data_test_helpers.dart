part of 'launcher_data_test.dart';

void _createGame(Directory gameRoot) {
  File(p.join(gameRoot.path, 'Robotopia.exe')).writeAsStringSync('');
  Directory(
    p.join(gameRoot.path, 'Robotopia_Data', 'Managed'),
  ).createSync(recursive: true);
  File(
    p.join(gameRoot.path, 'Robotopia_Data', 'Managed', 'UnityEngine.dll'),
  ).writeAsStringSync('');
}

void _createRuntimeSources(Directory repoRoot) {
  final bepinex = Directory(
    p.join(repoRoot.path, 'third_party', 'BepInEx', 'win_x64_5.4.23.5'),
  )..createSync(recursive: true);
  File(p.join(bepinex.path, 'winhttp.dll')).writeAsStringSync('');
  File(p.join(bepinex.path, 'doorstop_config.ini')).writeAsStringSync('');
  Directory(
    p.join(bepinex.path, 'BepInEx', 'core'),
  ).createSync(recursive: true);
  File(
    p.join(bepinex.path, 'BepInEx', 'core', 'BepInEx.dll'),
  ).writeAsStringSync('');

  final loader = Directory(
    p.join(
      repoRoot.path,
      'src',
      'TopiaForge.ModManager',
      'bin',
      'Release',
      'netstandard2.1',
    ),
  )..createSync(recursive: true);
  for (final dll in topiaForgeRuntimeLoaderDlls) {
    File(p.join(loader.path, dll)).writeAsStringSync('');
  }
}

// The built-in local source derives its catalog from the .topiaforgemod packages in dist/, so the
// fixture publishes a real package there rather than a hand-written registry document.
void _createRegistry(Directory repoRoot) {
  final dist = Directory(p.join(repoRoot.path, 'dist'))
    ..createSync(recursive: true);
  final archive = Archive()
    ..addFile(
      ArchiveFile.string(
        'topiaforge.mod.json',
        jsonEncode(
          _manifestJson(
            'registry.sample',
            '1.0.0',
            worldGamemodes: [
              {'id': 'registry.sample.survival', 'name': 'Registry Survival'},
            ],
          ),
        ),
      ),
    )
    ..addFile(
      ArchiveFile.string('${_assemblyName('registry.sample')}.dll', 'dll'),
    );
  File(
    p.join(dist.path, 'registry.sample-1.0.0.topiaforgemod'),
  ).writeAsBytesSync(ZipEncoder().encode(archive));
}

void _writeDistPackage(
  Directory dist, {
  required String id,
  required String version,
}) {
  dist.createSync(recursive: true);
  final archive = Archive()
    ..addFile(
      ArchiveFile.string(
        'topiaforge.mod.json',
        jsonEncode(_manifestJson(id, version)),
      ),
    )
    ..addFile(ArchiveFile.string('${_assemblyName(id)}.dll', 'dll'));
  File(
    p.join(dist.path, '$id-$version.topiaforgemod'),
  ).writeAsBytesSync(ZipEncoder().encode(archive));
}

File _createPackage(
  Directory root, {
  required String id,
  required String version,
  List<Map<String, Object?>> dependencies = const [],
  List<Map<String, Object?>> worldGamemodes = const [],
  List<String> apiAssemblies = const [],
  String? gameVersionRange,
  String? loaderVersionRange,
  String? sdkVersionRange,
}) {
  final package = File(p.join(root.path, '$id-$version.topiaforgemod'));
  final archive = Archive()
    ..addFile(
      ArchiveFile.string(
        'topiaforge.mod.json',
        jsonEncode(
          _manifestJson(
            id,
            version,
            dependencies: dependencies,
            worldGamemodes: worldGamemodes,
            apiAssemblies: apiAssemblies,
            gameVersionRange: gameVersionRange,
            loaderVersionRange: loaderVersionRange,
            sdkVersionRange: sdkVersionRange,
          ),
        ),
      ),
    )
    ..addFile(ArchiveFile.string('${_assemblyName(id)}.dll', 'dll'));
  for (final assembly in apiAssemblies) {
    archive.addFile(ArchiveFile.string(assembly, 'api'));
  }
  package.writeAsBytesSync(ZipEncoder().encode(archive));
  return package;
}

Map<String, Object?> _manifestJson(
  String id,
  String version, {
  List<Map<String, Object?>> dependencies = const [],
  List<Map<String, Object?>> worldGamemodes = const [],
  List<String> apiAssemblies = const [],
  String? gameVersionRange,
  String? loaderVersionRange,
  String? sdkVersionRange,
}) => {
  'schemaVersion': 5,
  'name': id,
  'displayName': id,
  'version': version,
  'author': {'name': 'TopiaForge'},
  'entryAssembly': '${_assemblyName(id)}.dll',
  'entryType': '$id.Entry',
  'supportedGameVersionRange': gameVersionRange ?? '*',
  'supportedLoaderVersionRange': loaderVersionRange ?? '*',
  'supportedSdkVersionRange': sdkVersionRange ?? '*',
  if (dependencies.isNotEmpty)
    'dependencies': {
      for (final item in dependencies)
        item['id'] as String: (item['versionRange'] ?? item['version'] ?? '*')
            .toString(),
    },
  if (worldGamemodes.isNotEmpty) 'worldGamemodes': worldGamemodes,
  if (apiAssemblies.isNotEmpty) 'apiAssemblies': apiAssemblies,
};

String sha256Of(File file) => sha256.convert(file.readAsBytesSync()).toString();

String _assemblyName(String id) {
  return id
      .split(RegExp(r'[^A-Za-z0-9]+'))
      .where((part) => part.isNotEmpty)
      .map((part) => part[0].toUpperCase() + part.substring(1))
      .join();
}
