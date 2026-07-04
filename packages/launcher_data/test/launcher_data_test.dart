import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:crypto/crypto.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory root;
  late Directory dataRoot;
  late Directory repoRoot;
  late Directory gameRoot;
  late LocalLauncherRepository repository;

  setUp(() {
    root = Directory.systemTemp.createTempSync('robotopia-launcher-data-');
    dataRoot = Directory(p.join(root.path, 'data'))..createSync();
    repoRoot = Directory(p.join(root.path, 'repo'))..createSync();
    gameRoot = Directory(p.join(root.path, 'Robotopia'))..createSync();
    _createGame(gameRoot);
    _createRuntimeSources(repoRoot);
    _createRegistry(repoRoot);
    repository = LocalLauncherRepository(
      dataRoot: dataRoot.path,
      repositoryRoot: repoRoot.path,
      knownGamePath: gameRoot.path,
    );
  });

  tearDown(() {
    if (root.existsSync()) {
      root.deleteSync(recursive: true);
    }
  });

  test('detects known install and repairs BepInEx plus loader', () async {
    final install = await repository.detectKnownInstall();
    expect(install, isNotNull);
    expect(install!.bepInExStatus, ComponentState.missing);

    final report = await repository.installOrRepairRuntime(install);
    expect(report.ok, isTrue);

    final repaired = await repository.selectGameDirectory(gameRoot.path);
    expect(repaired.bepInExStatus, ComponentState.ready);
    expect(repaired.loaderStatus, ComponentState.ready);
  });

  test('marks loader partial when installed DLLs are stale', () async {
    final install = await repository.selectGameDirectory(gameRoot.path);
    final report = await repository.installOrRepairRuntime(install);
    expect(report.ok, isTrue);

    File(
      p.join(
        gameRoot.path,
        'BepInEx',
        'plugins',
        'RobotopiaModManager',
        'Robotopia.Mods.Abstractions.dll',
      ),
    ).writeAsStringSync('old abstraction dll');

    final stale = await repository.selectGameDirectory(gameRoot.path);
    expect(stale.loaderStatus, ComponentState.partial);
    expect(stale.needsRepair, isTrue);
  });

  test(
    'repair deploys the UnityUi kit and detection flags it when missing',
    () async {
      final install = await repository.selectGameDirectory(gameRoot.path);
      final report = await repository.installOrRepairRuntime(install);
      expect(report.ok, isTrue);

      final unityUi = File(
        p.join(
          gameRoot.path,
          'BepInEx',
          'plugins',
          'RobotopiaModManager',
          'Robotopia.Mods.UnityUi.dll',
        ),
      );
      expect(
        unityUi.existsSync(),
        isTrue,
        reason: 'runtime repair must deploy the QwUi kit beside the loader',
      );

      // The manager plugin hard-depends on the kit, so losing it alone must drop
      // the loader pill to partial and flag a repair.
      unityUi.deleteSync();
      final degraded = await repository.selectGameDirectory(gameRoot.path);
      expect(degraded.loaderStatus, ComponentState.partial);
      expect(degraded.needsRepair, isTrue);
    },
  );

  test('installs, updates, disables, and uninstalls local packages', () async {
    final install = await repository.selectGameDirectory(gameRoot.path);
    final firstPackage = _createPackage(
      root,
      id: 'alpha.mod',
      version: '1.0.0',
    );
    final secondPackage = _createPackage(
      root,
      id: 'alpha.mod',
      version: '1.1.0',
    );

    var mods = await repository.installPackage(firstPackage.path, install);
    expect(mods.single.version, '1.0.0');
    expect(mods.single.enabled, isTrue);

    mods = await repository.installPackage(secondPackage.path, install);
    expect(mods.single.version, '1.1.0');

    mods = await repository.setModEnabled(install, 'alpha.mod', false);
    expect(mods.single.enabled, isFalse);
    expect(mods.single.restartRequired, isTrue);

    mods = await repository.uninstallMod(install, 'alpha.mod');
    expect(mods, isEmpty);
  });

  test('keeps disabled mods disabled when installing an update', () async {
    final install = await repository.selectGameDirectory(gameRoot.path);
    final firstPackage = _createPackage(
      root,
      id: 'alpha.mod',
      version: '1.0.0',
    );
    final secondPackage = _createPackage(
      root,
      id: 'alpha.mod',
      version: '1.1.0',
    );

    await repository.installPackage(firstPackage.path, install);
    var mods = await repository.setModEnabled(install, 'alpha.mod', false);
    expect(mods.single.enabled, isFalse);

    mods = await repository.installPackage(secondPackage.path, install);

    expect(mods.single.version, '1.1.0');
    expect(mods.single.enabled, isFalse);
    expect(mods.single.restartRequired, isTrue);
  });

  test('rejects zip traversal during preview', () async {
    final install = await repository.selectGameDirectory(gameRoot.path);
    final package = File(p.join(root.path, 'traversal.robotopiamod'));
    final archive = Archive()
      ..addFile(ArchiveFile.string('../escape.txt', 'nope'))
      ..addFile(
        ArchiveFile.string(
          'robotopia.mod.json',
          jsonEncode(_manifestJson('bad.mod', '1.0.0')),
        ),
      )
      ..addFile(ArchiveFile.string('Bad.dll', 'dll'));
    package.writeAsBytesSync(ZipEncoder().encode(archive));

    expect(
      () => repository.previewPackage(package.path, install),
      throwsA(isA<StateError>()),
    );
  });

  test('parses registry and detects legacy RoboPatch-style mods', () async {
    final legacyRoot = Directory(p.join(gameRoot.path, 'Mods'))..createSync();
    File(p.join(legacyRoot.path, 'LegacyPrompt.dll')).writeAsStringSync('dll');
    final manifestLegacy = Directory(p.join(legacyRoot.path, 'ManifestLegacy'))
      ..createSync();
    File(
      p.join(manifestLegacy.path, 'robotopia.mod.json'),
    ).writeAsStringSync(jsonEncode(_manifestJson('manifest.legacy', '1.0.0')));

    final snapshot = await repository.loadSnapshot();

    expect(snapshot.registryMods.single.manifest.id, 'registry.sample');
    expect(snapshot.legacyMods.map((mod) => mod.id), contains('LegacyPrompt'));
    expect(
      snapshot.legacyMods
          .singleWhere((mod) => mod.id == 'manifest.legacy')
          .canMigrate,
      isTrue,
    );
  });

  test('derives the catalog from dist packages, keeping latest per id', () async {
    final dist = Directory(p.join(repoRoot.path, 'dist'));
    // A newer build of the same mod supersedes the fixture's 1.0.0 in the listing.
    _writeDistPackage(dist, id: 'registry.sample', version: '2.0.0');
    _writeDistPackage(dist, id: 'other.mod', version: '0.3.0');
    // A malformed file must be skipped, not break the whole catalog.
    File(
      p.join(dist.path, 'broken.robotopiamod'),
    ).writeAsStringSync('not a zip');

    final snapshot = await repository.loadSnapshot();
    final byId = {
      for (final mod in snapshot.registryMods) mod.manifest.id: mod,
    };

    expect(byId.keys, containsAll(['registry.sample', 'other.mod']));
    expect(byId['registry.sample']!.manifest.version, '2.0.0');
    expect(byId['other.mod']!.manifest.version, '0.3.0');
    // The listing carries a computed sha and a file URL derived from the package itself.
    expect(byId['other.mod']!.packageSha256, isNotEmpty);
    expect(byId['other.mod']!.downloadUrl, startsWith('file:'));
  });

  test('installs transitive dependencies from package sources', () async {
    final install = await repository.selectGameDirectory(gameRoot.path);
    final dependencyPackage = _createPackage(
      root,
      id: 'dependency.mod',
      version: '1.0.0',
    );
    final dependencySha = sha256Of(dependencyPackage);
    final sourceFile = File(p.join(root.path, 'source.json'))
      ..writeAsStringSync(
        jsonEncode({
          'packages': {
            'dependency.mod': {
              'name': 'Dependency',
              'versions': {
                '1.0.0': {
                  ..._manifestJson('dependency.mod', '1.0.0'),
                  'url': dependencyPackage.uri.toString(),
                  'zipSHA256': dependencySha,
                },
              },
            },
          },
        }),
      );
    await repository.savePackageSources([
      PackageSource(
        id: 'test.source',
        name: 'Test Source',
        url: sourceFile.uri.toString(),
      ),
    ]);

    final rootPackage = _createPackage(
      root,
      id: 'main.mod',
      version: '1.0.0',
      dependencies: [
        {'id': 'dependency.mod', 'versionRange': '>=1.0.0'},
      ],
    );

    final plan = await repository.previewPackage(rootPackage.path, install);
    expect(plan.hasBlockingIssues, isFalse);
    expect(plan.installActions.map((action) => action.modId), [
      'dependency.mod',
      'main.mod',
    ]);

    final mods = await repository.installPackage(rootPackage.path, install);
    expect(
      mods.map((mod) => mod.id),
      containsAll(['dependency.mod', 'main.mod']),
    );
  });

  test('adds installed manifest gamemodes to world catalog', () async {
    final install = await repository.selectGameDirectory(gameRoot.path);
    final package = _createPackage(
      root,
      id: 'mode.mod',
      version: '1.0.0',
      worldGamemodes: [
        {
          'id': 'mode.mod.survival',
          'name': 'Survival',
          'description': 'Static gamemode metadata.',
        },
      ],
    );

    await repository.installPackage(package.path, install);
    final snapshot = await repository.loadSnapshot();

    expect(
      snapshot.worldCatalog.gamemodes.map((mode) => mode.id),
      contains('mode.mod.survival'),
    );
  });

  test('adds installed registry gamemodes to world catalog', () async {
    final install = await repository.selectGameDirectory(gameRoot.path);
    final package = _createPackage(
      root,
      id: 'registry.sample',
      version: '1.0.0',
    );

    await repository.installPackage(package.path, install);
    final snapshot = await repository.loadSnapshot();

    expect(
      snapshot.worldCatalog.gamemodes.map((mode) => mode.id),
      contains('registry.sample.survival'),
    );
  });

  test('creates diagnostic bundle with load order data', () async {
    final install = await repository.selectGameDirectory(gameRoot.path);
    final package = _createPackage(root, id: 'diag.mod', version: '1.0.0');
    final mods = await repository.installPackage(package.path, install);
    final resolution = const DependencyPlanner().resolveInstalled(mods);

    final bundle = await repository.createDiagnosticBundle(install, resolution);

    expect(File(bundle.path).existsSync(), isTrue);
    expect(bundle.includedFiles, contains('summary.json'));
    expect(bundle.includedFiles, contains('load-order.json'));
  });

  test('persists launcher update settings', () async {
    await repository.saveLauncherUpdateSettings(
      const LauncherUpdateSettings(
        enabled: true,
        checkAutomatically: false,
        channel: LauncherUpdateChannel.nightly,
      ),
    );

    final snapshot = await repository.loadSnapshot();

    expect(snapshot.launcherUpdates.enabled, isTrue);
    expect(snapshot.launcherUpdates.checkAutomatically, isFalse);
    expect(snapshot.launcherUpdates.channel, LauncherUpdateChannel.nightly);
  });
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
      'Robotopia.ModManager',
      'bin',
      'Release',
      'netstandard2.1',
    ),
  )..createSync(recursive: true);
  for (final dll in [
    'Robotopia.ModManager.dll',
    'Robotopia.ModManager.Core.dll',
    'Robotopia.Mods.Abstractions.dll',
    'Robotopia.Mods.UnityUi.dll',
  ]) {
    File(p.join(loader.path, dll)).writeAsStringSync('');
  }
}

// The built-in local source derives its catalog from the .robotopiamod packages in dist/, so the
// fixture publishes a real package there rather than a hand-written registry document.
void _createRegistry(Directory repoRoot) {
  final dist = Directory(p.join(repoRoot.path, 'dist'))
    ..createSync(recursive: true);
  final archive = Archive()
    ..addFile(
      ArchiveFile.string(
        'robotopia.mod.json',
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
    p.join(dist.path, 'registry.sample-1.0.0.robotopiamod'),
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
        'robotopia.mod.json',
        jsonEncode(_manifestJson(id, version)),
      ),
    )
    ..addFile(ArchiveFile.string('${_assemblyName(id)}.dll', 'dll'));
  File(
    p.join(dist.path, '$id-$version.robotopiamod'),
  ).writeAsBytesSync(ZipEncoder().encode(archive));
}

File _createPackage(
  Directory root, {
  required String id,
  required String version,
  List<Map<String, Object?>> dependencies = const [],
  List<Map<String, Object?>> worldGamemodes = const [],
  List<String> apiAssemblies = const [],
}) {
  final package = File(p.join(root.path, '$id-$version.robotopiamod'));
  final archive = Archive()
    ..addFile(
      ArchiveFile.string(
        'robotopia.mod.json',
        jsonEncode(
          _manifestJson(
            id,
            version,
            dependencies: dependencies,
            worldGamemodes: worldGamemodes,
            apiAssemblies: apiAssemblies,
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
}) => {
  'schemaVersion': 2,
  'name': id,
  'displayName': id,
  'version': version,
  'author': {'name': 'QuantumWorks'},
  'entryAssembly': '${_assemblyName(id)}.dll',
  'entryType': '$id.Entry',
  if (dependencies.isNotEmpty)
    'vpmDependencies': {
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
