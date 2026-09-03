import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  // The launcher used to merge the player's selection into the Worlds mod's own config file. That
  // document is a {schemaVersion, value} envelope owned by the mod, the launcher wrote its keys
  // beside `value` rather than inside it, and the mod's next save deleted them -- so every game mode
  // anyone picked was silently discarded. The selection now rides the one-shot launch profile, and
  // the mod's config is none of the launcher's business.
  test('launch leaves the Worlds mod config alone and carries the intent on '
      'the launch profile', () async {
    final root = Directory.systemTemp.createTempSync('world-config-merge-');
    addTearDown(() => root.deleteSync(recursive: true));
    final gameRoot = Directory(p.join(root.path, 'TopiaForge'))..createSync();
    final repositoryRoot = Directory(p.join(root.path, 'repo'))..createSync();
    final dataRoot = Directory(p.join(root.path, 'data'));
    _createGame(gameRoot);
    _createRuntimeSources(repositoryRoot);
    final repository = LocalLauncherRepository(
      dataRoot: dataRoot.path,
      repositoryRoot: repositoryRoot.path,
      gameProcessStarter: (_) async => 42,
    );
    var install = await repository.selectGameDirectory(gameRoot.path);
    final repair = await repository.installOrRepairRuntime(install);
    expect(repair.ok, isTrue);
    install = await repository.selectGameDirectory(gameRoot.path);
    final settingsFile = File(p.join(dataRoot.path, 'settings.json'));
    final settings =
        jsonDecode(settingsFile.readAsStringSync()) as Map<String, Object?>;
    settings['wineCommand'] = 'synthetic-wine';
    settingsFile.writeAsStringSync(jsonEncode(settings));

    const untouched =
        '{"schemaVersion":2,"value":{"endSessionOnMenuScene":false,'
        '"interceptPauseMenu":false,"futureRuntimeOption":{"enabled":true}}}';
    final configFile = File(
      p.join(
        gameRoot.path,
        'BepInEx',
        'TopiaForge',
        'config',
        'topiaforge.worlds.json',
      ),
    )..writeAsStringSync(untouched);

    final result = await repository.launch(
      install,
      const LauncherProfile(
        id: 'world-config-test',
        name: 'World Config Test',
        worldSelection: WorldSelection(
          worldId: 'io.github.furroxide.topiaforge.worlds.open_sandbox',
          gamemodeId: 'io.github.furroxide.topiaforge.zombies.survival',
          launchIntoGamemode: true,
        ),
      ),
    );
    expect(result.started, isTrue);

    expect(
      configFile.readAsStringSync(),
      untouched,
      reason: 'the mod owns this document; the launcher must not rewrite it',
    );

    final profileFile =
        Directory(
          p.join(gameRoot.path, 'BepInEx', 'TopiaForge', 'staging'),
        ).listSync().whereType<File>().singleWhere(
          (entry) => p.basename(entry.path).startsWith('launch-profile-'),
        );
    final profile =
        jsonDecode(profileFile.readAsStringSync()) as Map<String, Object?>;
    final intent = profile['worldLaunch'] as Map<String, Object?>;
    expect(intent['command'], WorldSelection.launchTargetCommand);
    expect(
      intent['gamemodeId'],
      'io.github.furroxide.topiaforge.zombies.survival',
    );
    expect(
      intent['worldId'],
      'io.github.furroxide.topiaforge.worlds.open_sandbox',
    );
  });

  test('a profile that only remembers a world still boots normally', () async {
    final root = Directory.systemTemp.createTempSync('world-config-normal-');
    addTearDown(() => root.deleteSync(recursive: true));
    final gameRoot = Directory(p.join(root.path, 'TopiaForge'))..createSync();
    final repositoryRoot = Directory(p.join(root.path, 'repo'))..createSync();
    final dataRoot = Directory(p.join(root.path, 'data'));
    _createGame(gameRoot);
    _createRuntimeSources(repositoryRoot);
    final repository = LocalLauncherRepository(
      dataRoot: dataRoot.path,
      repositoryRoot: repositoryRoot.path,
      gameProcessStarter: (_) async => 42,
    );
    var install = await repository.selectGameDirectory(gameRoot.path);
    expect((await repository.installOrRepairRuntime(install)).ok, isTrue);
    install = await repository.selectGameDirectory(gameRoot.path);
    // Starting a Windows executable off Windows needs a wine command, or the
    // launch reports "not started" for a reason that has nothing to do with
    // the intent this test is about.
    final settingsFile = File(p.join(dataRoot.path, 'settings.json'));
    final settings =
        jsonDecode(settingsFile.readAsStringSync()) as Map<String, Object?>;
    settings['wineCommand'] = 'synthetic-wine';
    settingsFile.writeAsStringSync(jsonEncode(settings));

    final result = await repository.launch(
      install,
      const LauncherProfile(
        id: 'remembers-only',
        name: 'Remembers only',
        worldSelection: WorldSelection(
          worldId: 'io.github.furroxide.topiaforge.worlds.open_sandbox',
          gamemodeId: 'io.github.furroxide.topiaforge.zombies.survival',
        ),
      ),
    );
    expect(result.started, isTrue);

    final profileFile =
        Directory(
          p.join(gameRoot.path, 'BepInEx', 'TopiaForge', 'staging'),
        ).listSync().whereType<File>().singleWhere(
          (entry) => p.basename(entry.path).startsWith('launch-profile-'),
        );
    final profile =
        jsonDecode(profileFile.readAsStringSync()) as Map<String, Object?>;
    expect(
      profile['worldLaunch'],
      {'command': WorldSelection.mainMenuCommand},
      reason:
          'remembering a world is not asking to launch into it, and the '
          'manager must be told so rather than left to its own memory',
    );
  });
}

void _createGame(Directory gameRoot) {
  final executable = File(p.join(gameRoot.path, 'Robotopia.exe'));
  if (Platform.isWindows) {
    File(Platform.resolvedExecutable).copySync(executable.path);
  } else {
    executable.writeAsStringSync('');
  }
  Directory(
    p.join(gameRoot.path, 'Robotopia_Data', 'Managed'),
  ).createSync(recursive: true);
  File(
    p.join(gameRoot.path, 'Robotopia_Data', 'Managed', 'UnityEngine.dll'),
  ).writeAsStringSync('');
}

void _createRuntimeSources(Directory repositoryRoot) {
  final bepInEx = Directory(
    p.join(repositoryRoot.path, 'third_party', 'BepInEx', 'win_x64_5.4.23.5'),
  )..createSync(recursive: true);
  File(p.join(bepInEx.path, 'winhttp.dll')).writeAsStringSync('');
  File(p.join(bepInEx.path, 'doorstop_config.ini')).writeAsStringSync('');
  Directory(
    p.join(bepInEx.path, 'BepInEx', 'core'),
  ).createSync(recursive: true);
  File(
    p.join(bepInEx.path, 'BepInEx', 'core', 'BepInEx.dll'),
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
  for (final name in topiaForgeRuntimeLoaderDlls) {
    File(p.join(loader.path, name)).writeAsStringSync('');
  }
}
