part of 'launcher_data_test.dart';

void _registerDiagnosticDataTests({
  required LocalLauncherRepository Function() repository,
  required Directory Function() dataRoot,
  required Directory Function() gameRoot,
}) {
  test('diagnostic bundles are bounded, atomic, and redact secrets', () async {
    final game = gameRoot();
    final selected = await repository().selectGameDirectory(game.path);
    final install = selected.copyWith(
      gameVersion: '0.0.2309',
      gameVersionLabel: 'build 2309',
    );

    final launcherLog = File(p.join(dataRoot().path, 'logs', 'launcher.log'));
    final configuredGamePath = p.join(dataRoot().path, 'configured-game');
    final settingsFile = File(p.join(dataRoot().path, 'settings.json'));
    final settings = (jsonDecode(settingsFile.readAsStringSync()) as Map)
        .cast<String, Object?>();
    settings['gamePath'] = '  $configuredGamePath  ';
    settingsFile.writeAsStringSync(jsonEncode(settings));
    launcherLog.writeAsStringSync(
      '${launcherLog.readAsStringSync()}\n'
      '{"accessToken":"launcher-secret","path":"${game.path}"}\n'
      'Configured game path: $configuredGamePath\n'
      'https://user:password@example.invalid/path?token=query-secret\n'
      'eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjMifQ.signaturevalue\n'
      'session_token=standalone-session-secret\n',
    );
    final managerLog = File(
      p.join(game.path, 'BepInEx', 'TopiaForge', 'logs', 'manager.log'),
    )..parent.createSync(recursive: true);
    managerLog.writeAsStringSync(
      '${List.filled(2 * 1024 * 1024 + 128, 'x').join()}\n'
      'Authorization: Bearer manager-secret\n'
      'password=plain-secret\n',
    );
    final bepinexLog = File(p.join(game.path, 'BepInEx', 'LogOutput.log'))
      ..parent.createSync(recursive: true)
      ..writeAsStringSync(
        List.generate(20002, (index) => 'line-$index').join('\n'),
      );

    final bundle = await repository().createDiagnosticBundle(
      install,
      const DependencyPlanner().resolveInstalled(const []),
    );
    final archive = ZipDecoder().decodeBytes(
      File(bundle.path).readAsBytesSync(),
      verify: true,
    );
    final contents = archive.files
        .where((file) => file.isFile)
        .map((file) => utf8.decode(file.content as List<int>))
        .join('\n');
    final files = {
      for (final file in archive.files.where((file) => file.isFile))
        file.name: List<int>.from(file.content as List<int>),
    };
    final manifest =
        jsonDecode(utf8.decode(files['diagnostic-manifest.json']!))
            as Map<String, Object?>;
    final manifestEntries = (manifest['entries'] as List).cast<Map>();

    expect(contents, contains('%ROBOTOPIA_GAME%'));
    expect(contents, contains('%REDACTED%'));
    expect(contents, contains('earlier content omitted'));
    expect(contents, contains('"gameVersion": "0.0.2309"'));
    expect(contents, isNot(contains(game.path)));
    expect(contents, isNot(contains(configuredGamePath)));
    expect(contents, isNot(contains('launcher-secret')));
    expect(contents, isNot(contains('query-secret')));
    expect(contents, isNot(contains('manager-secret')));
    expect(contents, isNot(contains('plain-secret')));
    expect(contents, isNot(contains('signaturevalue')));
    expect(contents, isNot(contains('standalone-session-secret')));
    expect(bundle.includedFiles, contains('diagnostic-manifest.json'));
    expect(manifest['hashAlgorithm'], 'SHA-256');
    for (final entry in manifestEntries) {
      final name = entry['name'] as String;
      final bytes = files[name]!;
      expect(entry['sha256'], sha256.convert(bytes).toString(), reason: name);
      expect(entry['includedBytes'], bytes.length, reason: name);
    }
    final managerMetadata = manifestEntries.singleWhere(
      (entry) => entry['name'] == 'manager.log',
    );
    expect(managerMetadata['truncated'], isTrue);
    expect(managerMetadata['truncationReasons'], contains('byteLimit'));
    final bepinexMetadata = manifestEntries.singleWhere(
      (entry) => entry['name'] == 'bepinex-log.txt',
    );
    expect(bepinexMetadata['truncated'], isTrue);
    expect(bepinexMetadata['truncationReasons'], contains('lineLimit'));
    expect(bundle.entries, hasLength(manifestEntries.length + 1));
    expect(bepinexLog.existsSync(), isTrue);
    expect(File(bundle.path).lengthSync(), lessThanOrEqualTo(16 * 1024 * 1024));
    expect(
      Directory(p.join(dataRoot().path, 'diagnostics')).listSync().where(
        (entity) =>
            entity.path.endsWith('.tmp') || entity.path.endsWith('.bak'),
      ),
      isEmpty,
    );
  });

  test('diagnostic bundles never include the multiplayer folder', () async {
    final install = await repository().selectGameDirectory(gameRoot().path);
    // The folder holds the player's name and favorites, and known-hosts
    // keys: unsalted hashes of typed `host:port` targets, from which a LAN
    // address is recovered in milliseconds.
    final folder = p.join(
      dataRoot().path,
      LocalMultiplayerRepository.folderName,
    );
    final store = MultiplayerStore(folder);
    const serverId =
        'c5f39060271ebd2f22d9bf155a26bf459274e31b795145fd9d862dc206d98bb3';
    await store.saveSettings(
      MultiplayerSettings(
        displayName: 'Bundle Probe',
        favorites: ServerFavorites([
          ServerBookmark(
            serverId: serverId,
            name: 'Probe Favorite',
            savedAt: DateTime.utc(2026, 10, 15),
          ),
        ]),
      ),
    );
    final typed = JoinAddressTarget(host: '192.168.50.7', port: 27250);
    await store.updateKnownHosts(
      (hosts) => hosts.remember(typed, serverId, DateTime.utc(2026, 10, 15)),
    );
    final key = KnownHosts.targetKey(typed.host, typed.port);
    expect(
      File(p.join(folder, 'known-hosts.json')).readAsStringSync(),
      contains(key),
    );
    File(
      p.join(folder, 'notes.json'),
    ).writeAsStringSync('{"probe": "multiplayer-folder-sentinel"}');

    final bundle = await repository().createDiagnosticBundle(
      install,
      const DependencyPlanner().resolveInstalled(const []),
    );
    final archive = ZipDecoder().decodeBytes(
      File(bundle.path).readAsBytesSync(),
      verify: true,
    );
    final contents = archive.files
        .where((file) => file.isFile)
        .map((file) => utf8.decode(file.content as List<int>))
        .join('\n');
    for (final name in [
      for (final file in archive.files) file.name,
      ...bundle.includedFiles,
    ]) {
      expect(name, isNot(contains('multiplayer')));
      expect(name, isNot(contains('known-hosts')));
    }
    for (final secret in [
      key,
      serverId,
      'Bundle Probe',
      'Probe Favorite',
      KnownHosts.schema,
      'multiplayer-folder-sentinel',
      '192.168.50.7',
    ]) {
      expect(contents, isNot(contains(secret)), reason: secret);
    }
  });
}
