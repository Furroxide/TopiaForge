import 'dart:convert';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_join_content_fixtures.dart';
import 'multiplayer_join_rig.dart';

/// A join's install: exactly the server's archives, as one change, without
/// touching anything the player's own profiles load.
void main() {
  test('a join changes nothing the player\'s own profiles load', () async {
    final rig = await JoinRig.create();
    await preparePlayer(rig);
    final (server, plan) = await StandardJoin.plan();
    rig.cache(StandardJoin.downloads);
    final before = await rig.playerView();

    final installed = await rig.repository.installJoinContent(
      rig.install,
      installingJoin(server, plan),
    );
    expect(installed, isA<JoinInstallCompleted>());
    final prepared = await rig.repository.prepareJoinProfile(
      rig.install,
      preparingJoin(server, plan),
    );
    expect(prepared, isA<JoinProfilePrepared>());
    final after = await rig.playerView();

    // The manager records of the player's own mods, byte for byte. The one
    // record added is the mod new to them, and it starts off.
    for (final MapEntry(:key, :value) in before.records.entries) {
      expect(after.records[key], value, reason: key);
    }
    expect(after.records.keys.toSet().difference(before.records.keys.toSet()), {
      'server.delta',
    });
    final delta = jsonDecode(after.records['server.delta']!) as Map;
    expect(delta['enabled'], isFalse);
    expect(after.stateRest, before.stateRest);
    // Every profile that is not a server profile, byte for byte, and what
    // each one loads.
    expect(after.profiles, before.profiles);
    expect(after.loads, before.loads);
    // The Mods list shows Alpha and Beta as the player's own versions, and
    // Delta, off.
    expect(after.mods.where((mod) => mod.startsWith('own.')), [
      ...before.mods.where((mod) => mod.startsWith('own.')),
    ]);
    expect(after.mods, contains('server.delta@1.0.0 off 1.0.0'));

    // The server profile loads exactly what the server runs.
    final serverProfile = (await rig.repository.loadSnapshot()).profiles
        .singleWhere(ServerProfiles.isServerProfile);
    expect(await rig.loadsOf(serverProfile), [
      'own.alpha@2.0.0',
      'own.beta@1.5.0',
      'own.gamma@1.0.0',
      'server.delta@1.0.0',
    ]);
    expect(joinFolders(rig.game), isEmpty);
  });

  test(
    'a join with no mod new to the player leaves the state file as it is',
    () async {
      final rig = await JoinRig.create();
      await preparePlayer(rig);
      final server = await verifiedServer([StandardJoin.serverAlpha.package]);
      final plan = planJoin(server, {
        StandardJoin.serverAlpha.sha256Hex: StandardJoin.serverAlpha.offeredAt(
          'https://packages.example/alpha.topiaforgemod',
        ),
      });
      rig.cache([StandardJoin.serverAlpha]);
      final state = managerStateFile(rig.game).readAsBytesSync();
      expect(
        await rig.repository.installJoinContent(
          rig.install,
          installingJoin(server, plan),
        ),
        isA<JoinInstallCompleted>(),
      );
      expect(managerStateFile(rig.game).readAsBytesSync(), state);
      expect(
        packageFolder(rig.game, 'own.alpha', '2.0.0').existsSync(),
        isTrue,
      );
    },
  );

  test('the record names each archive the join installed', () async {
    final rig = await JoinRig.create();
    await preparePlayer(rig);
    final (server, plan) = await StandardJoin.plan();
    rig.cache(StandardJoin.downloads);
    await rig.repository.installJoinContent(
      rig.install,
      installingJoin(server, plan),
    );
    final record = ServerContentRecord.read(
      serverContentFile(rig.game).readAsBytesSync(),
    )!;
    expect(record.damaged, isFalse);
    final entries = {
      for (final entry in record.record.entries)
        '${entry.modId}@${entry.version}': entry,
    };
    expect(entries.keys.toSet(), {
      'own.alpha@2.0.0',
      'own.beta@1.5.0',
      'server.delta@1.0.0',
    });
    // Versions of the player's own mods are hidden from their profiles; the
    // mod new to them is not, and its record is the join's.
    expect(entries['own.alpha@2.0.0']!.hidden, isTrue);
    expect(entries['own.beta@1.5.0']!.hidden, isTrue);
    expect(entries['server.delta@1.0.0']!.hidden, isFalse);
    expect(record.record.addedRecords, {'server.delta'});
    expect(entries['server.delta@1.0.0']!.sha256, StandardJoin.delta.sha256Hex);
  });

  test(
    'a mod named only by a profile of the player\'s own stays hidden',
    () async {
      final rig = await JoinRig.create();
      await rig.repository.saveProfiles([
        LauncherProfile.defaultProfile(),
        const LauncherProfile(
          id: 'wishful',
          name: 'Wishful',
          enabledMods: {'server.delta'},
        ),
      ], 'default');
      final server = await verifiedServer([StandardJoin.delta.package]);
      final plan = planJoin(server, {
        StandardJoin.delta.sha256Hex: StandardJoin.delta.offeredAt(
          'https://packages.example/delta.topiaforgemod',
        ),
      });
      rig.cache([StandardJoin.delta]);
      final wishful = (await rig.repository.loadSnapshot()).profiles
          .singleWhere((profile) => profile.id == 'wishful');
      final before = await rig.repository.previewLaunch(rig.install, wishful);
      await rig.repository.installJoinContent(
        rig.install,
        installingJoin(server, plan),
      );
      final after = await rig.repository.previewLaunch(rig.install, wishful);
      expect(after.packages, before.packages);
      expect(after.canLaunch, before.canLaunch);
      // Recorded off all the same, so a game started without the launcher
      // never turns it on by default.
      final record = jsonDecode(managerRecords(rig.game)['server.delta']!);
      expect(record['enabled'], isFalse);
    },
  );

  test('a version installed as another build is never replaced', () async {
    final rig = await JoinRig.create();
    final mine = TestArchive(
      'own.alpha',
      '2.0.0',
      name: 'Alpha',
      payload: 'mine',
    );
    await rig.installOwn(mine);
    final server = await verifiedServer([StandardJoin.serverAlpha.package]);
    final plan = planJoin(server, {
      StandardJoin.serverAlpha.sha256Hex: StandardJoin.serverAlpha.offeredAt(
        'https://packages.example/alpha.topiaforgemod',
      ),
    });
    rig.cache([StandardJoin.serverAlpha]);
    final state = managerStateFile(rig.game).readAsBytesSync();
    final event = await rig.repository.installJoinContent(
      rig.install,
      installingJoin(server, plan),
    );
    final failure = event as JoinFailed;
    expect(failure.code, JoinFailureCode.installFailed);
    expect(failure.rollbackComplete, isTrue);
    expect(failure.detail, contains('Alpha 2.0.0'));
    expect(failure.detail, contains('Uninstall it in Mods, then join again'));
    expect(managerStateFile(rig.game).readAsBytesSync(), state);
    expect(serverContentFile(rig.game).existsSync(), isFalse);
    expect(joinFolders(rig.game), isEmpty);
    // The planner says so before anything is downloaded.
    final catalog = await rig.repository.loadExactPackageCatalog(rig.install);
    final replanned = const JoinPlanner().planFromCatalog(
      server: server,
      catalog: catalog,
      environment: testJoinEnvironment,
      mayContactHost: true,
    );
    expect(replanned.isJoinable, isFalse);
    expect(
      replanned.blockers.single.message,
      contains('joining never replaces your copy'),
    );
  });

  test('the exact build already installed needs nothing', () async {
    final rig = await JoinRig.create();
    await rig.installOwn(StandardJoin.serverAlpha);
    final server = await verifiedServer([StandardJoin.serverAlpha.package]);
    // Planned before the player installed it themselves.
    final plan = planJoin(server, {
      StandardJoin.serverAlpha.sha256Hex: StandardJoin.serverAlpha.offeredAt(
        'https://packages.example/alpha.topiaforgemod',
      ),
    });
    expect(
      await rig.repository.installJoinContent(
        rig.install,
        installingJoin(server, plan),
      ),
      isA<JoinInstallCompleted>(),
    );
    // The player's own install, not server content.
    expect(serverContentFile(rig.game).existsSync(), isFalse);
  });

  group('a running game', () {
    test('that the launcher did not start ends the join', () async {
      final rig = await JoinRig.create(probe: (_) async => true);
      await preparePlayer(rig);
      final (server, plan) = await StandardJoin.plan();
      rig.cache(StandardJoin.downloads);
      final records = managerRecords(rig.game);
      final event = await rig.repository.installJoinContent(
        rig.install,
        installingJoin(server, plan),
      );
      final failure = event as JoinFailed;
      expect(failure.code, JoinFailureCode.gameRunning);
      expect(failure.gameOwnership, JoinGameOwnership.foreign);
      expect(failure.detail, contains('Close the game, then join again'));
      expect(managerRecords(rig.game), records);
      expect(
        packageFolder(rig.game, 'server.delta', '1.0.0').existsSync(),
        isFalse,
      );
      final ended = JoinSession.reduce(installingJoin(server, plan), failure);
      expect(ended.outcome!.failure!.gameOwnership, JoinGameOwnership.foreign);
    });

    test(
      'that the launcher started is closed first, then the join installs',
      () async {
        var running = false;
        var created = 0;
        final stopped = <LaunchProcessIdentity>[];
        late JoinRig rig;
        rig = await JoinRig.create(
          withRuntime: true,
          probe: (_) async => running,
          creator: (_) async {
            created++;
            running = true;
            final identity = LaunchProcessIdentity(
              pid: 900 + created,
              executablePath: rig.install.executablePath,
              startTimeUtc: DateTime.utc(2026, 10, 18, 0, 0, created),
              nativeStartToken: 'generation:$created',
            );
            return LaunchProcessReceipt(pid: identity.pid, identity: identity);
          },
          liveness: (_) async => running,
          stopper: (identity) async {
            stopped.add(identity);
            running = false;
            return true;
          },
        );
        final profile = (await rig.repository.loadSnapshot()).profiles.first;
        final launched = await rig.repository.launch(rig.install, profile);
        expect(launched.process, isNotNull, reason: launched.message);
        final (server, plan) = await StandardJoin.plan();
        rig.cache(StandardJoin.downloads);
        await rig.installOwn(StandardJoin.gamma);
        final event = await rig.repository.installJoinContent(
          rig.install,
          installingJoin(server, plan),
        );
        expect(event, isA<JoinInstallCompleted>());
        expect(stopped, hasLength(1));
        expect(
          packageFolder(rig.game, 'server.delta', '1.0.0').existsSync(),
          isTrue,
        );
      },
    );

    test('that will not close ends the join as owned', () async {
      var running = false;
      late JoinRig rig;
      rig = await JoinRig.create(
        withRuntime: true,
        probe: (_) async => running,
        creator: (_) async {
          running = true;
          final identity = LaunchProcessIdentity(
            pid: 950,
            executablePath: rig.install.executablePath,
            startTimeUtc: DateTime.utc(2026, 10, 18),
            nativeStartToken: 'generation:950',
          );
          return LaunchProcessReceipt(pid: identity.pid, identity: identity);
        },
        liveness: (_) async => null,
        stopper: (_) async => throw StateError('not stopped'),
      );
      final profile = (await rig.repository.loadSnapshot()).profiles.first;
      expect(
        (await rig.repository.launch(rig.install, profile)).process,
        isNotNull,
      );
      final (server, plan) = await StandardJoin.plan();
      rig.cache(StandardJoin.downloads);
      final event = await rig.repository.installJoinContent(
        rig.install,
        installingJoin(server, plan),
      );
      final failure = event as JoinFailed;
      expect(failure.code, JoinFailureCode.gameRunning);
      expect(failure.gameOwnership, JoinGameOwnership.owned);
      expect(failure.detail, contains('could not be closed'));
    });
  });

  test(
    'an install waits for no launch: a held lease ends it at once',
    () async {
      final rig = await JoinRig.create();
      await preparePlayer(rig);
      final (server, plan) = await StandardJoin.plan();
      rig.cache(StandardJoin.downloads);
      final lease = await LaunchStagingStore(
        rig.game.path,
      ).acquireLaunchLease();
      addTearDown(() => lease?.release());
      expect(lease, isNotNull);
      final event = await rig.repository.installJoinContent(
        rig.install,
        installingJoin(server, plan),
      );
      final failure = event as JoinFailed;
      expect(failure.code, JoinFailureCode.installFailed);
      expect(failure.rollbackComplete, isTrue);
      expect(failure.detail, contains('Wait for it to finish'));
      expect(serverContentFile(rig.game).existsSync(), isFalse);
      // And while the install holds it, no launch stages.
      await lease!.release();
    },
  );

  test('a download gone from the cache is asked for again', () async {
    final rig = await JoinRig.create();
    await preparePlayer(rig);
    final (server, plan) = await StandardJoin.plan();
    rig.cache([StandardJoin.serverAlpha, StandardJoin.serverBeta]);
    final event = await rig.repository.installJoinContent(
      rig.install,
      installingJoin(server, plan),
    );
    final failure = event as JoinFailed;
    expect(failure.code, JoinFailureCode.installFailed);
    expect(failure.rollbackComplete, isTrue);
    expect(failure.detail, contains('Join again to download it'));
    for (final archive in StandardJoin.downloads) {
      expect(
        packageFolder(rig.game, archive.id, archive.version).existsSync(),
        isFalse,
      );
    }
    expect(joinFolders(rig.game), isEmpty);
  });

  test('a cancel before anything changed undoes the install', () async {
    final cancellation = CancellationToken();
    final rig = await JoinRig.create(
      hook: (step) {
        if (step == 'staged') cancellation.cancel();
      },
    );
    await preparePlayer(rig);
    final (server, plan) = await StandardJoin.plan();
    rig.cache(StandardJoin.downloads);
    final before = await rig.playerView();
    final installing = JoinSession.reduce(
      installingJoin(server, plan),
      const JoinCancelRequested(),
    );
    final event = await rig.repository.installJoinContent(
      rig.install,
      installing,
      cancellation: cancellation,
    );
    expect(event, isA<JoinInstallUndone>());
    expect(
      JoinSession.reduce(installing, event!).outcome!.wasCancelled,
      isTrue,
    );
    final after = await rig.playerView();
    expect(after.records, before.records);
    expect(after.loads, before.loads);
    expect(serverContentFile(rig.game).existsSync(), isFalse);
    expect(joinFolders(rig.game), isEmpty);
  });

  test('only the installing stage is answered', () async {
    final rig = await JoinRig.create();
    final (server, plan) = await StandardJoin.plan();
    expect(
      await rig.repository.installJoinContent(
        rig.install,
        consentedJoin(server, plan),
      ),
      isNull,
    );
  });

  test('progress counts each package as it is prepared, and a listener '
      'that throws never stops the install', () async {
    final rig = await JoinRig.create();
    await preparePlayer(rig);
    final (server, plan) = await StandardJoin.plan();
    rig.cache(StandardJoin.downloads);
    final reports = <String>[];
    final event = await rig.repository.installJoinContent(
      rig.install,
      installingJoin(server, plan),
      onProgress: (progress) {
        reports.add('${progress.packagesDone}/${progress.packagesTotal}');
        throw StateError('A listener that fails.');
      },
    );
    expect(event, isA<JoinInstallCompleted>());
    // Counted first, then one more as each of the three is staged.
    expect(reports, ['0/3', '1/3', '2/3', '3/3']);
    expect(
      packageFolder(rig.game, 'server.delta', '1.0.0').existsSync(),
      isTrue,
    );

    // A join whose packages are all in place already has nothing to count.
    final again = <JoinInstallProgressed>[];
    expect(
      await rig.repository.installJoinContent(
        rig.install,
        installingJoin(server, plan),
        onProgress: again.add,
      ),
      isA<JoinInstallCompleted>(),
    );
    expect(again, isEmpty);
  });
}
