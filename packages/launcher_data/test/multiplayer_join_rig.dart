/// One game, one launcher data folder and a repository over them, for the
/// join content tests, with views of what the player's own setup loads.
library;

import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'multiplayer_join_content_fixtures.dart';
import 'open_handle_support.dart';

/// What the player's own setup holds and loads: the manager records, the
/// rest of the manager state, the stored profiles that are not server
/// profiles, what each of those profiles loads, and the Mods list.
final class PlayerView {
  const PlayerView({
    required this.records,
    required this.stateRest,
    required this.profiles,
    required this.loads,
    required this.mods,
  });

  final Map<String, String> records;
  final String stateRest;
  final Map<String, String> profiles;
  final Map<String, String> loads;
  final List<String> mods;
}

final class JoinRig {
  JoinRig._(this.root, this.game, this.dataRoot, this.repositoryRoot);

  final Directory root;
  final Directory game;
  final String dataRoot;
  final Directory repositoryRoot;
  late LocalLauncherRepository repository;
  late GameInstall install;

  /// A rig with no game running. [withRuntime] repairs a runtime into the
  /// game, so profiles can launch. [ownedGameQuit] is how an install closes
  /// a game the repository started; tests pass one whose pause returns at
  /// once.
  static Future<JoinRig> create({
    JoinInstallHook? hook,
    GameRunningProbe? probe,
    GameProcessCreator? creator,
    GameProcessLiveness? liveness,
    GameProcessStopper? stopper,
    OwnedGameQuitPolicy? ownedGameQuit,
    bool withRuntime = false,
  }) async {
    final root = Directory(
      Directory.systemTemp
          .createTempSync('tf-join-')
          .resolveSymbolicLinksSync(),
    );
    addTearDown(() {
      if (root.existsSync()) root.deleteSync(recursive: true);
    });
    // Runs once every repository the rig opened is closed.
    addTearDown(() => expectNothingOpenUnder(root));
    final rig = JoinRig._(
      root,
      createTestGame(root),
      p.join(root.path, 'data'),
      Directory(p.join(root.path, 'repo'))..createSync(),
    );
    if (withRuntime) _createRuntimeSources(rig.repositoryRoot);
    rig.repository = rig.open(
      hook: hook,
      probe: probe,
      creator: creator,
      liveness: liveness,
      stopper: stopper,
      ownedGameQuit: ownedGameQuit,
    );
    rig.install = await rig.repository.selectGameDirectory(rig.game.path);
    if (withRuntime) {
      final repair = await rig.repository.installOrRepairRuntime(rig.install);
      expect(repair.ok, isTrue, reason: 'the test runtime installs');
      rig.install = await rig.repository.selectGameDirectory(rig.game.path);
      final settingsFile = File(p.join(rig.dataRoot, 'settings.json'));
      final settings =
          jsonDecode(settingsFile.readAsStringSync()) as Map<String, Object?>;
      // A Windows build on Linux or macOS starts under this command.
      settings['wineCommand'] = Platform.resolvedExecutable;
      settingsFile.writeAsStringSync(jsonEncode(settings));
    }
    return rig;
  }

  /// Another repository over the same folders, as the next launcher start.
  LocalLauncherRepository open({
    JoinInstallHook? hook,
    GameRunningProbe? probe,
    GameProcessCreator? creator,
    GameProcessLiveness? liveness,
    GameProcessStopper? stopper,
    OwnedGameQuitPolicy? ownedGameQuit,
  }) {
    final opened = LocalLauncherRepository(
      dataRoot: dataRoot,
      repositoryRoot: repositoryRoot.path,
      packageMetadataValidator: (_) async => const [],
      gameRunningProbe: probe ?? (_) async => false,
      gameProcessCreator: creator,
      gameProcessLiveness: liveness,
      gameProcessStopper: stopper,
      ownedGameQuit: ownedGameQuit ?? const OwnedGameQuitPolicy(),
      joinInstallHook: hook,
    );
    addTearDown(opened.dispose);
    return opened;
  }

  /// Installs [archive] as the player's own, the way Mods does.
  Future<void> installOwn(TestArchive archive) async {
    final folder = Directory(p.join(root.path, 'own'))..createSync();
    await repository.installPackage(archive.writeTo(folder).path, install);
  }

  /// Puts [archives] in the package cache, as a finished download stage
  /// leaves them.
  void cache(Iterable<TestArchive> archives) {
    for (final archive in archives) {
      cachedArchive(dataRoot, archive)
        ..parent.createSync(recursive: true)
        ..writeAsBytesSync(archive.bytes);
    }
  }

  File get profilesFile => File(p.join(dataRoot, 'profiles.json'));

  /// The stored profiles, each as canonical text, by id.
  Map<String, String> storedProfiles({bool servers = false}) {
    if (!profilesFile.existsSync()) return const {};
    final document =
        jsonDecode(profilesFile.readAsStringSync()) as Map<String, Object?>;
    return {
      for (final entry in (document['profiles'] as List).cast<Map>())
        if (servers ==
            ServerProfiles.isServerProfile(
              LauncherProfile.fromJson(entry.cast<String, Object?>()),
            ))
          entry['id'] as String: canonicalText(entry),
    };
  }

  Future<PlayerView> playerView() async {
    final snapshot = await repository.loadSnapshot();
    final state = managerStateFile(game).existsSync()
        ? jsonDecode(managerStateFile(game).readAsStringSync())
              as Map<String, Object?>
        : <String, Object?>{};
    return PlayerView(
      records: managerRecords(game),
      stateRest: canonicalText({...state}..remove('mods')),
      profiles: storedProfiles(),
      loads: {
        for (final profile in snapshot.profiles)
          if (!ServerProfiles.isServerProfile(profile))
            profile.id: _loads(snapshot.previewsByProfile[profile.id]!),
      },
      mods: [
        for (final mod in snapshot.installedMods)
          '${mod.id}@${mod.version} ${mod.enabled ? 'on' : 'off'} '
              '${mod.installedVersions.map((item) => item.version).join('|')}',
      ],
    );
  }

  /// Everything a failed or undone join must leave as it found it: the
  /// player's view, the record of server content, and the folders of
  /// [archives].
  Future<Map<String, Object>> everything(List<TestArchive> archives) async {
    final view = await playerView();
    final record = serverContentFile(game).existsSync()
        ? ServerContentRecord.read(serverContentFile(game).readAsBytesSync())
        : null;
    return {
      'records': view.records,
      'stateRest': view.stateRest,
      'profiles': view.profiles,
      'loads': view.loads,
      'mods': view.mods,
      'record': canonicalText([
        for (final entry
            in record?.record?.entries ?? const <ServerContentEntry>[])
          entry.toJson(),
      ]),
      'added': [...?record?.record?.addedRecords],
      'folders': [
        for (final archive in archives)
          packageFolder(game, archive.id, archive.version).existsSync(),
      ],
    };
  }

  /// What [profile] would load now.
  Future<List<String>> loadsOf(LauncherProfile profile) async {
    final preview = await repository.previewLaunch(install, profile);
    return [
      for (final package in preview.packages)
        '${package.id}@${package.version}',
    ];
  }
}

String _loads(LaunchPreview preview) => canonicalText({
  'digest': preview.packageDigest,
  'packages': [for (final package in preview.packages) package.toJson()],
  'issues': [for (final issue in preview.issues) issue.toJson()],
});

void _createRuntimeSources(Directory repositoryRoot) {
  final bepinex = Directory(
    p.join(repositoryRoot.path, 'third_party', 'BepInEx', 'win_x64_5.4.23.5'),
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
      repositoryRoot.path,
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

/// Sets up the player of [StandardJoin] in [rig]: Alpha 1.0.0 on, Beta
/// 1.0.0 off, Gamma 1.0.0, and three profiles of their own.
Future<void> preparePlayer(JoinRig rig) async {
  for (final archive in [
    StandardJoin.alpha,
    StandardJoin.beta,
    StandardJoin.gamma,
  ]) {
    await rig.installOwn(archive);
  }
  await rig.repository.setModEnabled(rig.install, 'own.beta', false);
  await rig.repository.saveProfiles([
    LauncherProfile.defaultProfile(),
    const LauncherProfile(
      id: 'explicit',
      name: 'Explicit',
      enabledMods: {'own.alpha', 'own.gamma'},
      selectedVersions: {'own.gamma': '1.0.0'},
    ),
    const LauncherProfile(
      id: 'unpinned',
      name: 'Unpinned',
      enabledMods: {'own.alpha', 'own.beta'},
    ),
  ], 'default');
}
