part of 'topiaforge_cli_test.dart';

void _multiplayerCliTests(_CliTestHarness Function() currentHarness) {
  test('mod add multiplayer keeps V6 and scaffolds the contract lock', () async {
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
    expect(manifest['schemaVersion'], 6);
    expect(manifest['multiplayer'], {
      'mode': 'session',
      'presence': 'required',
      'protocol': {'version': '1.0.0', 'peerVersionRange': '>=1.0.0 <2.0.0'},
    });
    expect(
      (manifest['dependencies']
          as Map)['io.github.furroxide.topiaforge.multiplayer'],
      '0.2.0',
    );
    expect(added.stdout, contains('topiaforge mod sync multiplayer'));

    final project = Directory(projectDir)
        .listSync()
        .whereType<File>()
        .singleWhere((file) => p.extension(file.path) == '.csproj');
    expect(
      project.readAsStringSync(),
      contains(
        '<PackageReference Include="TopiaForge.Mods.Multiplayer" Version="0.2.0" />',
      ),
    );
    expect(
      project.readAsStringSync(),
      contains(
        '<PackageReference Include="TopiaForge.Mods.Multiplayer.Generators" Version="0.2.0" PrivateAssets="all" />',
      ),
    );
    final lockFile = File(
      p.join(projectDir, 'topiaforge.multiplayer.lock.json'),
    );
    expect(jsonDecode(lockFile.readAsStringSync()), {
      'schemaVersion': 3,
      'contracts': <Object?>[],
    });

    const generatedLock = '{"schemaVersion":3,"contracts":[{"id":"kept"}]}';
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
      'schemaVersion': 3,
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
    expect(migrateNoOp.stdout, contains('valid schema V6; no files changed'));
  });

  test(
    'mod remove multiplayer leaves V6 without a multiplayer block',
    () async {
      final created = await currentHarness().runCli([
        'new',
        'mod',
        't.remove-multiplayer',
        '--dir',
        currentHarness().temp.path,
      ]);
      expect(
        created.exitCode,
        0,
        reason: '${created.stdout}\n${created.stderr}',
      );
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
      expect(
        removed.exitCode,
        0,
        reason: '${removed.stdout}\n${removed.stderr}',
      );

      final manifest =
          jsonDecode(
                File(
                  p.join(projectDir, 'topiaforge.mod.json'),
                ).readAsStringSync(),
              )
              as Map<String, Object?>;
      expect(manifest['schemaVersion'], 6);
      expect(manifest, isNot(contains('multiplayer')));
      expect(
        (manifest['dependencies'] as Map? ?? const {}).keys,
        isNot(contains('io.github.furroxide.topiaforge.multiplayer')),
      );
      expect(
        File(
          p.join(projectDir, 'topiaforge.multiplayer.lock.json'),
        ).existsSync(),
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
    },
  );

  test(
    'multiplayer template packs only after its contract lock is synchronized',
    () async {
      final created = await currentHarness().runCli([
        'new',
        'mod',
        't.counter',
        '--template',
        'multiplayer',
        '--name',
        'Counter',
        '--author',
        'Sam',
        '--license',
        'MIT',
        '--dir',
        currentHarness().temp.path,
      ]);
      expect(
        created.exitCode,
        0,
        reason: '${created.stdout}\n${created.stderr}',
      );
      expect(created.stdout, contains('topiaforge mod sync multiplayer'));
      final projectDir = p.join(currentHarness().temp.path, 't.counter');

      final manifest =
          jsonDecode(
                File(
                  p.join(projectDir, 'topiaforge.mod.json'),
                ).readAsStringSync(),
              )
              as Map<String, Object?>;
      expect(manifest['schemaVersion'], 6);
      expect(manifest['multiplayer'], {
        'mode': 'session',
        'presence': 'required',
        'protocol': {'version': '1.0.0', 'peerVersionRange': '>=1.0.0 <2.0.0'},
      });
      expect(manifest['dependencies'], {
        'io.github.furroxide.topiaforge.multiplayer': '0.2.0',
      });
      expect(manifest['capabilities'], ['hud', 'input']);

      final project = File(
        p.join(projectDir, 'TCounter.csproj'),
      ).readAsStringSync();
      for (final package in const [
        'TopiaForge.Mods.Abstractions',
        'TopiaForge.Mods.Multiplayer',
      ]) {
        expect(
          project,
          contains('<PackageReference Include="$package" Version="0.2.0" />'),
        );
      }
      for (final analyzer in const [
        'TopiaForge.Mods.Analyzers',
        'TopiaForge.Mods.Multiplayer.Generators',
      ]) {
        expect(
          project,
          contains(
            '<PackageReference Include="$analyzer" Version="0.2.0" PrivateAssets="all" />',
          ),
        );
      }
      final lockFile = File(
        p.join(projectDir, 'topiaforge.multiplayer.lock.json'),
      );
      expect(jsonDecode(lockFile.readAsStringSync()), {
        'schemaVersion': 3,
        'contracts': <Object?>[],
      });

      final checked = await currentHarness().runCli([
        'check',
        'package',
        projectDir,
      ]);
      expect(
        checked.exitCode,
        0,
        reason: '${checked.stdout}\n${checked.stderr}',
      );

      final output = p.join(currentHarness().temp.path, 'packages');
      final refused = await currentHarness().runCli([
        'pack',
        '--project',
        projectDir,
        '--output',
        output,
      ]);
      expect(refused.exitCode, 1, reason: '${refused.stdout}');
      expect(refused.stderr, contains('is stale or tampered'));
      expect(refused.stderr, contains('topiaforge mod sync multiplayer'));

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
      final lock =
          jsonDecode(lockFile.readAsStringSync()) as Map<String, Object?>;
      final contract = (lock['contracts']! as List).single as Map;
      expect(contract['id'], 't.counter');
      expect(contract['stateIds'], ['t.counter/count']);
      expect(contract['commandIds'], ['t.counter/increment']);
      expect(contract['objectTypeIds'], isEmpty);
      expect(contract['eventIds'], isEmpty);

      final packed = await currentHarness().runCli([
        'pack',
        '--project',
        projectDir,
        '--output',
        output,
      ]);
      expect(packed.exitCode, 0, reason: '${packed.stdout}\n${packed.stderr}');
      final archive = ZipDecoder().decodeBytes(
        File(p.join(output, 't.counter-0.1.0.topiaforgemod')).readAsBytesSync(),
      );
      String packedText(String name) => utf8.decode(
        archive.files.singleWhere((file) => file.name == name).content
            as List<int>,
      );
      final packedManifest =
          jsonDecode(packedText('topiaforge.mod.json')) as Map<String, Object?>;
      expect((packedManifest['multiplayer'] as Map)['synchronizedFiles'], [
        'topiaforge.multiplayer.lock.json',
      ]);
      expect(
        packedText('topiaforge.multiplayer.lock.json'),
        lockFile.readAsStringSync(),
      );
    },
    timeout: const Timeout(Duration(minutes: 5)),
  );
}
