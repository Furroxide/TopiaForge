import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'tomato_cake_launcher_fixture.dart';

/// Discovery and build detection for a game the official Tomato Cake
/// launcher moved, observed with build 2545: `launcher-config.json` names the
/// new parent, the game and its current `filelist.json` move there, and
/// `installed-build.json` stays in the launcher's state directory beside a
/// stale `filelist.json`.
void main() {
  final currentVersion = TopiaForgeRuntimeVersions.gameVersion;
  final currentBuildId = RobotopiaGameVersion.tryBuildId(currentVersion)!;
  final staleBuildId = currentBuildId - 1;
  final staleVersion = RobotopiaGameVersion.tryFromBuildId(staleBuildId)!;

  late TomatoCakeFixture fixture;
  late LocalLauncherRepository repository;

  LocalLauncherRepository createRepository({String hostPlatform = 'windows'}) {
    final created = LocalLauncherRepository(
      dataRoot: p.join(fixture.root.path, 'data'),
      repositoryRoot: p.join(fixture.root.path, 'repo'),
      gameInstallDiscoveryService: GameInstallDiscoveryService.standard(
        environment: fixture.environment,
        hostPlatform: hostPlatform,
        steamRoots: const [],
      ),
      tomatoCakeLauncherState: fixture.launcherState(
        hostPlatform: hostPlatform,
      ),
    );
    addTearDown(created.dispose);
    return created;
  }

  String issues(GameInstall install) =>
      install.issues.map((issue) => issue.message).join(' ');

  setUp(() {
    fixture = TomatoCakeFixture.create();
    repository = createRepository();
  });
  tearDown(() => fixture.dispose());

  test('default layout keeps the marker beside the game', () async {
    createWindowsGame(fixture.defaultGameRoot);
    fixture.writeStateMarker(currentBuildId);

    final candidates = await repository.discoverGameInstalls();

    expect(candidates, hasLength(1));
    expect(candidates.single.install.path, fixture.defaultGameRoot.path);
    expect(candidates.single.primarySource.id, 'tomato-cake');
    expect(candidates.single.install.gameVersion, currentVersion);
  });

  test('relocated layout is discovered with the launcher marker', () async {
    createWindowsGame(fixture.relocatedGameRoot);
    File(
      p.join(fixture.relocatedParent.path, 'filelist.json'),
    ).writeAsStringSync('{"version":1,"root":"Robotopia","files":[]}');
    fixture.writeStateMarker(currentBuildId);
    File(
      p.join(fixture.stateDirectory.path, 'filelist.json'),
    ).writeAsStringSync('stale manifest from an earlier build');
    fixture.writeGameDirectory(fixture.relocatedParent.path);

    final candidates = await repository.discoverGameInstalls();

    expect(candidates, hasLength(1));
    final install = candidates.single.install;
    expect(install.path, fixture.relocatedGameRoot.path);
    expect(candidates.single.primarySource.id, 'tomato-cake');
    expect(install.gameVersion, currentVersion);
    expect(issues(install), isNot(contains('build metadata')));
    expect((await repository.detectKnownInstall())?.path, install.path);
  });

  test('both layouts are offered while the default copy remains', () async {
    createWindowsGame(fixture.defaultGameRoot);
    createWindowsGame(fixture.relocatedGameRoot);
    fixture.writeStateMarker(currentBuildId);
    fixture.writeGameDirectory(fixture.relocatedParent.path);

    final candidates = await repository.discoverGameInstalls();

    expect(
      candidates.map((candidate) => candidate.install.path),
      unorderedEquals([
        fixture.defaultGameRoot.path,
        fixture.relocatedGameRoot.path,
      ]),
    );
  });

  test(
    'a mismatched game_dir never lends its marker to another install',
    () async {
      fixture.relocatedParent.createSync(recursive: true);
      final elsewhere = Directory(
        p.join(fixture.root.path, 'Elsewhere', 'Robotopia'),
      );
      createWindowsGame(elsewhere);
      fixture.writeStateMarker(currentBuildId);
      fixture.writeGameDirectory(fixture.relocatedParent.path);

      expect(await repository.discoverGameInstalls(), isEmpty);
      final install = await repository.selectGameDirectory(elsewhere.path);

      expect(install.gameVersion, isNull);
      expect(issues(install), contains('build metadata is missing'));
    },
  );

  test('a marker beside the game outranks a stale launcher marker', () async {
    createWindowsGame(fixture.relocatedGameRoot);
    fixture.writeStateMarker(staleBuildId);
    fixture.writeGameDirectory(fixture.relocatedParent.path);

    var install = await repository.selectGameDirectory(
      fixture.relocatedGameRoot.path,
    );
    expect(install.gameVersion, staleVersion);

    final beside = File(
      p.join(fixture.relocatedParent.path, 'installed-build.json'),
    )..writeAsStringSync('{"id":$currentBuildId}');
    install = await repository.selectGameDirectory(
      fixture.relocatedGameRoot.path,
    );
    expect(install.gameVersion, currentVersion);

    // A higher-priority marker that exists must fail closed rather than let
    // the launcher's marker stand in for it.
    beside.writeAsStringSync('{"id":0}');
    install = await repository.selectGameDirectory(
      fixture.relocatedGameRoot.path,
    );
    expect(install.gameVersion, isNull);
    expect(issues(install), contains('invalid or unreadable'));
  });

  test('a malformed config offers no relocation and lends no marker', () async {
    createWindowsGame(fixture.relocatedGameRoot);
    fixture.writeStateMarker(currentBuildId);
    final value = jsonEncode(fixture.relocatedParent.path);
    fixture.writeConfig('{"game_dir":$value,"game_dir":$value}');

    expect(await repository.discoverGameInstalls(), isEmpty);
    final install = await repository.selectGameDirectory(
      fixture.relocatedGameRoot.path,
    );

    expect(install.gameVersion, isNull);
    expect(issues(install), contains('build metadata is missing'));
  });

  test('other hosts leave their layouts unchanged', () async {
    createWindowsGame(fixture.relocatedGameRoot);
    fixture.writeStateMarker(currentBuildId);
    fixture.writeGameDirectory(fixture.relocatedParent.path);
    final mac = createRepository(hostPlatform: 'macos');

    expect(await mac.discoverGameInstalls(), isEmpty);
    final install = await mac.selectGameDirectory(
      fixture.relocatedGameRoot.path,
    );
    expect(install.gameVersion, isNull);
  });
}
