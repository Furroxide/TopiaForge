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
    final bytes = serverContentFile(rig.game).readAsBytesSync();
    final read = ServerContentRecord.read(bytes);
    expect(read.status, ServerContentStatus.valid);
    final record = read.record!;
    // Written exactly as the contract the game's loader reads spells it.
    expect(record.encode(), bytes);
    final entries = {
      for (final entry in record.entries)
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
    expect(record.addedRecords, {'server.delta'});
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

  test('another server\'s build of a version says to remove that server\'s '
      'content, never to uninstall the player\'s mod', () async {
    final rig = await JoinRig.create();
    await preparePlayer(rig);
    final (first, firstPlan) = await StandardJoin.plan();
    rig.cache(StandardJoin.downloads);
    expect(
      await rig.repository.installJoinContent(
        rig.install,
        installingJoin(first, firstPlan),
      ),
      isA<JoinInstallCompleted>(),
    );
    // A second server runs Alpha 2.0.0 too, as another build. The first
    // server's Alpha 2.0.0 is hidden: the Mods list shows only the player's
    // Alpha 1.0.0, and uninstalling Alpha there would take it too.
    final otherBuild = TestArchive(
      'own.alpha',
      '2.0.0',
      name: 'Alpha',
      payload: 'another build',
    );
    final second = await verifiedServer(
      [otherBuild.package],
      name: 'Other Server',
      issuedAt: 1791850200,
    );
    final catalog = await rig.repository.loadExactPackageCatalog(rig.install);
    final plan = const JoinPlanner().planFromCatalog(
      server: second,
      catalog: catalog,
      environment: testJoinEnvironment,
      mayContactHost: true,
    );
    final message = plan.blockers.single.message;
    expect(message, contains("Remove that server's content, then join again"));
    expect(message, isNot(contains('Mods')));
    // It names no screen, so it holds whichever screen offers the cleanup.
    expect(message, isNot(contains('Settings')));
    // The install says the same, should it get that far.
    final forced = planJoin(second, {
      otherBuild.sha256Hex: otherBuild.offeredAt(
        'https://packages.example/other-alpha.topiaforgemod',
      ),
    });
    rig.cache([otherBuild]);
    final event = await rig.repository.installJoinContent(
      rig.install,
      installingJoin(second, forced),
    );
    expect((event as JoinFailed).detail, contains("Remove that server's"));
    expect(event.detail, contains('Alpha 2.0.0'));
    expect(event.detail, isNot(contains('Settings')));
  });

  test('a join that would fill the record past its bound is refused before '
      'anything changes', () async {
    final rig = await JoinRig.create();
    await preparePlayer(rig);
    final (server, plan) = await StandardJoin.plan();
    rig.cache(StandardJoin.downloads);
    serverContentFile(rig.game).writeAsBytesSync(
      ServerContentRecord.empty().withInstalled([
        for (var index = 0; index < ServerContentRecord.maximumEntries; index++)
          ServerContentEntry(
            modId: 'example.mod$index',
            version: '1.0.0',
            sha256: 'a' * 64,
            receiptSha256: 'b' * 64,
            hidden: true,
            installedAt: 1,
          ),
      ]).encode(),
    );
    final before = await rig.everything(StandardJoin.downloads);
    final event = await rig.repository.installJoinContent(
      rig.install,
      installingJoin(server, plan),
    );
    expect((event as JoinFailed).rollbackComplete, isTrue);
    expect(event.detail, contains('Too many mods are installed for servers'));
    expect(event.detail, contains('then join again'));
    expect(event.detail, isNot(contains('Settings')));
    expect(await rig.everything(StandardJoin.downloads), before);
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
