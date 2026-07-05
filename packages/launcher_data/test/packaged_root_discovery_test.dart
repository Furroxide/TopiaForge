import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory root;
  late Directory dataRoot;
  late Directory repoRoot;
  late Directory gameRoot;

  setUp(() {
    root = Directory.systemTemp.createTempSync('robotopia-packaged-root-data-');
    dataRoot = Directory(p.join(root.path, 'data'))..createSync();
    repoRoot = Directory(p.join(root.path, 'package'))..createSync();
    gameRoot = Directory(p.join(root.path, 'Robotopia'))..createSync();
    _createGame(gameRoot);
    // Keep the official remote source disabled: this suite is about packaged
    // dist discovery, so a network fetch would only slow the test and add
    // unrelated failure modes. Reconciliation still rebuilds the local source
    // URL from the discovered root.
    File(p.join(dataRoot.path, 'package_sources.json')).writeAsStringSync(
      jsonEncode({
        'sources': [
          {
            'id': 'robotopia.local',
            'name': 'Bundled Local Packages',
            'url': 'file:///reconciled-at-load-time',
            'enabled': true,
            'builtIn': true,
          },
          {
            'id': ModRegistryFormat.officialSourceId,
            'name': ModRegistryFormat.officialSourceName,
            'url': ModRegistryFormat.officialRegistryUrl,
            'enabled': false,
            'builtIn': true,
          },
        ],
      }),
    );
  });

  tearDown(() {
    if (root.existsSync()) {
      root.deleteSync(recursive: true);
    }
  });

  test(
    'launcher repository discovers packaged dist from current directory',
    () async {
      _skipWhenRepositoryRootEnvIsSet();
      final dist = _createPackagedRoot(repoRoot);
      _createPackage(dist, id: 'packaged.registry', version: '1.0.0');
      final workingDirectory = Directory(p.join(repoRoot.path, 'launcher'))
        ..createSync(recursive: true);

      final repository = LocalLauncherRepository(
        dataRoot: dataRoot.path,
        workingDirectory: workingDirectory.path,
        knownGamePath: gameRoot.path,
      );

      final snapshot = await repository.loadSnapshot();

      expect(snapshot.registryMods.single.manifest.id, 'packaged.registry');
    },
  );

  test(
    'developer repository discovers packaged dist from current directory',
    () async {
      _skipWhenRepositoryRootEnvIsSet();
      final dist = _createPackagedRoot(repoRoot);
      _createPackage(dist, id: 'packaged.api', version: '1.0.0');
      final workingDirectory = Directory(p.join(repoRoot.path, 'launcher'))
        ..createSync(recursive: true);

      final repository = LocalDeveloperRepository(
        dataRoot: dataRoot.path,
        workingDirectory: workingDirectory.path,
      );
      final workspace = await repository.createModProject(
        parentDirectory: root.path,
        id: 'packaged.consumer',
        name: 'Packaged Consumer',
      );

      await repository.addProjectDependency(
        workspace.projectRoot,
        const ModDependency(id: 'packaged.api'),
      );
      final restored = await repository.resolveDeveloperProject(
        workspace.projectRoot,
      );

      expect(restored.issues.where((issue) => issue.isBlocking), isEmpty);
      expect(restored.lock!.packages.single.id, 'packaged.api');
    },
  );
}

Directory _createPackagedRoot(Directory repoRoot) {
  Directory(p.join(repoRoot.path, 'tools')).createSync(recursive: true);
  Directory(p.join(repoRoot.path, 'templates')).createSync(recursive: true);
  return Directory(p.join(repoRoot.path, 'dist'))..createSync(recursive: true);
}

void _createGame(Directory gameRoot) {
  File(p.join(gameRoot.path, 'Robotopia.exe')).writeAsStringSync('');
  Directory(
    p.join(gameRoot.path, 'Robotopia_Data', 'Managed'),
  ).createSync(recursive: true);
  File(
    p.join(gameRoot.path, 'Robotopia_Data', 'Managed', 'UnityEngine.dll'),
  ).writeAsStringSync('');
}

File _createPackage(
  Directory dist, {
  required String id,
  required String version,
}) {
  final package = File(p.join(dist.path, '$id-$version.robotopiamod'));
  final archive = Archive()
    ..addFile(
      ArchiveFile.string(
        'robotopia.mod.json',
        jsonEncode(_manifestJson(id, version)),
      ),
    )
    ..addFile(ArchiveFile.string('${_assemblyName(id)}.dll', 'dll'));
  package.writeAsBytesSync(ZipEncoder().encode(archive));
  return package;
}

Map<String, Object?> _manifestJson(String id, String version) => {
  'schemaVersion': 2,
  'name': id,
  'displayName': id,
  'version': version,
  'author': {'name': 'QuantumWorks'},
  'entryAssembly': '${_assemblyName(id)}.dll',
  'entryType': '$id.Entry',
};

String _assemblyName(String id) {
  return id
      .split(RegExp(r'[^A-Za-z0-9]+'))
      .where((part) => part.isNotEmpty)
      .map((part) => part[0].toUpperCase() + part.substring(1))
      .join();
}

void _skipWhenRepositoryRootEnvIsSet() {
  final configured = Platform.environment['ROBOTOPIA_REPOSITORY_ROOT'];
  if (configured != null && configured.trim().isNotEmpty) {
    markTestSkipped(
      'ROBOTOPIA_REPOSITORY_ROOT is set, so default discovery must prefer it.',
    );
  }
}
