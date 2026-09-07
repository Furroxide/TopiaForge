part of 'launcher_data_test.dart';

void _registerLaunchSafeModeRecoveryTests({
  required Directory Function() dataRoot,
  required Directory Function() repositoryRoot,
  required Directory Function() gameRoot,
}) {
  group('V4 safe-mode recovery', () {
    test(
      'malformed manager content launches only explicit empty safe mode without writing state',
      () async {
        final commands = <ProfileLaunchConfigurationV4>[];
        final (repo, install) = await _prepareProfileLaunchRepository(
          dataRoot: dataRoot(),
          repositoryRoot: repositoryRoot(),
          gameRoot: gameRoot(),
          starter: (request) async {
            commands.add(
              ProfileLaunchConfigurationV4.fromJson(
                jsonDecode(
                  File(
                    request.environment[ProfileLaunchConfigurationV4
                        .environmentVariable]!,
                  ).readAsStringSync(),
                ),
              ),
            );
            return 580;
          },
        );
        final state = _profileManagerState(gameRoot());
        final profile = LauncherProfile.defaultProfile();
        final safe = profile.copyWith(
          launchSettings: const LaunchSettings(safeMode: true),
        );
        for (final text in [
          '{',
          '[]',
          '{"mods":null}',
          '{"mods":{}}',
          '{"schemaVersion":1.0,"mods":[]}',
          '{"schemaVersion":null,"mods":[]}',
          '{"schemaVersion":2,"mods":[]}',
        ]) {
          state.writeAsStringSync(text);
          final blocked = await repo.launch(install, profile);
          expect(blocked.processStarted, isFalse, reason: text);
          final preview = await repo.previewLaunch(install, safe);
          expect(
            preview.canLaunch,
            isTrue,
            reason:
                '$text: ${preview.issues.map((issue) => issue.message).join(' ')}',
          );
          expect(preview.packages, isEmpty);
          expect(preview.effectiveSelection.kind, LaunchSelectionKind.mainMenu);
          expect(
            preview.issues.where(
              (issue) => issue.severity == IssueSeverity.warning,
            ),
            isNotEmpty,
          );
          final launched = await repo.launch(install, safe);
          expect(launched.processStarted, isTrue, reason: launched.message);
          expect(commands.last.command, 'main-menu');
          expect(commands.last.safeMode, isTrue);
          expect(commands.last.packages, isEmpty);
          expect(commands.last.digest, packageSetDigest(const []));
          expect(state.readAsStringSync(), text);
          expect(File('${state.path}.bak').existsSync(), isFalse);
        }
        expect(commands, hasLength(7));
      },
    );

    test(
      'legacy absent mods is empty but present malformed records block every ordinary command',
      () async {
        final (repo, install) = await _prepareProfileLaunchRepository(
          dataRoot: dataRoot(),
          repositoryRoot: repositoryRoot(),
          gameRoot: gameRoot(),
          starter: (_) async => 583,
        );
        final state = _profileManagerState(gameRoot());
        const explicit = LauncherProfile(id: 'menu', name: 'Menu');
        for (final text in [
          '{}',
          '{"schemaVersion":0}',
          '{"schemaVersion":1,"unknown":true}',
        ]) {
          state.writeAsStringSync(text);
          expect(
            (await repo.previewLaunch(install, explicit)).canLaunch,
            isTrue,
            reason: text,
          );
          expect(state.readAsStringSync(), text);
        }
        for (final text in [
          '{"mods":[42]}',
          '{"mods":[{"id":"bad id","enabled":false}]}',
          '{"mods":[{"id":"play.mod","enabled":"yes"}]}',
          '{"mods":[{"id":"play.mod","enabled":false},{"id":"play.mod","enabled":false}]}',
        ]) {
          state.writeAsStringSync(text);
          expect(
            (await repo.previewLaunch(install, explicit)).canLaunch,
            isFalse,
            reason: text,
          );
          final safe = explicit.copyWith(
            launchSettings: const LaunchSettings(safeMode: true),
          );
          final recovery = await repo.previewLaunch(install, safe);
          expect(recovery.canLaunch, isTrue, reason: text);
          expect(recovery.packages, isEmpty);
          expect(state.readAsStringSync(), text);
        }
      },
    );

    test(
      'duplicate manager properties are refused without losing original escaped keys',
      () async {
        final (repo, install) = await _prepareProfileLaunchRepository(
          dataRoot: dataRoot(),
          repositoryRoot: repositoryRoot(),
          gameRoot: gameRoot(),
          starter: (_) async => 584,
        );
        final state = _profileManagerState(gameRoot());
        const text =
            r'{"mods":[{"id":"play.mod","enabled":false,"\u0065nabled":true}]}';
        state.writeAsStringSync(text);
        const ordinary = LauncherProfile(id: 'menu', name: 'Menu');
        final blocked = await repo.previewLaunch(install, ordinary);
        expect(blocked.canLaunch, isFalse);
        expect(
          blocked.issues.map((issue) => issue.message).join(' '),
          contains('duplicate property'),
        );
        expect(
          (await repo.previewLaunch(
            install,
            ordinary.copyWith(
              launchSettings: const LaunchSettings(safeMode: true),
            ),
          )).canLaunch,
          isTrue,
        );
        expect(state.readAsStringSync(), text);
      },
    );

    test(
      'snapshot reconciliation never rewrites malformed records or unrepresentable legacy numbers',
      () async {
        final (repo, _) = await _prepareProfileLaunchRepository(
          dataRoot: dataRoot(),
          repositoryRoot: repositoryRoot(),
          gameRoot: gameRoot(),
          starter: (_) async => 585,
        );
        final state = _profileManagerState(gameRoot());
        for (final text in [
          '{"mods":[{"id":"play.mod","enabled":false,"restartRequired":true},{"id":"bad id","enabled":false}]}',
          '{"mods":[{"id":"play.mod","enabled":false,"restartRequired":true}],"worldLaunch":{"future":1234567890123456789012345678901234567890}}',
        ]) {
          state.writeAsStringSync(text);
          final snapshot = await repo.loadSnapshot();
          expect(snapshot.profiles, isNotEmpty);
          expect(
            snapshot.previewsByProfile.values.every(
              (preview) => !preview.canLaunch,
            ),
            isTrue,
          );
          expect(state.readAsStringSync(), text);
          expect(File('${state.path}.bak').existsSync(), isFalse);
        }
      },
    );

    test(
      'present versioned manager selection must remain valid even for an explicit launcher menu',
      () async {
        final (repo, install) = await _prepareProfileLaunchRepository(
          dataRoot: dataRoot(),
          repositoryRoot: repositoryRoot(),
          gameRoot: gameRoot(),
          starter: (_) async => 586,
        );
        final state = _profileManagerState(gameRoot());
        const menu = LauncherProfile(id: 'menu', name: 'Menu');
        for (final value in [
          null,
          42,
          {'schemaVersion': 1.0, 'kind': 'main-menu'},
          {'schemaVersion': 1, 'kind': 'unknown'},
        ]) {
          final text = jsonEncode({'mods': [], 'launchSelection': value});
          state.writeAsStringSync(text);
          expect(
            (await repo.previewLaunch(install, menu)).canLaunch,
            isFalse,
            reason: text,
          );
          expect(
            (await repo.previewLaunch(
              install,
              menu.copyWith(
                launchSettings: const LaunchSettings(safeMode: true),
              ),
            )).canLaunch,
            isTrue,
          );
          expect(state.readAsStringSync(), text);
        }
      },
    );

    test(
      'runtime byte limit and missing-primary backup prevent ordinary admission without writes',
      () async {
        final (repo, install) = await _prepareProfileLaunchRepository(
          dataRoot: dataRoot(),
          repositoryRoot: repositoryRoot(),
          gameRoot: gameRoot(),
          starter: (_) async => 587,
        );
        final state = _profileManagerState(gameRoot());
        final backup = File('${state.path}.bak');
        const menu = LauncherProfile(id: 'menu', name: 'Menu');
        final safe = menu.copyWith(
          launchSettings: const LaunchSettings(safeMode: true),
        );
        final text = jsonEncode({
          'mods': [],
          'future': 'x' * (4 * 1024 * 1024),
        });
        state.writeAsStringSync(text);
        expect((await repo.previewLaunch(install, menu)).canLaunch, isFalse);
        expect((await repo.previewLaunch(install, safe)).canLaunch, isTrue);
        expect(state.readAsStringSync(), text);
        state.deleteSync();
        backup.writeAsStringSync('{"mods":[]}');
        expect((await repo.previewLaunch(install, menu)).canLaunch, isFalse);
        expect((await repo.previewLaunch(install, safe)).canLaunch, isTrue);
        expect(state.existsSync(), isFalse);
        expect(backup.readAsStringSync(), '{"mods":[]}');
      },
    );

    test(
      'snapshot retains profiles and explicit recovery when manager JSON is malformed',
      () async {
        final (repo, _) = await _prepareProfileLaunchRepository(
          dataRoot: dataRoot(),
          repositoryRoot: repositoryRoot(),
          gameRoot: gameRoot(),
          starter: (_) async => 582,
        );
        final ordinary = LauncherProfile.defaultProfile();
        final safe = ordinary.copyWith(
          id: 'safe',
          name: 'Safe',
          launchSettings: const LaunchSettings(safeMode: true),
        );
        await repo.saveProfiles([ordinary, safe], ordinary.id);
        final state = _profileManagerState(gameRoot())..writeAsStringSync('{');
        final snapshot = await repo.loadSnapshot();
        expect(snapshot.profiles.map((profile) => profile.id), [
          ordinary.id,
          'safe',
        ]);
        expect(snapshot.installedMods, isEmpty);
        expect(snapshot.previewsByProfile[ordinary.id]!.canLaunch, isFalse);
        expect(snapshot.previewsByProfile['safe']!.canLaunch, isTrue);
        expect(state.readAsStringSync(), '{');
      },
    );

    test('safe mode still rejects a nonordinary manager state path', () async {
      var starts = 0;
      final (repo, install) = await _prepareProfileLaunchRepository(
        dataRoot: dataRoot(),
        repositoryRoot: repositoryRoot(),
        gameRoot: gameRoot(),
        starter: (_) async {
          starts++;
          return 581;
        },
      );
      final state = _profileManagerState(gameRoot());
      if (state.existsSync()) state.deleteSync();
      Directory(state.path).createSync();
      final safe = LauncherProfile.defaultProfile().copyWith(
        launchSettings: const LaunchSettings(safeMode: true),
      );
      expect((await repo.previewLaunch(install, safe)).canLaunch, isFalse);
      expect((await repo.launch(install, safe)).processStarted, isFalse);
      expect(starts, 0);
      expect(Directory(state.path).existsSync(), isTrue);
    });
  });
}
