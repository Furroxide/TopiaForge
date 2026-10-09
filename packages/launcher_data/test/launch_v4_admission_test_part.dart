part of 'launcher_data_test.dart';

void _registerLaunchV4AdmissionTests({
  required Directory Function() root,
  required Directory Function() dataRoot,
  required Directory Function() repositoryRoot,
  required Directory Function() gameRoot,
}) {
  group('V4 admission and observations', () {
    test(
      'restart validates replacement before stopping exactly the owned process',
      () async {
        var running = false;
        var starts = 0;
        var stops = 0;
        LaunchProcessIdentity? stopped;
        final (repo, install) = await _prepareProfileLaunchRepository(
          dataRoot: dataRoot(),
          repositoryRoot: repositoryRoot(),
          gameRoot: gameRoot(),
          probe: (_) async => running,
          starter: (_) async {
            running = true;
            return ++starts + 500;
          },
          identityReader: (pid, path) async => LaunchProcessIdentity(
            pid: pid,
            startTimeUtc: DateTime.utc(2026),
            executablePath: path,
            nativeStartToken: 'test-generation',
          ),
          liveness: (_) async => running,
          stopper: (identity) async {
            stopped = identity;
            stops++;
            running = false;
            return true;
          },
        );
        await repo.installPackage(_v6Package(root()).path, install);
        final first = await repo.launch(install, _v6Profile());
        expect(first.processStarted, isTrue);
        final invalid = _v6Profile().copyWith(
          enabledMods: {'play.mod', 'missing.mod'},
        );
        final rejected = await repo.restart(install, invalid);
        expect(rejected.processStarted, isFalse);
        expect(stops, 0);
        expect(running, isTrue);
        final restarted = await repo.restart(install, _v6Profile());
        expect(restarted.processStarted, isTrue, reason: restarted.message);
        expect(stops, 1);
        expect(stopped, same(first.process));
        expect(starts, 2);
        expect(restarted.requestId, isNot(first.requestId));
      },
    );

    test(
      'disposing a pending restart never stops the previously running game',
      () async {
        var running = false;
        var restartProbe = false;
        var stops = 0;
        final entered = Completer<void>();
        final proceed = Completer<void>();
        final (repo, install) = await _prepareProfileLaunchRepository(
          dataRoot: dataRoot(),
          repositoryRoot: repositoryRoot(),
          gameRoot: gameRoot(),
          probe: (_) async {
            if (restartProbe) {
              entered.complete();
              await proceed.future;
            }
            return running;
          },
          starter: (_) async {
            running = true;
            return 560;
          },
          identityReader: (pid, path) async => LaunchProcessIdentity(
            pid: pid,
            startTimeUtc: DateTime.utc(2026),
            executablePath: path,
          ),
          liveness: (_) async => running,
          stopper: (_) async {
            stops++;
            running = false;
            return true;
          },
        );
        const profile = LauncherProfile(id: 'menu', name: 'Menu');
        expect((await repo.launch(install, profile)).processStarted, isTrue);
        restartProbe = true;
        final pending = repo.restart(install, profile);
        await entered.future;
        await repo.dispose();
        proceed.complete();
        expect((await pending).processStarted, isFalse);
        expect(stops, 0);
        expect(running, isTrue);
      },
    );

    test(
      'unowned or unverified running process never reaches stopper',
      () async {
        var stops = 0;
        var starts = 0;
        var throws = false;
        final (repo, install) = await _prepareProfileLaunchRepository(
          dataRoot: dataRoot(),
          repositoryRoot: repositoryRoot(),
          gameRoot: gameRoot(),
          probe: (_) async {
            if (throws) throw StateError('unknown');
            return true;
          },
          starter: (_) async {
            starts++;
            return 510;
          },
          stopper: (_) async {
            stops++;
            return true;
          },
        );
        final profile = const LauncherProfile(id: 'menu', name: 'Menu');
        expect((await repo.launch(install, profile)).message, contains('Busy'));
        expect((await repo.restart(install, profile)).processStarted, isFalse);
        throws = true;
        expect((await repo.launch(install, profile)).message, contains('Busy'));
        expect(stops, 0);
        expect(starts, 0);
      },
    );

    test(
      'dead previous process does not leave monitor admission permanently Busy',
      () async {
        var running = false;
        var starts = 0;
        final (repo, install) = await _prepareProfileLaunchRepository(
          dataRoot: dataRoot(),
          repositoryRoot: repositoryRoot(),
          gameRoot: gameRoot(),
          probe: (_) async => running,
          starter: (_) async {
            running = true;
            return ++starts + 520;
          },
          identityReader: (pid, path) async => LaunchProcessIdentity(
            pid: pid,
            startTimeUtc: DateTime.utc(2026),
            executablePath: path,
          ),
          liveness: (_) async => running,
        );
        final profile = const LauncherProfile(id: 'menu', name: 'Menu');
        expect((await repo.launch(install, profile)).processStarted, isTrue);
        running = false;
        expect((await repo.launch(install, profile)).processStarted, isTrue);
        expect(starts, 2);
      },
    );

    test(
      'separate repository instances share admission until preparation ends',
      () async {
        final entered = Completer<void>();
        final proceed = Completer<void>();
        var calls = 0;
        var starts = 0;
        final (first, install) = await _prepareProfileLaunchRepository(
          dataRoot: dataRoot(),
          repositoryRoot: repositoryRoot(),
          gameRoot: gameRoot(),
          starter: (_) async {
            starts++;
            return 551;
          },
          probe: (_) async {
            if (++calls == 1) {
              entered.complete();
              await proceed.future;
            }
            return false;
          },
        );
        final second = LocalLauncherRepository(
          dataRoot: dataRoot().path,
          repositoryRoot: repositoryRoot().path,
          gameRunningProbe: (_) async => false,
          gameProcessStarter: (_) async {
            starts++;
            return 552;
          },
          packageMetadataValidator: _acceptPackageMetadata,
        );
        addTearDown(second.dispose);
        const profile = LauncherProfile(id: 'menu', name: 'Menu');
        final pending = first.launch(install, profile);
        await entered.future;
        try {
          final competing = await second.launch(install, profile);
          expect(competing.processStarted, isFalse);
          expect(competing.message, contains('Busy'));
          expect(starts, 0);
        } finally {
          await first.dispose();
          proceed.complete();
          expect((await pending).processStarted, isFalse);
        }
        expect(
          (await second.launch(install, profile)).processStarted,
          isTrue,
          reason:
              'The disposed preparation must release its cross-instance admission.',
        );
        expect(starts, 1);
      },
    );

    test(
      'malformed manager envelope never becomes an empty inherited selection',
      () async {
        final (repo, install) = await _prepareProfileLaunchRepository(
          dataRoot: dataRoot(),
          repositoryRoot: repositoryRoot(),
          gameRoot: gameRoot(),
          starter: (_) async => 553,
        );
        final state = _profileManagerState(gameRoot());
        for (final text in [
          '[]',
          '{"mods":null}',
          '{"mods":{}}',
          '{"schemaVersion":1.0,"mods":[]}',
          '{"schemaVersion":null,"mods":[]}',
          '{"schemaVersion":2,"mods":[]}',
        ]) {
          state.writeAsStringSync(text);
          final preview = await repo.previewLaunch(
            install,
            LauncherProfile.defaultProfile(),
          );
          expect(preview.canLaunch, isFalse, reason: text);
          expect(
            preview.issues.map((issue) => issue.message).join(' '),
            contains('Manager state'),
          );
          expect(state.readAsStringSync(), text);
        }
      },
    );

    test(
      'only matching provenance affects availability and disabled packages stay disabled',
      () async {
        final (repo, install) = await _prepareProfileLaunchRepository(
          dataRoot: dataRoot(),
          repositoryRoot: repositoryRoot(),
          gameRoot: gameRoot(),
          starter: (_) async => 530,
        );
        await repo.installPackage(_v6Package(root()).path, install);
        final profile = _v6Profile();
        final original = await repo.previewLaunch(install, profile);
        void publish(
          String profileId,
          int revision, {
          String version = '1.0.0',
        }) {
          final envelope = LaunchObservationEnvelope(
            profileId: profileId,
            profileRevision: revision,
            producer: PackageIdentity(id: 'play.mod', version: version),
            packageSetDigest: original.packageDigest,
            observationRevision: 100,
            availability: [
              LaunchAvailability(
                kind: 'world',
                id: 'play.mod.world',
                blocks: [
                  LaunchBlock(
                    LaunchBlockCode.worldUnavailable,
                    'play.mod.world',
                  ),
                ],
              ),
            ],
          );
          final key = LaunchStorageKeys.observation(envelope);
          File(
            p.join(
              gameRoot().path,
              'BepInEx',
              'TopiaForge',
              'staging',
              'runtime-observation-$key.json',
            ),
          ).writeAsStringSync(jsonEncode(envelope.toJson()));
        }

        publish('foreign', profile.revision);
        publish(profile.id, profile.revision + 1);
        publish(profile.id, profile.revision, version: '2.0.0');
        expect((await repo.previewLaunch(install, profile)).canLaunch, isTrue);
        publish(profile.id, profile.revision);
        final unavailable = await repo.previewLaunch(install, profile);
        expect(unavailable.canLaunch, isFalse);
        expect(
          unavailable.blocks.map((block) => block.code),
          contains(LaunchBlockCode.worldUnavailable),
        );
        final disabled = await repo.previewLaunch(
          install,
          profile.copyWith(enabledMods: {}),
        );
        expect(
          disabled.blocks.map((block) => block.code),
          contains(LaunchBlockCode.targetPackageDisabled),
        );
        expect(disabled.resolution!.plan, isNull);
      },
    );

    test(
      'explicit main menu and safe mode preserve unresolved durable selection',
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
            return 540;
          },
        );
        await repo.installPackage(_v6Package(root()).path, install);
        final profile = _v6Profile().copyWith(
          launchSelection: LaunchSelection.unresolvedLegacy({
            'worldSelection': {
              'gamemodeId': 'retired.mode',
              'launchIntoGamemode': true,
              'future': null,
            },
          }),
        );
        final persisted = (await repo.saveProfiles([
          profile,
        ], profile.id)).single;
        final file = File(p.join(dataRoot().path, 'profiles.json'));
        final before = file.readAsStringSync();
        expect((await repo.launch(install, persisted)).processStarted, isFalse);
        expect(
          (await repo.launch(
            install,
            persisted,
            selectionOverride: const LaunchSelection.mainMenu(),
          )).processStarted,
          isTrue,
        );
        expect(commands.last.command, 'main-menu');
        expect(commands.last.packages.map((item) => item.id), ['play.mod']);
        expect(
          (await repo.launch(
            install,
            persisted.copyWith(
              launchSettings: const LaunchSettings(safeMode: true),
            ),
          )).processStarted,
          isTrue,
        );
        expect(commands.last.command, 'main-menu');
        expect(commands.last.safeMode, isTrue);
        expect(commands.last.packages, isEmpty);
        expect(file.readAsStringSync(), before);
      },
    );
  });
}
