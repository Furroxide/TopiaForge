part of 'topiaforge_cli_test.dart';

void _multiplayerCliTests(_CliTestHarness Function() currentHarness) {
  test('mod add multiplayer keeps V5 and scaffolds the contract lock', () async {
    final created = await currentHarness().runCli([
      'new',
      'mod',
      't.multiplayer',
      '--dir',
      currentHarness().temp.path,
    ]);
    expect(created.exitCode, 0, reason: '${created.stdout}\n${created.stderr}');
    final projectDir = p.join(currentHarness().temp.path, 't.multiplayer');

    final added = await currentHarness().runCli([
      'mod',
      'add',
      'multiplayer',
      '--project',
      projectDir,
    ]);
    expect(added.exitCode, 0, reason: '${added.stdout}\n${added.stderr}');

    final manifest =
        jsonDecode(
              File(
                p.join(projectDir, 'topiaforge.mod.json'),
              ).readAsStringSync(),
            )
            as Map<String, Object?>;
    expect(manifest['schemaVersion'], 5);
    expect(manifest['multiplayer'], {
      'mode': 'session',
      'presence': 'required',
      'protocol': {'version': '1.0.0', 'peerVersionRange': '>=1.0.0 <2.0.0'},
    });
    expect(
      (manifest['dependencies']
          as Map)['io.github.furroxide.topiaforge.multiplayer'],
      '0.1.0-rc.1',
    );
    expect(added.stdout, contains('topiaforge mod sync multiplayer'));

    final project = Directory(projectDir)
        .listSync()
        .whereType<File>()
        .singleWhere((file) => p.extension(file.path) == '.csproj');
    expect(
      project.readAsStringSync(),
      contains(
        '<PackageReference Include="TopiaForge.Mods.Multiplayer" Version="0.1.0-rc.1" />',
      ),
    );
    expect(
      project.readAsStringSync(),
      contains(
        '<PackageReference Include="TopiaForge.Mods.Multiplayer.Generators" Version="0.1.0-rc.1" PrivateAssets="all" />',
      ),
    );
    final lockFile = File(
      p.join(projectDir, 'topiaforge.multiplayer.lock.json'),
    );
    expect(jsonDecode(lockFile.readAsStringSync()), {
      'schemaVersion': 2,
      'protocolVersion': '1.0.0',
      'contracts': <Object?>[],
    });

    const generatedLock = '{"schemaVersion":2,"contracts":[{"id":"kept"}]}';
    lockFile.writeAsStringSync(generatedLock);
    final addedAgain = await currentHarness().runCli([
      'mod',
      'add',
      'multiplayer',
      '--project',
      projectDir,
    ]);
    expect(
      addedAgain.exitCode,
      0,
      reason: '${addedAgain.stdout}\n${addedAgain.stderr}',
    );
    expect(lockFile.readAsStringSync(), generatedLock);

    // Synchronization rebuilds the root project so the checked-in lock always
    // comes from generated contract descriptors. Source-less multiplayer
    // projects are deliberately unsupported because they cannot be verified.
    final synchronized = await currentHarness().runCli([
      'mod',
      'sync',
      'multiplayer',
      '--project',
      projectDir,
    ]);
    expect(
      synchronized.exitCode,
      0,
      reason: '${synchronized.stdout}\n${synchronized.stderr}',
    );
    expect(synchronized.stdout, contains('Synchronized'));
    expect(jsonDecode(lockFile.readAsStringSync()), {
      'schemaVersion': 2,
      'protocolVersion': '1.0.0',
      'contracts': <Object?>[],
    });

    final shown = await currentHarness().runCli([
      'mod',
      'show',
      '--project',
      projectDir,
    ]);
    expect(shown.exitCode, 0, reason: '${shown.stdout}\n${shown.stderr}');

    final migrateNoOp = await currentHarness().runCli([
      'migrate-manifest',
      '--project',
      projectDir,
    ]);
    expect(migrateNoOp.exitCode, 0);
    expect(migrateNoOp.stdout, contains('supported schema V5'));
  });

  test('mod remove multiplayer keeps the project on standalone V5', () async {
    final created = await currentHarness().runCli([
      'new',
      'mod',
      't.remove-multiplayer',
      '--dir',
      currentHarness().temp.path,
    ]);
    expect(created.exitCode, 0, reason: '${created.stdout}\n${created.stderr}');
    final projectDir = p.join(
      currentHarness().temp.path,
      't.remove-multiplayer',
    );
    final added = await currentHarness().runCli([
      'mod',
      'add',
      'multiplayer',
      '--project',
      projectDir,
    ]);
    expect(added.exitCode, 0, reason: '${added.stdout}\n${added.stderr}');

    final removed = await currentHarness().runCli([
      'mod',
      'remove',
      'multiplayer',
      '--project',
      projectDir,
    ]);
    expect(removed.exitCode, 0, reason: '${removed.stdout}\n${removed.stderr}');

    final manifest =
        jsonDecode(
              File(
                p.join(projectDir, 'topiaforge.mod.json'),
              ).readAsStringSync(),
            )
            as Map<String, Object?>;
    expect(manifest['schemaVersion'], 5);
    expect(manifest, isNot(contains('multiplayer')));
    expect(
      (manifest['dependencies'] as Map? ?? const {}).keys,
      isNot(contains('io.github.furroxide.topiaforge.multiplayer')),
    );
    expect(
      File(p.join(projectDir, 'topiaforge.multiplayer.lock.json')).existsSync(),
      isFalse,
    );
    final project = Directory(projectDir)
        .listSync()
        .whereType<File>()
        .singleWhere((file) => p.extension(file.path) == '.csproj');
    expect(
      project.readAsStringSync(),
      isNot(contains('TopiaForge.Mods.Multiplayer')),
    );
  });
}
