import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:crypto/crypto.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

part 'launcher_data_test_helpers.dart';
part 'installed_build_provenance_test_part.dart';
part 'launcher_data_diagnostics_test_part.dart';
part 'devtool_installation_test_part.dart';
part 'profile_launch_test_part.dart';
part 'runtime_repair_security_test_part.dart';
part 'runtime_loader_payload_test_part.dart';
part 'restart_requirement_test_part.dart';
part 'world_catalog_test_part.dart';

void main() {
  late Directory root;
  late Directory dataRoot;
  late Directory repoRoot;
  late Directory gameRoot;
  late LocalLauncherRepository repository;
  late bool gameRunning;
  late bool probeThrows;

  setUp(() {
    root = Directory.systemTemp.createTempSync('topiaforge-launcher-data-');
    dataRoot = Directory(p.join(root.path, 'data'))..createSync();
    repoRoot = Directory(p.join(root.path, 'repo'))..createSync();
    gameRoot = Directory(p.join(root.path, 'TopiaForge'))..createSync();
    _createGame(gameRoot);
    _createRuntimeSources(repoRoot);
    _createRegistry(repoRoot);
    gameRunning = true;
    probeThrows = false;
    repository = LocalLauncherRepository(
      dataRoot: dataRoot.path,
      repositoryRoot: repoRoot.path,
      knownGamePath: gameRoot.path,
      packageMetadataValidator: _acceptPackageMetadata,
      gameRunningProbe: (_) async {
        if (probeThrows) {
          throw StateError('Process list unavailable.');
        }
        return gameRunning;
      },
    );
  });

  tearDown(() {
    if (root.existsSync()) {
      root.deleteSync(recursive: true);
    }
  });

  _registerDiagnosticDataTests(
    repository: () => repository,
    dataRoot: () => dataRoot,
    gameRoot: () => gameRoot,
  );
  _registerProfileLaunchTests(
    root: () => root,
    dataRoot: () => dataRoot,
    repositoryRoot: () => repoRoot,
    gameRoot: () => gameRoot,
  );
  _registerRuntimeRepairSecurityTests(
    repository: () => repository,
    repositoryRoot: () => repoRoot,
    gameRoot: () => gameRoot,
  );
  _registerRuntimeLoaderPayloadTests(
    repository: () => repository,
    gameRoot: () => gameRoot,
  );
  _registerDevToolInstallationTests(
    repository: () => repository,
    root: () => root,
    gameRoot: () => gameRoot,
  );
  _registerRestartRequirementTests(
    root: () => root,
    gameRoot: () => gameRoot,
    repository: () => repository,
    setGameRunning: (value) => gameRunning = value,
    setProbeThrows: (value) => probeThrows = value,
  );
  _registerWorldCatalogTests(
    root: () => root,
    gameRoot: () => gameRoot,
    repository: () => repository,
  );

  _registerInstalledBuildProvenanceTests(
    repository: () => repository,
    root: () => root,
    gameRoot: () => gameRoot,
  );

  test('detects known install and repairs BepInEx plus loader', () async {
    final install = await repository.detectKnownInstall();
    expect(install, isNotNull);
    expect(install!.bepInExStatus, ComponentState.missing);
    final blockedLog = Directory(p.join(dataRoot.path, 'logs', 'launcher.log'))
      ..createSync(recursive: true);

    final report = await repository.installOrRepairRuntime(install);
    expect(report.ok, isTrue);
    blockedLog.deleteSync();

    final repaired = await repository.selectGameDirectory(gameRoot.path);
    expect(repaired.bepInExStatus, ComponentState.ready);
    expect(repaired.loaderStatus, ComponentState.ready);
  });

  test(
    'launch attempts automatic repair before reporting repair failure',
    () async {
      final install = await repository.detectKnownInstall();
      expect(install, isNotNull);
      expect(install!.needsRepair, isTrue);

      Directory(
        p.join(repoRoot.path, 'src', 'TopiaForge.ModManager'),
      ).deleteSync(recursive: true);

      final result = await repository.launch(
        install,
        LauncherProfile.defaultProfile(),
      );

      expect(result.started, isFalse);
      expect(
        result.message,
        contains('Automatic runtime repair could not complete.'),
      );
      expect(result.message, contains('Built loader DLLs were not found.'));
      expect(File(p.join(gameRoot.path, 'winhttp.dll')).existsSync(), isFalse);
    },
  );

  test('marks loader partial when installed DLLs are stale', () async {
    final install = await repository.selectGameDirectory(gameRoot.path);
    final report = await repository.installOrRepairRuntime(install);
    expect(report.ok, isTrue);

    File(
      p.join(
        gameRoot.path,
        'BepInEx',
        'plugins',
        'TopiaForge.ModManager',
        'TopiaForge.Mods.Abstractions.dll',
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
          'TopiaForge.ModManager',
          'TopiaForge.Mods.UnityUi.dll',
        ),
      );
      expect(
        unityUi.existsSync(),
        isTrue,
        reason:
            'runtime repair must deploy the TopiaForge Unity UI kit beside the loader',
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

  test('re-enables disabled dependencies for dependent installs', () async {
    final install = await repository.selectGameDirectory(gameRoot.path);
    final dependencyPackage = _createPackage(
      root,
      id: 'dependency.mod',
      version: '1.0.0',
    );

    await repository.installPackage(dependencyPackage.path, install);
    var mods = await repository.setModEnabled(install, 'dependency.mod', false);
    expect(mods.single.enabled, isFalse);

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
    expect(plan.installActions.first.enableOnly, isTrue);

    mods = await repository.installPackage(rootPackage.path, install);
    final byId = {for (final mod in mods) mod.id: mod};
    expect(byId['dependency.mod']!.enabled, isTrue);
    expect(byId['main.mod']!.enabled, isTrue);

    final resolution = const DependencyPlanner().resolveInstalled(mods);
    expect(resolution.hasBlockingIssues, isFalse);
    expect(resolution.orderedMods.map((mod) => mod.id), [
      'dependency.mod',
      'main.mod',
    ]);
  });

  test('rejects zip traversal during preview', () async {
    final install = await repository.selectGameDirectory(gameRoot.path);
    final package = File(p.join(root.path, 'traversal.topiaforgemod'));
    final archive = Archive()
      ..addFile(ArchiveFile.string('../escape.txt', 'nope'))
      ..addFile(
        ArchiveFile.string(
          'topiaforge.mod.json',
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

  test('derives the catalog from dist packages, keeping latest per id', () async {
    final dist = Directory(p.join(repoRoot.path, 'dist'));
    // A newer build of the same mod supersedes the fixture's 1.0.0 in the listing.
    _writeDistPackage(dist, id: 'registry.sample', version: '2.0.0');
    _writeDistPackage(dist, id: 'other.mod', version: '0.3.0');
    // A malformed file must be skipped, not break the whole catalog.
    File(
      p.join(dist.path, 'broken.topiaforgemod'),
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

Future<List<String>> _acceptPackageMetadata(Directory _) async => const [];
