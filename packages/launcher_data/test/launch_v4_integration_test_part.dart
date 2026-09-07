part of 'launcher_data_test.dart';

void _registerLaunchV4IntegrationTests({
  required Directory Function() root,
  required Directory Function() dataRoot,
  required Directory Function() repositoryRoot,
  required Directory Function() gameRoot,
}) {
  group('V4 production integration', () {
    test(
      'target wire and immediate acknowledgement match exact installed plan',
      () async {
        late ProfileLaunchConfigurationV4 command;
        final (repo, install) = await _prepareProfileLaunchRepository(
          dataRoot: dataRoot(),
          repositoryRoot: repositoryRoot(),
          gameRoot: gameRoot(),
          starter: (request) async {
            final path = request
                .environment[ProfileLaunchConfigurationV4.environmentVariable]!;
            command = ProfileLaunchConfigurationV4.fromJson(
              jsonDecode(File(path).readAsStringSync()),
            );
            final key = p.basename(path).substring('launch-profile-'.length);
            File(
              p.join(p.dirname(path), 'launch-outcome-$key'),
            ).writeAsStringSync(
              jsonEncode(
                LaunchOutcome(
                  kind: 'launch',
                  requestId: command.requestId,
                  command: command.command,
                  sessionId: 'real-session',
                  sequence: 4,
                  phase: 'running',
                  status: 'succeeded',
                ).toJson(),
              ),
            );
            return 401;
          },
        );
        await repo.installPackage(_v6Package(root()).path, install);
        final profile = _v6Profile();
        final preview = await repo.previewLaunch(install, profile);
        expect(
          preview.canLaunch,
          isTrue,
          reason: preview.issues.map((i) => i.message).join(' '),
        );
        final result = await repo.launch(install, profile);
        expect(result.processStarted, isTrue, reason: result.message);
        expect(command.command, 'launch-target');
        expect(command.profileRevision, 0);
        expect(command.plan!.targetId, 'play.mod.menu');
        expect(command.plan!.worldId, 'play.mod.world');
        expect(command.plan!.transition, 'additive-arena');
        expect(command.packages.map((item) => item.id), ['play.mod']);
        expect(command.digest, preview.packageDigest);
        expect(result.latestActivity!.sessionStarted, isTrue);
        expect(result.latestActivity!.requestId, command.requestId);
      },
    );

    test(
      'snapshot computes each profile from its own enabled package set',
      () async {
        final (repo, install) = await _prepareProfileLaunchRepository(
          dataRoot: dataRoot(),
          repositoryRoot: repositoryRoot(),
          gameRoot: gameRoot(),
          starter: (_) async => 402,
        );
        await repo.installPackage(_v6Package(root()).path, install);
        final profiles = await repo.saveProfiles([
          _v6Profile(),
          _v6Profile().copyWith(id: 'disabled', enabledMods: {}),
        ], 'play-profile');
        final snapshot = await repo.loadSnapshot();
        expect(
          snapshot.previewsByProfile.keys,
          unorderedEquals(profiles.map((item) => item.id)),
        );
        expect(snapshot.previewsByProfile['play-profile']!.canLaunch, isTrue);
        final disabled = snapshot.previewsByProfile['disabled']!;
        expect(disabled.canLaunch, isFalse);
        expect(disabled.resolution!.plan, isNull);
        expect(
          disabled.blocks.map((item) => item.code),
          contains(LaunchBlockCode.targetPackageDisabled),
        );
      },
    );

    test(
      'disposing during the last preflight prevents process creation',
      () async {
        final entered = Completer<void>();
        final proceed = Completer<void>();
        var probes = 0;
        var starts = 0;
        final (repo, install) = await _prepareProfileLaunchRepository(
          dataRoot: dataRoot(),
          repositoryRoot: repositoryRoot(),
          gameRoot: gameRoot(),
          starter: (_) async {
            starts++;
            return 403;
          },
          probe: (_) async {
            probes++;
            if (probes == 2) {
              entered.complete();
              await proceed.future;
            }
            return false;
          },
        );
        final pending = repo.launch(
          install,
          const LauncherProfile(id: 'menu', name: 'Menu'),
        );
        await entered.future;
        await repo.dispose();
        proceed.complete();
        final result = await pending;
        expect(result.processStarted, isFalse);
        expect(starts, 0);
        expect(
          Directory(
            p.join(gameRoot().path, 'BepInEx', 'TopiaForge', 'staging'),
          ).listSync().where(
            (file) => p.basename(file.path).startsWith('launch-profile-'),
          ),
          isEmpty,
        );
      },
    );

    test(
      'disposal after process creation retains truthful receipt without starting monitor',
      () async {
        final entered = Completer<void>();
        final receipt = Completer<int>();
        var livenessCalls = 0;
        final (repo, install) = await _prepareProfileLaunchRepository(
          dataRoot: dataRoot(),
          repositoryRoot: repositoryRoot(),
          gameRoot: gameRoot(),
          starter: (_) {
            entered.complete();
            return receipt.future;
          },
          identityReader: (pid, executable) async => LaunchProcessIdentity(
            pid: pid,
            startTimeUtc: DateTime.utc(2026),
            executablePath: executable,
          ),
          liveness: (_) async {
            livenessCalls++;
            return true;
          },
        );
        final pending = repo.launch(
          install,
          const LauncherProfile(id: 'menu', name: 'Menu'),
        );
        await entered.future;
        await repo.dispose();
        receipt.complete(404);
        final result = await pending;
        expect(result.processStarted, isTrue);
        expect(result.latestActivity!.unconfirmed, isTrue);
        expect(result.requestId, isNotEmpty);
        expect(
          livenessCalls,
          0,
          reason: 'Closed repository must never create a new monitor.',
        );
      },
    );

    test(
      'package mutation in final preflight prevents process creation',
      () async {
        var probes = 0;
        var starts = 0;
        final (repo, install) = await _prepareProfileLaunchRepository(
          dataRoot: dataRoot(),
          repositoryRoot: repositoryRoot(),
          gameRoot: gameRoot(),
          starter: (_) async {
            starts++;
            return 405;
          },
          probe: (_) async {
            if (++probes == 2) {
              File(
                p.join(
                  gameRoot().path,
                  'BepInEx',
                  'TopiaForge',
                  'packages',
                  'play.mod',
                  '1.0.0',
                  'PlayMod.dll',
                ),
              ).writeAsStringSync('tampered during preflight');
            }
            return false;
          },
        );
        await repo.installPackage(_v6Package(root()).path, install);
        final result = await repo.launch(install, _v6Profile());
        expect(result.processStarted, isFalse);
        expect(starts, 0);
        expect(result.message, contains('play.mod'));
      },
    );

    test(
      'saved revision changing during preflight prevents stale launch',
      () async {
        var probes = 0;
        var starts = 0;
        late LocalLauncherRepository repository;
        late LauncherProfile saved;
        final (repo, install) = await _prepareProfileLaunchRepository(
          dataRoot: dataRoot(),
          repositoryRoot: repositoryRoot(),
          gameRoot: gameRoot(),
          starter: (_) async {
            starts++;
            return 409;
          },
          probe: (_) async {
            if (++probes == 2) {
              await repository.saveProfiles([
                saved.copyWith(name: 'Changed elsewhere'),
              ], saved.id);
            }
            return false;
          },
        );
        repository = repo;
        saved = (await repo.saveProfiles([
          const LauncherProfile(id: 'menu', name: 'Menu'),
        ], 'menu')).single;
        final result = await repo.launch(install, saved);
        expect(result.processStarted, isFalse);
        expect(starts, 0);
        expect(result.message, contains('changed'));
      },
    );

    test(
      'malformed inherited state retains independent diagnostics without writing',
      () async {
        final (repo, install) = await _prepareProfileLaunchRepository(
          dataRoot: dataRoot(),
          repositoryRoot: repositoryRoot(),
          gameRoot: gameRoot(),
          starter: (_) async => 406,
        );
        final state = _profileManagerState(gameRoot());
        const original =
            '{"mods":[{"id":42,"enabled":true},{"id":"missing.mod","enabled":true}]}';
        state.writeAsStringSync(original);
        final preview = await repo.previewLaunch(
          install,
          LauncherProfile.defaultProfile(),
        );
        expect(preview.canLaunch, isFalse);
        expect(
          preview.issues.map((issue) => issue.message).join(' '),
          contains('missing.mod'),
        );
        expect(
          preview.issues.map((issue) => issue.message).join(' '),
          contains('malformed'),
        );
        expect(state.readAsStringSync(), original);
      },
    );

    test(
      'unreadable default-enabled package absent from manager state blocks launch',
      () async {
        final (repo, install) = await _prepareProfileLaunchRepository(
          dataRoot: dataRoot(),
          repositoryRoot: repositoryRoot(),
          gameRoot: gameRoot(),
          starter: (_) async => 407,
        );
        await repo.installPackage(_v6Package(root()).path, install);
        _profileManagerState(gameRoot()).writeAsStringSync('{"mods":[]}');
        File(
          p.join(
            gameRoot().path,
            'BepInEx',
            'TopiaForge',
            'packages',
            'play.mod',
            '1.0.0',
            'topiaforge.mod.json',
          ),
        ).writeAsStringSync('{malformed');
        final preview = await repo.previewLaunch(
          install,
          LauncherProfile.defaultProfile(),
        );
        expect(preview.canLaunch, isFalse);
        expect(
          preview.issues.map((issue) => issue.message).join(' '),
          contains('play.mod'),
        );
      },
    );

    test(
      'duplicate selected physical versions cannot produce an authoritative plan',
      () async {
        final (repo, install) = await _prepareProfileLaunchRepository(
          dataRoot: dataRoot(),
          repositoryRoot: repositoryRoot(),
          gameRoot: gameRoot(),
          starter: (_) async => 408,
        );
        await repo.installPackage(_v6Package(root()).path, install);
        final source = Directory(
          p.join(
            gameRoot().path,
            'BepInEx',
            'TopiaForge',
            'packages',
            'play.mod',
            '1.0.0',
          ),
        );
        final duplicate = Directory(p.join(source.parent.path, 'alternate'))
          ..createSync();
        for (final file in source.listSync().whereType<File>()) {
          file.copySync(p.join(duplicate.path, p.basename(file.path)));
        }
        final preview = await repo.previewLaunch(install, _v6Profile());
        expect(preview.canLaunch, isFalse);
        expect(
          preview.resolution!.plan,
          isNull,
          reason: 'Both physical owners must reach resolver ambiguity checks.',
        );
        expect(
          preview.blocks.map((block) => block.code),
          contains(LaunchBlockCode.declarationIdAmbiguous),
        );
      },
    );
  });
}

LauncherProfile _v6Profile() => LauncherProfile(
  id: 'play-profile',
  name: 'Play profile',
  inheritManagerModState: false,
  enabledMods: const {'play.mod'},
  launchSelection: LaunchSelection.target(
    LaunchRequest(targetId: 'play.mod.menu'),
  ),
);

File _v6Package(Directory root, {String version = '1.0.0'}) => _createPackage(
  root,
  id: 'play.mod',
  version: version,
  contributions: {
    'gamemodes': [
      {
        'id': 'play.mod.mode',
        'name': 'Mode',
        'implementation': {'type': 'Example.Mode'},
        'sceneChangePolicy': 'end-session',
      },
    ],
    'worlds': [
      {
        'id': 'play.mod.world',
        'name': 'World',
        'content': {
          'kind': 'provider',
          'implementation': {'type': 'Example.Provider'},
        },
        'transitions': ['additive-arena'],
        'spawn': {'kind': 'provider-default'},
        'openToAnyCompatible': true,
      },
    ],
    'launchTargets': [
      {
        'id': 'play.mod.menu',
        'title': 'Play',
        'gamemode': 'play.mod.mode',
        'world': {
          'policy': 'open',
          'default': 'play.mod.world',
          'allowPlayerOverride': true,
        },
        'transition': 'auto',
      },
    ],
  },
);
