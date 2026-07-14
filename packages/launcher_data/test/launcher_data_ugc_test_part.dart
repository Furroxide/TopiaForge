part of 'launcher_data_test.dart';

void _registerUgcDataTests({
  required LocalLauncherRepository Function() repository,
  required Directory Function() dataRoot,
  required Directory Function() gameRoot,
}) {
  test('UGC scene scan is typed, deterministic, and bounded', () async {
    final watch = Directory(p.join(dataRoot().path, 'ugc-watch'))..createSync();
    File(p.join(watch.path, 'project.json')).writeAsStringSync(
      jsonEncode({
        'scenes': {
          'z-scene': {'name': 'Zulu'},
          'a-scene': {'id': 'scene-a', 'name': 'Alpha'},
        },
      }),
    );

    final scenes = await repository().listWatchFolderScenes(watch.path);

    expect(scenes.map((scene) => scene.id), ['scene-a', 'z-scene']);

    File(p.join(watch.path, 'project.json')).writeAsStringSync('{not-json');
    await expectLater(
      repository().listWatchFolderScenes(watch.path),
      throwsA(isA<FormatException>()),
    );

    File(
      p.join(watch.path, 'project.json'),
    ).writeAsBytesSync(List<int>.filled(16 * 1024 * 1024 + 1, 0x20));
    await expectLater(
      repository().listWatchFolderScenes(watch.path),
      throwsA(predicate((error) => error.toString().contains('exceeds'))),
    );
  });

  test('UGC inspection reports source and strict UTF-8 failures', () async {
    final watch = Directory(p.join(dataRoot().path, 'ugc-invalid-utf8'))
      ..createSync();
    final snapshot = File(p.join(watch.path, 'project.json'))
      ..writeAsBytesSync([0xff, 0xfe, 0xfd]);

    final result = await repository().inspectWatchFolderScenes(watch.path);

    expect(result.scenes, isEmpty);
    expect(result.hasBlockingIssues, isTrue);
    expect(result.source?.path, snapshot.path);
    expect(result.source?.byteLength, 3);
    expect(result.issues.single.message, contains('UTF-8'));
    await expectLater(
      repository().listWatchFolderScenes(watch.path),
      throwsA(isA<FormatException>()),
    );
  });

  test('UGC inspection breaks equal-mtime ties deterministically', () async {
    final watch = Directory(p.join(dataRoot().path, 'ugc-tie'))..createSync();
    final a = File(p.join(watch.path, 'a.json'))
      ..writeAsStringSync(
        jsonEncode({
          'scenes': {'scene-a': {}},
        }),
      );
    final z = File(p.join(watch.path, 'z.json'))
      ..writeAsStringSync(
        jsonEncode({
          'scenes': {'scene-z': {}},
        }),
      );
    final tied = DateTime.utc(2026, 1, 2, 3, 4, 5);
    a.setLastModifiedSync(tied);
    z.setLastModifiedSync(tied);

    final result = await repository().inspectWatchFolderScenes(watch.path);

    expect(result.issues, isEmpty);
    expect(result.source?.path, a.path);
    expect(result.scenes.map((scene) => scene.id), ['scene-a']);
  });

  test('UGC inspection detects replacement between scan and read', () async {
    final watch = Directory(p.join(dataRoot().path, 'ugc-race'))..createSync();
    final snapshot = File(p.join(watch.path, 'project.json'))
      ..writeAsStringSync(
        jsonEncode({
          'scenes': {'old': {}},
        }),
      );
    final racingRepository = LocalLauncherRepository(
      dataRoot: p.join(dataRoot().path, 'race-data'),
      repositoryRoot: p.join(dataRoot().path, 'race-repo'),
      ugcInspectionReadHook: (_) {
        snapshot.writeAsStringSync(
          jsonEncode({
            'scenes': {'replacement-with-a-different-size': {}},
          }),
        );
      },
    );

    final result = await racingRepository.inspectWatchFolderScenes(watch.path);

    expect(result.scenes, isEmpty);
    expect(result.hasBlockingIssues, isTrue);
    expect(result.issues.single.message, contains('changed'));
  });

  test('malformed UGC config and status are surfaced', () async {
    final install = await repository().selectGameDirectory(gameRoot().path);
    final configDir = Directory(
      p.join(gameRoot().path, 'BepInEx', 'RobotopiaModManager', 'config'),
    )..createSync(recursive: true);
    final config = File(p.join(configDir.path, 'robotopia.ugc.livesync.json'))
      ..writeAsStringSync('{broken');

    await expectLater(
      repository().deployUgcLiveSyncConfig(
        install,
        const UgcLiveSyncSettings(watchFolder: 'safe'),
      ),
      throwsA(isA<FormatException>()),
    );
    expect(config.readAsStringSync(), '{broken');

    File(
      p.join(configDir.path, 'robotopia.ugc.livesync.status.json'),
    ).writeAsStringSync('[]');
    await expectLater(
      repository().readUgcLiveSyncStatus(install),
      throwsA(isA<FormatException>()),
    );
  });

  test('cleans up UGC live-sync runtime state', () async {
    final repo = repository();
    final game = gameRoot();
    final install = await repo.selectGameDirectory(game.path);
    final configDir = Directory(
      p.join(game.path, 'BepInEx', 'RobotopiaModManager', 'config'),
    )..createSync(recursive: true);
    final statusFile = File(
      p.join(configDir.path, 'robotopia.ugc.livesync.status.json'),
    )..writeAsStringSync('{"status":"Connected"}');
    final sessionFile = File(p.join(dataRoot().path, 'ugc-session.json'))
      ..writeAsStringSync('{"documentUrl":"automerge:stale"}');

    final report = await repo.cleanupUgcLiveSync(
      install,
      const UgcLiveSyncSettings(
        transport: 'automerge',
        watchFolder: r'C:\Robotopia\ugc-watch',
        editorUrl: 'https://editor/?project=automerge:stale',
        documentUrl: 'automerge:stale',
        sceneId: 'main',
        autoConnectOnStart: true,
      ),
    );

    final config =
        jsonDecode(File(report.configPath).readAsStringSync())
            as Map<String, Object?>;
    final command =
        jsonDecode(File(report.commandPath).readAsStringSync())
            as Map<String, Object?>;
    expect(config['autoConnectOnStart'], isFalse);
    expect(config['editorUrl'], isEmpty);
    expect(config['documentUrl'], isEmpty);
    expect(config['watchFolder'], r'C:\Robotopia\ugc-watch');
    expect(command['command'], 'stop');
    expect(command['cleanup'], isTrue);
    expect(statusFile.existsSync(), isFalse);
    expect(sessionFile.existsSync(), isFalse);
    expect(report.statusFileDeleted, isTrue);
    expect(report.sessionFileDeleted, isTrue);

    await repo.deployUgcLiveSyncConfig(
      install,
      const UgcLiveSyncSettings(watchFolder: 'durable-watch'),
    );
    expect(File(report.commandPath).existsSync(), isFalse);
  });

  test('deploy and cleanup preserve unknown durable runtime fields', () async {
    final repo = repository();
    final install = await repo.selectGameDirectory(gameRoot().path);
    final configPath = await repo.deployUgcLiveSyncConfig(
      install,
      const UgcLiveSyncSettings(
        transport: 'automerge',
        watchFolder: r'C:\Robotopia\durable-watch',
        editorUrl: 'https://editor/?project=automerge:stale',
        documentUrl: 'automerge:stale',
        syncServerUrl: 'https://sync.example.test',
        sceneId: 'durable-scene',
        autoConnectOnStart: true,
        maxSnapshotBytes: 123456,
        debounceMilliseconds: 375,
      ),
    );
    final configFile = File(configPath);
    final seeded =
        jsonDecode(configFile.readAsStringSync()) as Map<String, Object?>;
    seeded['futureRuntimeOption'] = {'enabled': true};
    configFile.writeAsStringSync(jsonEncode(seeded));

    await repo.deployUgcLiveSyncConfig(
      install,
      const UgcLiveSyncSettings(
        transport: 'automerge',
        watchFolder: r'C:\Robotopia\durable-watch',
        documentUrl: 'automerge:new',
        syncServerUrl: 'https://sync.example.test',
        sceneId: 'durable-scene',
        autoConnectOnStart: true,
        maxSnapshotBytes: 123456,
        debounceMilliseconds: 375,
      ),
    );
    final report = await repo.cleanupUgcLiveSync(
      install,
      const UgcLiveSyncSettings(),
    );
    final config =
        jsonDecode(File(report.configPath).readAsStringSync())
            as Map<String, Object?>;

    expect(config['transport'], 'automerge');
    expect(config['watchFolder'], r'C:\Robotopia\durable-watch');
    expect(config['syncServerUrl'], 'https://sync.example.test');
    expect(config['sceneId'], 'durable-scene');
    expect(config['maxSnapshotBytes'], 123456);
    expect(config['debounceMilliseconds'], 375);
    expect(config['autoConnectOnStart'], isFalse);
    expect(config['editorUrl'], isEmpty);
    expect(config['documentUrl'], isEmpty);
    expect(config['futureRuntimeOption'], {'enabled': true});
  });
}
