import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

/// The macOS app-bundle layout resolves off the on-disk shape of the install,
/// not the host OS, so the full detect → validate → repair cycle is
/// exercisable on any development machine.
void main() {
  late Directory root;
  late Directory repoRoot;
  late Directory launcherDir;
  late LocalLauncherRepository repository;

  setUp(() {
    root = Directory.systemTemp.createTempSync('mac_layout_repo_test');
    repoRoot = Directory(p.join(root.path, 'repo'))..createSync();
    launcherDir = Directory(p.join(root.path, 'launcher'))..createSync();
    _createMacGame(launcherDir);
    File(
      p.join(launcherDir.path, 'installed-build.json'),
    ).writeAsStringSync('{"id":"2227"}');
    _createRuntimeSources(repoRoot);
    repository = LocalLauncherRepository(
      dataRoot: p.join(root.path, 'data'),
      repositoryRoot: repoRoot.path,
      knownGamePath: launcherDir.path,
    );
  });

  tearDown(() {
    if (root.existsSync()) {
      root.deleteSync(recursive: true);
    }
  });

  test('detects and validates a mac app-bundle install', () async {
    final install = await repository.detectKnownInstall();
    expect(install, isNotNull);
    expect(install!.layout, GameInstallLayout.macAppBundle);
    expect(
      install.executablePath,
      p.join(
        launcherDir.resolveSymbolicLinksSync(),
        'Robotopia.app',
        'Contents',
        'MacOS',
        'robotopia',
      ),
    );
    expect(install.canLaunch, isTrue);
    // The bundle fixture includes the managed assemblies, so no warning.
    expect(install.issues.where((issue) => issue.isBlocking), isEmpty);
    expect(install.bepInExStatus, ComponentState.missing);
    expect(install.gameVersion, '0.0.2227');
    expect(install.gameVersionLabel, 'build 2227');
  });

  test('prefers a bundle marker over the launcher-directory marker', () async {
    File(
      p.join(launcherDir.path, 'Robotopia.app', 'installed-build.json'),
    ).writeAsStringSync('{"id":2228}');

    final install = await repository.detectKnownInstall();

    expect(install?.gameVersion, '0.0.2228');
  });

  test('repair installs the macOS BepInEx bundle beside the app', () async {
    final install = (await repository.detectKnownInstall())!;
    final report = await repository.installOrRepairRuntime(install);

    expect(report.ok, isTrue, reason: report.issues.join('; '));
    expect(
      File(p.join(launcherDir.path, 'run_bepinex.sh')).existsSync(),
      isTrue,
    );
    expect(
      File(p.join(launcherDir.path, 'libdoorstop.dylib')).existsSync(),
      isTrue,
    );
    expect(
      File(
        p.join(launcherDir.path, 'BepInEx', 'core', 'BepInEx.dll'),
      ).existsSync(),
      isTrue,
    );

    final refreshed = (await repository.detectKnownInstall())!;
    expect(refreshed.bepInExStatus, ComponentState.ready);
    expect(refreshed.loaderStatus, ComponentState.ready);
  });
}

void _createMacGame(Directory launcherDir) {
  final bundle = Directory(p.join(launcherDir.path, 'Robotopia.app'));
  File(
    p.join(bundle.path, 'Contents', 'MacOS', 'robotopia'),
  ).createSync(recursive: true);
  File(
    p.join(
      bundle.path,
      'Contents',
      'Resources',
      'Data',
      'Managed',
      'UnityEngine.dll',
    ),
  ).createSync(recursive: true);
}

void _createRuntimeSources(Directory repoRoot) {
  final bepinex = Directory(
    p.join(repoRoot.path, 'third_party', 'BepInEx', 'macos_universal_5.4.23.5'),
  )..createSync(recursive: true);
  File(p.join(bepinex.path, 'run_bepinex.sh')).writeAsStringSync('#!/bin/sh\n');
  File(p.join(bepinex.path, 'libdoorstop.dylib')).writeAsStringSync('');
  File(
    p.join(bepinex.path, 'BepInEx', 'core', 'BepInEx.dll'),
  ).createSync(recursive: true);

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
