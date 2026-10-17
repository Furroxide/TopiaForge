part of 'launcher_data_test.dart';

/// Only host-session and join-session launches stage request V5; every
/// other launch stages V4 exactly as the V4 writer writes it.
void _registerLaunchV5IntegrationTests({
  required Directory Function() root,
  required Directory Function() dataRoot,
  required Directory Function() repositoryRoot,
  required Directory Function() gameRoot,
}) {
  group('V5 session launches', () {
    final settings = MultiplayerSettings(
      displayName: 'Sam',
    ).acknowledgePrivacyDisclosure();
    const host = '203.0.113.77';
    const port = 47819;

    LaunchSessionRequest join(LaunchSessionTarget target) =>
        LaunchSessionRequest.join(
          intentId: 'intent-1',
          settings: settings,
          privacy: settings.privacyForSession(allowDirect: true),
          target: target,
        );

    LaunchDirectTarget direct() => LaunchDirectTarget(
      host: host,
      port: port,
      serverId: 'c' * 64,
      descriptorSha256: 'd' * 64,
      packageDigest: 'e' * 64,
    );

    Future<(LocalLauncherRepository, GameInstall, List<String>)>
    prepare() async {
      final staged = <String>[];
      final (repo, install) = await _prepareProfileLaunchRepository(
        dataRoot: dataRoot(),
        repositoryRoot: repositoryRoot(),
        gameRoot: gameRoot(),
        starter: (request) async {
          final path = request
              .environment[ProfileLaunchConfigurationV4.environmentVariable]!;
          staged.add(File(path).readAsStringSync());
          return 501 + staged.length;
        },
      );
      await repo.installPackage(_v6Package(root()).path, install);
      return (repo, install, staged);
    }

    test('a normal launch stages V4 exactly as the V4 writer does', () async {
      final (repo, install, staged) = await prepare();
      final result = await repo.launch(install, _v6Profile());
      expect(result.processStarted, isTrue, reason: result.message);
      final text = staged.single;
      final v4 = ProfileLaunchConfigurationV4.fromJson(jsonDecode(text));
      expect(text, LaunchTransport.writeProfile(v4));
      expect(StagedLaunchRequest.forLaunch(v4).encode(), text);
      expect(StagedLaunchRequest.decode(text), isA<StagedV4LaunchRequest>());
      expect(text, isNot(contains('"session"')));
      expect(result.latestActivity!.command, 'launch-target');
    });

    test('a host stages V5 around the same executable profile', () async {
      final (repo, install, staged) = await prepare();
      final hostSession = LaunchSessionRequest.host(
        intentId: 'intent-2',
        settings: settings,
        privacy: SessionPrivacy.relayOnly,
        host: LaunchHostOptions(name: "Sam's Sandbox"),
      );
      final result = await repo.launchSession(
        install,
        _v6Profile(),
        hostSession,
      );
      expect(result.processStarted, isTrue, reason: result.message);
      final request = StagedLaunchRequest.decode(staged.single);
      expect(request, isA<StagedV5LaunchRequest>());
      expect(request.command, 'host-session');
      expect(request.executable.command, 'launch-target');
      expect(request.executable.plan!.targetId, 'play.mod.menu');
      expect(request.session!.intentId, 'intent-2');
      expect(request.encode(), staged.single);
      expect(result.latestActivity!.command, 'host-session');
      expect(result.latestActivity!.isSessionLaunch, isTrue);
    });

    test('a join starts at the main menu and refuses a target', () async {
      final (repo, install, staged) = await prepare();
      expect(
        () => repo.launchSession(
          install,
          _v6Profile(),
          join(direct()),
          selectionOverride: LaunchSelection.target(
            LaunchRequest(targetId: 'play.mod.menu'),
          ),
        ),
        throwsArgumentError,
      );
      final result = await repo.launchSession(
        install,
        _v6Profile(),
        join(direct()),
      );
      expect(result.processStarted, isTrue, reason: result.message);
      final request = StagedLaunchRequest.decode(staged.single);
      expect(request.command, 'join-session');
      expect(request.executable.command, 'main-menu');
      expect(request.executable.plan, isNull);
      expect(result.latestActivity!.command, 'join-session');
    });

    test('a restart V5 cannot carry never stops the running game', () async {
      var running = false;
      var created = 0;
      final stopped = <LaunchProcessIdentity>[];
      // The launcher owns a started process only when its receipt names the
      // game image. On Linux and macOS a Windows build starts under the
      // configured Wine command, so the program started is not the image:
      // the receipt names the image, so the game is owned on every platform.
      final image = p.join(gameRoot().path, 'Robotopia.exe');
      final (repo, install) = await _prepareProfileLaunchRepository(
        dataRoot: dataRoot(),
        repositoryRoot: repositoryRoot(),
        gameRoot: gameRoot(),
        creator: (_) async {
          created++;
          running = true;
          final identity = LaunchProcessIdentity(
            pid: 700 + created,
            executablePath: image,
            startTimeUtc: DateTime.utc(2026, 10, 16, 0, 0, created),
            nativeStartToken: 'generation:${800 + created}',
          );
          return LaunchProcessReceipt(pid: identity.pid, identity: identity);
        },
        probe: (_) async => running,
        liveness: (_) async => running,
        stopper: (identity) async {
          stopped.add(identity);
          running = false;
          return true;
        },
      );
      await repo.installPackage(_v6Package(root()).path, install);
      final first = await repo.launch(install, _v6Profile());
      expect(first.processStarted, isTrue, reason: first.message);
      expect(
        first.process,
        isNotNull,
        reason: 'The game must be owned, or no restart could stop it.',
      );

      final hostSession = LaunchSessionRequest.host(
        intentId: 'intent-3',
        settings: settings,
        privacy: SessionPrivacy.relayOnly,
        host: LaunchHostOptions(name: "Sam's Sandbox"),
      );
      final safe = _v6Profile().copyWith(
        launchSettings: const LaunchSettings(safeMode: true),
      );
      final inheriting = _v6Profile().copyWith(inheritManagerModState: true);
      for (final (profile, session, reason) in [
        (safe, hostSession, 'Safe Mode'),
        (safe, join(direct()), 'Safe Mode'),
        (inheriting, join(direct()), "manager's mod state"),
      ]) {
        final result = await repo.launchSession(
          install,
          profile,
          session,
          restart: true,
        );
        expect(result.started, isFalse);
        expect(result.message, contains(reason));
        expect(result.message, contains('No process was stopped.'));
      }
      expect(stopped, isEmpty);
      expect(running, isTrue);
      expect(created, 1);

      // A host from an inheriting profile is fine, and does replace the game.
      final replaced = await repo.launchSession(
        install,
        inheriting,
        hostSession,
        restart: true,
      );
      expect(replaced.processStarted, isTrue, reason: replaced.message);
      expect(stopped, hasLength(1));
    });

    test('the direct address exists only in the staged request', () async {
      final (repo, install, staged) = await prepare();
      final result = await repo.launchSession(
        install,
        _v6Profile(),
        join(direct()),
      );
      expect(result.processStarted, isTrue, reason: result.message);
      expect(staged.single, contains(host));
      final request = StagedLaunchRequest.decode(staged.single);
      final requestFile = p.join(
        gameRoot().path,
        'BepInEx',
        'TopiaForge',
        'staging',
        'launch-profile-${LaunchStorageKeys.request(request.requestId)}.json',
      );
      expect(File(requestFile).readAsStringSync(), staged.single);
      void addressless(String text, String label) {
        expect(text, isNot(contains(host)), reason: label);
        expect(text, isNot(contains('$port')), reason: label);
      }

      addressless(result.message, 'launch result');
      addressless('${result.latestActivity}', 'activity');
      for (final entry in [
        ...dataRoot().listSync(recursive: true),
        ...gameRoot().listSync(recursive: true),
      ].whereType<File>()) {
        if (p.equals(entry.path, requestFile)) continue;
        final name = p.basename(entry.path);
        if (!name.endsWith('.json') && !name.endsWith('.log')) continue;
        addressless(entry.readAsStringSync(), entry.path);
      }
    });
  });
}
