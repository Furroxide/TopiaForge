part of 'launcher_data_test.dart';

void _registerDevPeerLaunchIntegrationTests({
  required Directory Function() root,
  required Directory Function() dataRoot,
  required Directory Function() repositoryRoot,
  required Directory Function() gameRoot,
}) {
  group(
    'development peer real launch admission',
    () {
      for (final scenario in [
        'start',
        'process refusal',
        'admission refusal',
        'busy',
      ]) {
        test(scenario, () async {
          GameProcessRequest? child;
          var running = false;
          final (repo, _) = await _prepareProfileLaunchRepository(
            dataRoot: dataRoot(),
            repositoryRoot: repositoryRoot(),
            gameRoot: gameRoot(),
            creator: (request) async {
              child = request;
              if (scenario == 'process refusal') {
                throw StateError('Fixture process creation refused');
              }
              return LaunchProcessReceipt(
                pid: 641,
                identity: LaunchProcessIdentity(
                  pid: 641,
                  startTimeUtc: DateTime.utc(2026, 10, 1),
                  executablePath: request.executable,
                  nativeStartToken: 'fixture-created:641',
                ),
              );
            },
            probe: (_) async => running,
            liveness: (_) async => false,
          );
          final overlay = DevPeerOverlay(0, gameRoot().path, dataRoot().path);
          final runId = 'a' * 32;
          final session = DevPeerSession(
            overlay,
            runId,
            repositoryRoot().path,
            repository: repo,
          );
          await session.prepare(
            [
              _v6Package(root()).path,
              _createPackage(
                root(),
                id: 'io.github.furroxide.topiaforge.multiplayer',
                version: '1.0.0',
              ).path,
            ],
            _v6Profile(),
            'play.mod',
            null,
          );
          // Admission is real; package metadata and process probes use doubles.
          // No native game starts here.
          if (scenario == 'admission refusal') {
            final manager = Directory(
              p.join(gameRoot().path, 'BepInEx', 'TopiaForge'),
            );
            manager.renameSync('${manager.path}.fixture-backup');
            File(manager.path).writeAsStringSync('Not a directory');
            await expectLater(
              session.launch(2),
              throwsA(
                isA<StateError>().having(
                  (error) => error.message,
                  'safe admission failure',
                  allOf(
                    contains('Launch preparation failed.'),
                    isNot(contains(gameRoot().path)),
                  ),
                ),
              ),
            );
            expect(child, isNull);
            return;
          }
          if (scenario == 'busy') {
            running = true;
            await expectLater(
              session.launch(2),
              throwsA(
                isA<StateError>().having(
                  (error) => error.message,
                  'busy refusal',
                  contains(
                    'TopiaForge is Busy: this install has a running or unverified process.',
                  ),
                ),
              ),
            );
            expect(child, isNull);
            return;
          }
          if (scenario == 'process refusal') {
            await expectLater(
              session.launch(2),
              throwsA(
                isA<StateError>().having(
                  (error) => error.message,
                  'message',
                  contains(
                    'TopiaForge could not be started. No mod state was changed.',
                  ),
                ),
              ),
            );
          } else {
            await session.launch(2);
          }
          expect(child, isNotNull);
          expect(
            child!.environment['TOPIAFORGE_DATA_ROOT'],
            overlay.launcherRoot,
          );
          expect(child!.environment['TOPIAFORGE_DEV_PEERS'], runId);
          expect(child!.environment['TOPIAFORGE_LIVE_MULTIPLAYER'], '1');
          if (scenario == 'start') {
            final requestPath = child!
                .environment[ProfileLaunchConfigurationV4.environmentVariable]!;
            final request = StagedLaunchRequest.decode(
              File(requestPath).readAsStringSync(),
            );
            expect(request, isA<StagedV5LaunchRequest>());
            expect(request.session!.devInstance!.index, 0);
            expect(request.session!.devInstance!.count, 2);
          }
        });
      }
    },
    skip: !Platform.isWindows
        ? 'Development peers require Windows creation receipts.'
        : false,
  );
}
