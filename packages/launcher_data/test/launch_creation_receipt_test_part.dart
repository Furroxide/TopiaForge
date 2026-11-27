part of 'launcher_data_test.dart';

void _registerLaunchCreationReceiptTests({
  required Directory Function() dataRoot,
  required Directory Function() repositoryRoot,
  required Directory Function() gameRoot,
}) {
  group('V4 creation receipt authority', () {
    test(
      'creator receipt alone owns restart and never consults later PID lookup',
      () async {
        var running = false;
        var starts = 0;
        var lookups = 0;
        final stopped = <LaunchProcessIdentity>[];
        final identities = <LaunchProcessIdentity>[];
        final (repo, install) = await _prepareProfileLaunchRepository(
          dataRoot: dataRoot(),
          repositoryRoot: repositoryRoot(),
          gameRoot: gameRoot(),
          creator: (request) async {
            running = true;
            final identity = LaunchProcessIdentity(
              pid: 600 + ++starts,
              startTimeUtc: DateTime.utc(2026, 9, 7, 0, starts),
              executablePath: GameLayout.resolve(
                gameRoot().path,
              )!.executablePath,
              nativeStartToken: 'created:$starts',
            );
            identities.add(identity);
            return LaunchProcessReceipt(pid: identity.pid, identity: identity);
          },
          identityReader: (_, path) async {
            lookups++;
            return LaunchProcessIdentity(
              pid: 999,
              startTimeUtc: DateTime.utc(2026),
              executablePath: path,
              nativeStartToken: 'replacement',
            );
          },
          probe: (_) async => running,
          liveness: (_) async => running,
          stopper: (identity) async {
            stopped.add(identity);
            running = false;
            return true;
          },
        );
        const profile = LauncherProfile(id: 'menu', name: 'Menu');
        final first = await repo.launch(install, profile);
        expect(first.processStarted, isTrue, reason: first.message);
        expect(first.process, same(identities.single));
        expect(first.latestActivity!.process, same(first.process));
        final second = await repo.restart(install, profile);
        expect(second.processStarted, isTrue, reason: second.message);
        expect(stopped.single, same(first.process));
        expect(second.process, same(identities.last));
        expect(second.process, isNot(same(first.process)));
        expect(lookups, 0);
      },
    );

    for (final flaw in [
      'missing',
      'wrong-pid',
      'wrong-image',
      'local-time',
      'missing-token',
    ]) {
      test(
        '$flaw creation identity stays unowned even when a later PID lookup would succeed',
        () async {
          var running = false;
          var lookups = 0;
          var stops = 0;
          final (repo, install) = await _prepareProfileLaunchRepository(
            dataRoot: dataRoot(),
            repositoryRoot: repositoryRoot(),
            gameRoot: gameRoot(),
            creator: (request) async {
              running = true;
              final image = GameLayout.resolve(gameRoot().path)!.executablePath;
              return LaunchProcessReceipt(
                pid: 620,
                identity: flaw == 'missing'
                    ? null
                    : LaunchProcessIdentity(
                        pid: flaw == 'wrong-pid' ? 621 : 620,
                        startTimeUtc: flaw == 'local-time'
                            ? DateTime(2026)
                            : DateTime.utc(2026),
                        executablePath: flaw == 'wrong-image'
                            ? p.join(gameRoot().path, 'unrelated.exe')
                            : image,
                        nativeStartToken: flaw == 'missing-token'
                            ? ''
                            : 'created:620',
                      ),
              );
            },
            identityReader: (pid, path) async {
              lookups++;
              return LaunchProcessIdentity(
                pid: pid,
                startTimeUtc: DateTime.utc(2026),
                executablePath: path,
                nativeStartToken: 'later:620',
              );
            },
            probe: (_) async => running,
            liveness: (_) async => true,
            stopper: (_) async {
              stops++;
              running = false;
              return true;
            },
          );
          const profile = LauncherProfile(id: 'menu', name: 'Menu');
          final first = await repo.launch(install, profile);
          expect(first.processStarted, isTrue, reason: first.message);
          expect(first.processId, 620);
          expect(first.process, isNull);
          expect(first.latestActivity!.unconfirmed, isTrue);
          expect(first.latestActivity!.sessionStarted, isFalse);
          final restart = await repo.restart(install, profile);
          expect(restart.processStarted, isFalse);
          expect(restart.message, contains('not owned'));
          expect(stops, 0);
          expect(lookups, 0);
          expect(running, isTrue);
        },
      );
    }

    test('creator and legacy starter hooks are mutually exclusive', () {
      expect(
        () => LocalLauncherRepository(
          dataRoot: dataRoot().path,
          repositoryRoot: repositoryRoot().path,
          gameProcessCreator: (_) async => const LaunchProcessReceipt(pid: 630),
          gameProcessStarter: (_) async => 631,
        ),
        throwsArgumentError,
      );
    });
  });
}
