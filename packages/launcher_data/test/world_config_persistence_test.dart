import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  test('launch preserves runtime-owned world config fields', () async {
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

    final configFile =
        File(
          p.join(
            gameRoot.path,
            'BepInEx',
            'TopiaForge',
            'config',
            'topiaforge.worlds.json',
          ),
        )..writeAsStringSync(
          jsonEncode({
            'selectedWorldId': 'old-world',
            'endSessionOnMenuScene': false,
            'interceptPauseMenu': false,
            'futureRuntimeOption': {'enabled': true},
          }),
        );

    final result = await repository.launch(
      install,
      const LauncherProfile(
        id: 'world-config-test',
        name: 'World Config Test',
        worldSelection: WorldSelection(
          worldId: 'new-world',
          gamemodeId: 'new-mode',
        ),
      ),
    );
    expect(result.started, isTrue);

    final config =
        jsonDecode(configFile.readAsStringSync()) as Map<String, Object?>;
    expect(config['selectedWorldId'], 'new-world');
    expect(config['selectedGamemodeId'], 'new-mode');
    expect(config['endSessionOnMenuScene'], isFalse);
    expect(config['interceptPauseMenu'], isFalse);
    expect(config['futureRuntimeOption'], {'enabled': true});
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
  for (final name in [
    'TopiaForge.ModManager.dll',
    'TopiaForge.ModManager.Core.dll',
    'TopiaForge.Mods.Abstractions.dll',
    'TopiaForge.Mods.UnityUi.dll',
  ]) {
    File(p.join(loader.path, name)).writeAsStringSync('');
  }
}
