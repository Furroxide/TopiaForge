import 'dart:convert';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_join_content_fixtures.dart';
import 'multiplayer_join_rig.dart';

/// The data half of the server-content cleanup: a plan, version by version,
/// then exactly that plan applied, or refused.
void main() {
  final zeta = TestArchive('server.zeta', '1.0.0', name: 'Zeta');

  /// The standard player, joined to server A (the standard join) and to
  /// server B, which runs Delta, which A runs too, and Zeta.
  Future<(JoinRig, String, String)> twoServers({
    GameRunningProbe? probe,
  }) async {
    final rig = await JoinRig.create(probe: probe);
    await preparePlayer(rig);
    final (serverA, planA) = await StandardJoin.plan();
    rig.cache([...StandardJoin.downloads, zeta]);
    final first = rig.open();
    await first.installJoinContent(rig.install, installingJoin(serverA, planA));
    await first.prepareJoinProfile(rig.install, preparingJoin(serverA, planA));
    // Another host: any 32-byte key names another server.
    final serverB = await verifiedServer(
      [StandardJoin.delta.package, zeta.package],
      name: 'Other',
      signingKey: List.filled(32, 0x42),
    );
    expect(serverB.serverId, isNot(serverA.serverId));
    final planB = planJoin(serverB, {
      StandardJoin.delta.sha256Hex: JoinPackageResolution.installed(
        StandardJoin.delta.manifest,
      ),
      zeta.sha256Hex: zeta.offeredAt('https://packages.example/zeta.zip'),
    });
    await first.installJoinContent(rig.install, installingJoin(serverB, planB));
    await first.prepareJoinProfile(rig.install, preparingJoin(serverB, planB));
    return (
      rig,
      JoinPlanner.derivedProfileId(serverA.serverId),
      JoinPlanner.derivedProfileId(serverB.serverId),
    );
  }

  Map<String, ServerContentVersion> byVersion(ServerContentCleanupPlan plan) =>
      {for (final item in plan.versions) '${item.modId}@${item.version}': item};

  test('the plan lists what joins installed, its size and its pins', () async {
    final (rig, a, b) = await twoServers();
    final plan = await rig.repository.planServerContentCleanup(rig.install);
    final versions = byVersion(plan);
    // The player's own installs never appear, not even Gamma, which A pins.
    expect(versions.keys.toSet(), {
      'own.alpha@2.0.0',
      'own.beta@1.5.0',
      'server.delta@1.0.0',
      'server.zeta@1.0.0',
    });
    expect(versions['server.delta@1.0.0']!.serverProfileIds, [a, b]..sort());
    expect(versions['server.zeta@1.0.0']!.serverProfileIds, [b]);
    for (final version in plan.versions) {
      expect(version.sizeBytes, greaterThan(0));
      expect(version.removable, isTrue, reason: version.modId);
    }
    expect(plan.profiles.map((item) => item.id).toSet(), {a, b});
    expect(
      plan.removableBytes,
      plan.versions.fold<int>(0, (total, item) => total + item.sizeBytes),
    );
  });

  test(
    'applying it removes exactly that, and the player keeps their own',
    () async {
      final (rig, a, b) = await twoServers();
      final before = await rig.playerView();
      final plan = await rig.repository.planServerContentCleanup(rig.install);
      final result = await rig.repository.applyServerContentCleanup(
        rig.install,
        plan,
      );
      expect(result.applied, isTrue, reason: result.message);
      expect(result.message, isEmpty);
      expect(result.removedVersions.toSet(), {
        'own.alpha@2.0.0',
        'own.beta@1.5.0',
        'server.delta@1.0.0',
        'server.zeta@1.0.0',
      });
      expect(result.removedProfiles.toSet(), {a, b});
      expect(result.freedBytes, greaterThanOrEqualTo(plan.removableBytes));
      for (final (id, version) in [
        ('own.alpha', '2.0.0'),
        ('own.beta', '1.5.0'),
        ('server.delta', '1.0.0'),
        ('server.zeta', '1.0.0'),
      ]) {
        expect(packageFolder(rig.game, id, version).existsSync(), isFalse);
      }
      // The player's own versions stay, and so do their records; the records
      // the joins added go with the mods.
      expect(
        packageFolder(rig.game, 'own.alpha', '1.0.0').existsSync(),
        isTrue,
      );
      expect(
        packageFolder(rig.game, 'own.gamma', '1.0.0').existsSync(),
        isTrue,
      );
      final records = managerRecords(rig.game);
      expect(records.keys.toSet(), {'own.alpha', 'own.beta', 'own.gamma'});
      for (final archive in [...StandardJoin.downloads, zeta]) {
        expect(cachedArchive(rig.dataRoot, archive).existsSync(), isFalse);
      }
      final after = await rig.playerView();
      expect(
        after.records,
        before.records
          ..remove('server.delta')
          ..remove('server.zeta'),
      );
      expect(after.loads, before.loads);
      expect(rig.storedProfiles(servers: true), isEmpty);
      expect(
        ServerContentRecord.read(
          serverContentFile(rig.game).readAsBytesSync(),
        )!.record.isEmpty,
        isTrue,
      );
    },
  );

  test('content another server still pins is kept', () async {
    final (rig, a, b) = await twoServers();
    final plan = await rig.repository.planServerContentCleanup(
      rig.install,
      serverProfileIds: {a},
    );
    final versions = byVersion(plan);
    expect(
      versions['server.delta@1.0.0']!.kept,
      ServerContentKept.pinnedByKeptServer,
    );
    expect(
      versions['server.zeta@1.0.0']!.kept,
      ServerContentKept.pinnedByKeptServer,
    );
    expect(versions['own.alpha@2.0.0']!.removable, isTrue);
    expect(plan.profiles.map((item) => item.id), [a]);
    final result = await rig.repository.applyServerContentCleanup(
      rig.install,
      plan,
    );
    expect(result.applied, isTrue);
    expect(
      packageFolder(rig.game, 'server.delta', '1.0.0').existsSync(),
      isTrue,
    );
    expect(packageFolder(rig.game, 'own.alpha', '2.0.0').existsSync(), isFalse);
    final remaining = (await rig.repository.loadSnapshot()).profiles
        .where(ServerProfiles.isServerProfile)
        .map((profile) => profile.id);
    expect(remaining, [b]);
  });

  test('content a profile of the player\'s own uses is kept', () async {
    final (rig, _, _) = await twoServers();
    // The player turns on Delta, a mod a join brought, and pins Zeta in a
    // profile of their own.
    await rig.repository.setModEnabled(rig.install, 'server.delta', true);
    final snapshot = await rig.repository.loadSnapshot();
    await rig.repository.saveProfiles([
      ...snapshot.profiles,
      const LauncherProfile(
        id: 'zeta-fan',
        name: 'Zeta fan',
        enabledMods: {'server.zeta'},
        selectedVersions: {'server.zeta': '1.0.0'},
      ),
    ], snapshot.selectedProfileId);
    final plan = await rig.repository.planServerContentCleanup(rig.install);
    final versions = byVersion(plan);
    expect(
      versions['server.delta@1.0.0']!.kept,
      ServerContentKept.usedByProfile,
    );
    expect(
      versions['server.zeta@1.0.0']!.kept,
      ServerContentKept.usedByProfile,
    );
    expect(versions['own.alpha@2.0.0']!.removable, isTrue);
    await rig.repository.applyServerContentCleanup(rig.install, plan);
    expect(
      packageFolder(rig.game, 'server.delta', '1.0.0').existsSync(),
      isTrue,
    );
    expect(
      packageFolder(rig.game, 'server.zeta', '1.0.0').existsSync(),
      isTrue,
    );
    final delta = jsonDecode(managerRecords(rig.game)['server.delta']!) as Map;
    expect(delta['enabled'], isTrue);
  });

  test('a plan that has gone stale is refused and removes nothing', () async {
    final (rig, a, _) = await twoServers();
    final plan = await rig.repository.planServerContentCleanup(rig.install);
    // After planning, the player starts using Zeta in a profile of their own.
    final snapshot = await rig.repository.loadSnapshot();
    await rig.repository.saveProfiles([
      ...snapshot.profiles,
      const LauncherProfile(
        id: 'zeta-fan',
        name: 'Zeta fan',
        enabledMods: {'server.zeta'},
      ),
    ], snapshot.selectedProfileId);
    final result = await rig.repository.applyServerContentCleanup(
      rig.install,
      plan,
    );
    expect(result.refusal, ServerContentCleanupRefusal.stale);
    expect(result.message, contains('Review it again'));
    expect(
      packageFolder(rig.game, 'server.zeta', '1.0.0').existsSync(),
      isTrue,
    );
    expect(packageFolder(rig.game, 'own.alpha', '2.0.0').existsSync(), isTrue);
    expect(rig.storedProfiles(servers: true).keys, contains(a));
  });

  test('a cleanup is refused while the game runs', () async {
    var running = false;
    final (rig, _, _) = await twoServers(probe: (_) async => running);
    final plan = await rig.repository.planServerContentCleanup(rig.install);
    running = true;
    final result = await rig.repository.applyServerContentCleanup(
      rig.install,
      plan,
    );
    expect(result.refusal, ServerContentCleanupRefusal.gameRunning);
    expect(result.message, contains('Close the game'));
    expect(
      packageFolder(rig.game, 'server.zeta', '1.0.0').existsSync(),
      isTrue,
    );
  });

  test('a cleanup is refused while a launch holds the game folder', () async {
    final (rig, _, _) = await twoServers();
    final plan = await rig.repository.planServerContentCleanup(rig.install);
    final lease = await LaunchStagingStore(rig.game.path).acquireLaunchLease();
    expect(lease, isNotNull);
    addTearDown(() => lease!.release());
    final result = await rig.repository.applyServerContentCleanup(
      rig.install,
      plan,
    );
    expect(result.refusal, ServerContentCleanupRefusal.busy);
    expect(
      packageFolder(rig.game, 'server.zeta', '1.0.0').existsSync(),
      isTrue,
    );
  });

  test('an empty scope removes only what no server pins', () async {
    final (rig, a, b) = await twoServers();
    // The player removes server B's profile themselves, so Zeta is pinned by
    // no one; Delta is still A's.
    final snapshot = await rig.repository.loadSnapshot();
    await rig.repository.saveProfiles([
      for (final profile in snapshot.profiles)
        if (profile.id != b) profile,
    ], snapshot.selectedProfileId);
    final plan = await rig.repository.planServerContentCleanup(
      rig.install,
      serverProfileIds: const {},
    );
    expect(plan.profiles, isEmpty);
    expect(plan.removable.map((item) => item.modId), ['server.zeta']);
    final result = await rig.repository.applyServerContentCleanup(
      rig.install,
      plan,
    );
    expect(result.removedVersions, ['server.zeta@1.0.0']);
    expect(
      packageFolder(rig.game, 'server.delta', '1.0.0').existsSync(),
      isTrue,
    );
    expect(managerRecords(rig.game).keys, isNot(contains('server.zeta')));
    expect(rig.storedProfiles(servers: true).keys, [a]);
  });

  test(
    'a pending uninstall of a mod is finished with its last version',
    () async {
      final (rig, _, _) = await twoServers();
      // The in-game manager marked Zeta for uninstall while it was loaded.
      final stateFile = managerStateFile(rig.game);
      final state =
          jsonDecode(stateFile.readAsStringSync()) as Map<String, Object?>;
      for (final record in (state['mods'] as List).cast<Map>()) {
        if (record['id'] == 'server.zeta') record['uninstallPending'] = true;
      }
      stateFile.writeAsStringSync(jsonEncode(state));
      final plan = await rig.repository.planServerContentCleanup(rig.install);
      expect(byVersion(plan)['server.zeta@1.0.0']!.removable, isTrue);
      await rig.repository.applyServerContentCleanup(rig.install, plan);
      expect(managerRecords(rig.game).keys, isNot(contains('server.zeta')));
    },
  );

  test(
    'a version the player installs again is theirs, and leaves the record',
    () async {
      final (rig, _, _) = await twoServers();
      // The player installs Alpha 2.0.0 themselves: the folder now holds their
      // own install, with its own receipt.
      await rig.installOwn(StandardJoin.serverAlpha);
      final plan = await rig.repository.planServerContentCleanup(rig.install);
      expect(byVersion(plan).keys, isNot(contains('own.alpha@2.0.0')));
      expect(
        (await rig.repository.loadSnapshot()).installedMods
            .singleWhere((mod) => mod.id == 'own.alpha')
            .version,
        '2.0.0',
      );
      await rig.repository.applyServerContentCleanup(rig.install, plan);
      expect(
        packageFolder(rig.game, 'own.alpha', '2.0.0').existsSync(),
        isTrue,
      );
      final record = ServerContentRecord.read(
        serverContentFile(rig.game).readAsBytesSync(),
      )!.record;
      expect(record.entryFor('own.alpha', '2.0.0'), isNull);
    },
  );

  test(
    'a mod the player uninstalled leaves the record after the next join',
    () async {
      final (rig, _, _) = await twoServers();
      await rig.repository.uninstallMod(rig.install, 'server.zeta');
      final (server, plan) = await StandardJoin.plan();
      expect(
        await rig.repository.prepareJoinProfile(
          rig.install,
          preparingJoin(server, plan),
        ),
        isA<JoinProfilePrepared>(),
      );
      final record = ServerContentRecord.read(
        serverContentFile(rig.game).readAsBytesSync(),
      )!.record;
      expect(record.entryFor('server.zeta', '1.0.0'), isNull);
      expect(record.addedRecords, isNot(contains('server.zeta')));
      expect(record.addedRecords, contains('server.delta'));
    },
  );
}
