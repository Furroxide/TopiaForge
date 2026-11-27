part of 'launcher_data_test.dart';

/// When the launcher may retire a pending restart, and when it must leave one standing.
void _registerRestartRequirementTests({
  required Directory Function() root,
  required Directory Function() gameRoot,
  required LocalLauncherRepository Function() repository,
  required void Function(bool) setGameRunning,
  required void Function(bool) setProbeThrows,
}) {
  test('clears the restart requirement when the game is not running', () async {
    final install = await repository().selectGameDirectory(gameRoot().path);
    final package = _createPackage(root(), id: 'alpha.mod', version: '1.0.0');
    await repository().installPackage(package.path, install);

    setGameRunning(true);
    var mods = await repository().setModEnabled(install, 'alpha.mod', false);
    expect(mods.single.restartRequired, isTrue);

    setGameRunning(false);
    mods = await repository().setModEnabled(install, 'alpha.mod', true);
    expect(
      mods.single.restartRequired,
      isFalse,
      reason: 'With no loader alive there is no stale state to restart past.',
    );
  });

  test(
    'an install records its restart requirement regardless of the probe',
    () async {
      final install = await repository().selectGameDirectory(gameRoot().path);
      final package = _createPackage(root(), id: 'alpha.mod', version: '1.0.0');

      // The install receipt in state.json is read back by the release scaffold
      // validator, so it must not depend on whether a game process happened to be
      // running while the CLI installed. Retiring the flag is the next read's job.
      setGameRunning(false);
      final mods = await repository().installPackage(package.path, install);
      expect(
        mods.single.restartRequired,
        isTrue,
        reason: 'a requirement this operation just wrote is not stale',
      );
    },
  );

  test('retires a restart requirement left by an external exit', () async {
    final install = await repository().selectGameDirectory(gameRoot().path);
    final package = _createPackage(root(), id: 'alpha.mod', version: '1.0.0');
    await repository().installPackage(package.path, install);

    setGameRunning(true);
    var mods = await repository().setModEnabled(install, 'alpha.mod', false);
    expect(mods.single.restartRequired, isTrue);

    // The player closes the game outside the launcher, so nothing rewrites
    // the flag: only a reconciling read can retire it.
    setGameRunning(false);
    final snapshot = await repository().loadSnapshot();
    expect(snapshot.installedMods.single.restartRequired, isFalse);
  });

  test('keeps the restart requirement when the probe cannot tell', () async {
    final install = await repository().selectGameDirectory(gameRoot().path);
    final package = _createPackage(root(), id: 'alpha.mod', version: '1.0.0');
    await repository().installPackage(package.path, install);

    setGameRunning(true);
    var mods = await repository().setModEnabled(install, 'alpha.mod', false);
    expect(mods.single.restartRequired, isTrue);

    setProbeThrows(true);
    final snapshot = await repository().loadSnapshot();
    expect(
      snapshot.installedMods.single.restartRequired,
      isTrue,
      reason: 'A probe that failed must not be read as "looked, found none".',
    );
  });

  test('leaves uninstall-pending mods their restart requirement', () async {
    final install = await repository().selectGameDirectory(gameRoot().path);
    final package = _createPackage(root(), id: 'alpha.mod', version: '1.0.0');
    await repository().installPackage(package.path, install);

    // Only the in-game path stages an uninstall, so write the state the
    // runtime would have left behind.
    final stateFile = File(
      p.join(gameRoot().path, 'BepInEx', 'TopiaForge', 'state.json'),
    );
    final state =
        jsonDecode(stateFile.readAsStringSync()) as Map<String, Object?>;
    for (final item in (state['mods'] as List).whereType<Map>()) {
      item['restartRequired'] = true;
      item['uninstallPending'] = true;
    }
    stateFile.writeAsStringSync(jsonEncode(state));

    setGameRunning(false);
    final snapshot = await repository().loadSnapshot();
    expect(
      snapshot.installedMods.single.restartRequired,
      isTrue,
      reason: 'Package removal really is deferred to the next game start.',
    );
  });
}
