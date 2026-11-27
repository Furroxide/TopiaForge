part of 'launcher_data_test.dart';

void _registerLaunchSelectionRegressions({
  required Directory Function() root,
  required Directory Function() dataRoot,
  required Directory Function() repositoryRoot,
  required Directory Function() gameRoot,
}) {
  group('launch selection regression', () {
    test(
      'inherited default-enabled package absent from state cannot bypass validation',
      () async {
        var starts = 0;
        final (repository, install) = await _prepareProfileLaunchRepository(
          dataRoot: dataRoot(),
          repositoryRoot: repositoryRoot(),
          gameRoot: gameRoot(),
          starter: (_) async {
            starts++;
            return 41;
          },
        );
        addTearDown(repository.dispose);
        await repository.installPackage(
          _createPackage(root(), id: 'default.mod', version: '1.0.0').path,
          install,
        );
        final state = _profileManagerState(gameRoot());
        state.writeAsStringSync(jsonEncode({'schemaVersion': 1, 'mods': []}));
        File(
          p.join(
            gameRoot().path,
            'BepInEx',
            'TopiaForge',
            'packages',
            'default.mod',
            '1.0.0',
            'DefaultMod.dll',
          ),
        ).writeAsStringSync('tampered after install');
        final result = await repository.launch(
          install,
          LauncherProfile.defaultProfile(),
        );
        expect(
          result.started,
          isFalse,
          reason:
              'Runtime default-enabled packages must participate in launcher preflight.',
        );
        expect(result.message, contains('default.mod'));
        expect(starts, 0);
        expect((jsonDecode(state.readAsStringSync()) as Map)['mods'], isEmpty);
      },
    );

    test(
      'unpinned state version cannot override highest compatible installed version',
      () async {
        File(
          p.join(gameRoot().path, 'installed-build.json'),
        ).writeAsStringSync('{"id":2309}');
        var starts = 0;
        final (repository, install) = await _prepareProfileLaunchRepository(
          dataRoot: dataRoot(),
          repositoryRoot: repositoryRoot(),
          gameRoot: gameRoot(),
          starter: (_) async {
            starts++;
            return 42;
          },
        );
        addTearDown(repository.dispose);
        await repository.installPackage(
          _createPackage(
            root(),
            id: 'versioned.mod',
            version: '1.0.0',
            gameVersionRange: '0.0.2309',
          ).path,
          install,
        );
        await repository.installPackage(
          _createPackage(root(), id: 'versioned.mod', version: '2.0.0').path,
          install,
        );
        final state = _profileManagerState(gameRoot());
        final document =
            jsonDecode(state.readAsStringSync()) as Map<String, Object?>;
        final item = (document['mods'] as List).cast<Map>().single;
        item['version'] = '1.0.0';
        item['versionPinned'] = false;
        state.writeAsStringSync(jsonEncode(document));
        File(
          p.join(gameRoot().path, 'installed-build.json'),
        ).writeAsStringSync('{"id":2228}');
        final result = await repository.launch(
          install,
          LauncherProfile.defaultProfile(),
        );
        expect(
          result.started,
          isTrue,
          reason:
              'An unpinned state hint must not force an incompatible installed version.',
        );
        expect(starts, 1);
        expect(
          (jsonDecode(state.readAsStringSync()) as Map)['mods'],
          document['mods'],
        );
      },
    );

    test(
      'linked staging ancestor never creates a command outside the trusted game',
      () async {
        var starts = 0;
        final (repository, install) = await _prepareProfileLaunchRepository(
          dataRoot: dataRoot(),
          repositoryRoot: repositoryRoot(),
          gameRoot: gameRoot(),
          starter: (_) async {
            starts++;
            return 43;
          },
        );
        addTearDown(repository.dispose);
        final outside = Directory(p.join(root().path, 'foreign-staging'))
          ..createSync();
        final marker = File(p.join(outside.path, 'keep.json'))
          ..writeAsStringSync('foreign');
        final staging = Directory(
          p.join(gameRoot().path, 'BepInEx', 'TopiaForge', 'staging'),
        );
        if (staging.existsSync()) staging.deleteSync();
        final link = Link(staging.path);
        try {
          link.createSync(outside.path);
        } on FileSystemException catch (error) {
          markTestSkipped('Directory-link creation unavailable: $error');
          return;
        }
        addTearDown(() {
          if (link.existsSync()) link.deleteSync();
        });
        final result = await repository.launch(
          install,
          const LauncherProfile(id: 'safe', name: 'Safe'),
        );
        expect(result.started, isFalse);
        expect(starts, 0);
        expect(outside.listSync().map((entry) => p.basename(entry.path)), [
          'keep.json',
        ]);
        expect(marker.readAsStringSync(), 'foreign');
      },
    );
  });
}
