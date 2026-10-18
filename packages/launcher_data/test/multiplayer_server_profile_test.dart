import 'dart:convert';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'held_file_support.dart';
import 'multiplayer_join_content_fixtures.dart';
import 'multiplayer_join_rig.dart';

/// The server profile a join saves: `mp-<sha16(serverId)>`, "Server · name",
/// nothing inherited, every package pinned; a re-join updates it.
void main() {
  Future<(JoinRig, VerifiedServerDescriptor, JoinPlan)> joined() async {
    final rig = await JoinRig.create();
    await preparePlayer(rig);
    final (server, plan) = await StandardJoin.plan();
    rig.cache(StandardJoin.downloads);
    expect(
      await rig.repository.installJoinContent(
        rig.install,
        installingJoin(server, plan),
      ),
      isA<JoinInstallCompleted>(),
    );
    expect(
      await rig.repository.prepareJoinProfile(
        rig.install,
        preparingJoin(server, plan),
      ),
      isA<JoinProfilePrepared>(),
    );
    return (rig, server, plan);
  }

  Future<LauncherProfile> serverProfile(JoinRig rig) async =>
      (await rig.repository.loadSnapshot()).profiles.singleWhere(
        ServerProfiles.isServerProfile,
      );

  test('a join saves its server profile with every package pinned', () async {
    final (rig, server, plan) = await joined();
    final profile = await serverProfile(rig);
    expect(profile.id, JoinPlanner.derivedProfileId(server.serverId));
    expect(profile.name, "Server · Sam's Sandbox");
    expect(profile.inheritManagerModState, isFalse);
    expect(profile.enabledMods, {
      'own.alpha',
      'own.beta',
      'own.gamma',
      'server.delta',
    });
    expect(profile.selectedVersions, {
      'own.alpha': '2.0.0',
      'own.beta': '1.5.0',
      'own.gamma': '1.0.0',
      'server.delta': '1.0.0',
    });
    expect(profile.revision, 0);
    expect(profile.launchSettings.safeMode, isFalse);
    // The player's selection stays theirs.
    expect((await rig.repository.loadSnapshot()).selectedProfileId, 'default');
    expect(plan.derivedProfile.id, profile.id);
  });

  test('a re-join updates the pins and keeps what the player set', () async {
    final (rig, server, _) = await joined();
    final saved = await serverProfile(rig);
    final edited = saved.copyWith(
      name: 'My name for it',
      launchSettings: const LaunchSettings(
        safeMode: true,
        extraArguments: ['-windowed'],
        environment: {'TF_TRACE': '1'},
      ),
      launchSelection: LaunchSelection.unresolvedLegacy(const {
        'worldSelection': 'city',
      }),
    );
    final snapshot = await rig.repository.loadSnapshot();
    await rig.repository.saveProfiles([
      for (final profile in snapshot.profiles)
        profile.id == saved.id ? edited : profile,
    ], 'explicit');

    final newer = TestArchive('own.alpha', '2.1.0', name: 'Alpha');
    final renamed = await verifiedServer(
      [
        newer.package,
        StandardJoin.serverBeta.package,
        StandardJoin.delta.package,
        StandardJoin.gamma.package,
      ],
      name: "Sam's Zombies",
      issuedAt: 1791850000,
    );
    expect(renamed.serverId, server.serverId);
    final plan = planJoin(renamed, {
      newer.sha256Hex: newer.offeredAt('https://packages.example/a.zip'),
      for (final archive in [
        StandardJoin.serverBeta,
        StandardJoin.delta,
        StandardJoin.gamma,
      ])
        archive.sha256Hex: JoinPackageResolution.installed(archive.manifest),
    });
    rig.cache([newer]);
    expect(
      await rig.repository.installJoinContent(
        rig.install,
        installingJoin(renamed, plan),
      ),
      isA<JoinInstallCompleted>(),
    );
    expect(
      await rig.repository.prepareJoinProfile(
        rig.install,
        preparingJoin(renamed, plan),
      ),
      isA<JoinProfilePrepared>(),
    );
    final updated = await serverProfile(rig);
    expect(updated.id, saved.id);
    // What the join owns follows the server.
    expect(updated.name, "Server · Sam's Zombies");
    expect(updated.selectedVersions['own.alpha'], '2.1.0');
    expect(updated.inheritManagerModState, isFalse);
    expect(updated.launchSettings.safeMode, isFalse);
    // What the player set stays.
    expect(updated.launchSettings.extraArguments, ['-windowed']);
    expect(updated.launchSettings.environment, {'TF_TRACE': '1'});
    expect(updated.launchSelection.toJson(), edited.launchSelection.toJson());
    expect(updated.revision, saved.revision + 2);
    expect((await rig.repository.loadSnapshot()).selectedProfileId, 'explicit');
  });

  test('a re-join with nothing new writes nothing', () async {
    final (rig, server, plan) = await joined();
    final before = rig.profilesFile.readAsBytesSync();
    expect(
      await rig.repository.prepareJoinProfile(
        rig.install,
        preparingJoin(server, plan),
      ),
      isA<JoinProfilePrepared>(),
    );
    expect(rig.profilesFile.readAsBytesSync(), before);
  });

  test('a failed save leaves the earlier server profile intact', () async {
    final (rig, server, _) = await joined();
    final newer = TestArchive('own.alpha', '2.1.0', name: 'Alpha');
    final updated = await verifiedServer([newer.package], issuedAt: 1791850100);
    final plan = planJoin(updated, {
      newer.sha256Hex: newer.offeredAt('https://packages.example/a.zip'),
    });
    final before = rig.profilesFile.readAsBytesSync();
    final release = await holdInPlace(rig.profilesFile);
    if (release == null) return;
    final event = await rig.repository.prepareJoinProfile(
      rig.install,
      preparingJoin(updated, plan),
    );
    await release();
    final failure = event as JoinFailed;
    expect(failure.code, JoinFailureCode.profileSaveFailed);
    expect(failure.detail, contains('your profiles are as they were'));
    expect(rig.profilesFile.readAsBytesSync(), before);
    expect((await serverProfile(rig)).selectedVersions['own.alpha'], '2.0.0');
    expect(
      JoinSession.reduce(
        preparingJoin(updated, plan),
        failure,
      ).outcome!.failure!.code,
      JoinFailureCode.profileSaveFailed,
    );
    expect(server.serverId, updated.serverId);
  });

  test(
    'a profile of the player\'s own under that id is never taken over',
    () async {
      final rig = await JoinRig.create();
      final (server, plan) = await StandardJoin.plan();
      final mine = LauncherProfile(
        id: JoinPlanner.derivedProfileId(server.serverId),
        name: 'Imported',
        inheritManagerModState: true,
      );
      await rig.repository.saveProfiles([
        LauncherProfile.defaultProfile(),
        mine,
      ], 'default');
      final before = rig.profilesFile.readAsBytesSync();
      final event = await rig.repository.prepareJoinProfile(
        rig.install,
        preparingJoin(server, plan),
      );
      final failure = event as JoinFailed;
      expect(failure.code, JoinFailureCode.profileSaveFailed);
      expect(failure.detail, contains('"Imported"'));
      expect(rig.profilesFile.readAsBytesSync(), before);
    },
  );

  group('versions a re-join replaced', () {
    Future<(JoinRig, JoinPlan, VerifiedServerDescriptor)> rejoinedWith({
      bool running = false,
    }) async {
      final (rig, _, _) = await joined();
      final newer = TestArchive('own.alpha', '2.1.0', name: 'Alpha');
      final updated = await verifiedServer([
        newer.package,
        StandardJoin.serverBeta.package,
        StandardJoin.delta.package,
        StandardJoin.gamma.package,
      ], issuedAt: 1791850200);
      final plan = planJoin(updated, {
        newer.sha256Hex: newer.offeredAt('https://packages.example/a.zip'),
        for (final archive in [
          StandardJoin.serverBeta,
          StandardJoin.delta,
          StandardJoin.gamma,
        ])
          archive.sha256Hex: JoinPackageResolution.installed(archive.manifest),
      });
      rig.cache([newer]);
      await rig.repository.installJoinContent(
        rig.install,
        installingJoin(updated, plan),
      );
      if (running) rig.repository = rig.open(probe: (_) async => true);
      expect(
        await rig.repository.prepareJoinProfile(
          rig.install,
          preparingJoin(updated, plan),
        ),
        isA<JoinProfilePrepared>(),
      );
      return (rig, plan, updated);
    }

    test('are removed on their own once nothing pins them', () async {
      final (rig, _, _) = await rejoinedWith();
      expect(
        packageFolder(rig.game, 'own.alpha', '2.0.0').existsSync(),
        isFalse,
      );
      expect(
        packageFolder(rig.game, 'own.alpha', '2.1.0').existsSync(),
        isTrue,
      );
      expect(
        packageFolder(rig.game, 'own.alpha', '1.0.0').existsSync(),
        isTrue,
      );
      expect(
        cachedArchive(rig.dataRoot, StandardJoin.serverAlpha).existsSync(),
        isFalse,
      );
      final record = ServerContentRecord.read(
        serverContentFile(rig.game).readAsBytesSync(),
      )!.record;
      expect(record.entryFor('own.alpha', '2.0.0'), isNull);
      expect(record.entryFor('own.alpha', '2.1.0'), isNotNull);
      // The player's own Alpha keeps its record exactly.
      final alpha = jsonDecode(managerRecords(rig.game)['own.alpha']!) as Map;
      expect(alpha['version'], '1.0.0');
      expect(alpha['enabled'], isTrue);
    });

    test('stay while the game runs', () async {
      final (rig, _, _) = await rejoinedWith(running: true);
      expect(
        packageFolder(rig.game, 'own.alpha', '2.0.0').existsSync(),
        isTrue,
      );
    });
  });
}
