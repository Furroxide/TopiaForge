part of 'launcher_data_test.dart';

void _registerDevToolInstallationTests({
  required LocalLauncherRepository Function() repository,
  required Directory Function() root,
  required Directory Function() gameRoot,
}) {
  test(
    'installs DevTool packages disabled and preserves opt-in on update',
    () async {
      final install = await repository().selectGameDirectory(gameRoot().path);
      final firstPackage = _createPackage(
        root(),
        id: 'creator.tools',
        version: '1.0.0',
        category: 'DevTool',
      );
      final secondPackage = _createPackage(
        root(),
        id: 'creator.tools',
        version: '1.1.0',
        category: 'DevTool',
      );

      var mods = await repository().installPackage(firstPackage.path, install);
      expect(mods.single.enabled, isFalse);

      final stateFile = File(
        p.join(gameRoot().path, 'BepInEx', 'TopiaForge', 'state.json'),
      );
      final state =
          jsonDecode(stateFile.readAsStringSync()) as Map<String, dynamic>;
      final entries = (state['mods'] as List).cast<Map<String, dynamic>>();
      entries.single.remove('enabled');
      stateFile.writeAsStringSync(jsonEncode(state));
      mods = (await repository().loadSnapshot()).installedMods;
      expect(
        mods.single.enabled,
        isFalse,
        reason: 'legacy/fallback state must not silently enable a DevTool',
      );

      mods = await repository().setModEnabled(install, 'creator.tools', true);
      expect(mods.single.enabled, isTrue);

      mods = await repository().installPackage(secondPackage.path, install);
      expect(mods.single.version, '1.1.0');
      expect(mods.single.enabled, isTrue);
    },
  );
}
