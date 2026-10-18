import 'dart:convert';
import 'dart:io';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'held_file_support.dart';
import 'multiplayer_join_content_fixtures.dart';
import 'multiplayer_join_rig.dart';

/// Nothing that happens to the record of server content, or beside it, ever
/// turns a server's version into one the player's own profiles load: a
/// receipt another program holds, a damaged record, a newer launcher's
/// record, or a mod the player turned on for their own play.
void main() {
  /// The standard player, joined to the standard server: Alpha 2.0.0 and
  /// Beta 1.5.0 hidden, Delta new to them.
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

  /// What each profile of the player's own loads, by id.
  Future<Map<String, List<String>>> playerLoads(JoinRig rig) async {
    final snapshot = await rig.repository.loadSnapshot();
    return {
      for (final profile in snapshot.profiles)
        if (!ServerProfiles.isServerProfile(profile))
          profile.id: await rig.loadsOf(profile),
    };
  }

  File receiptOf(JoinRig rig, String id, String version) => File(
    p.join(
      packageFolder(rig.game, id, version).path,
      'topiaforge.install.json',
    ),
  );

  ServerContentRecord storedRecord(JoinRig rig) => ServerContentRecord.read(
    serverContentFile(rig.game).readAsBytesSync(),
  ).record!;

  group('a receipt another program holds', () {
    test('keeps its version through the tidy-up after a re-join', () async {
      final (rig, server, plan) = await joined();
      final before = await playerLoads(rig);
      final release = await holdUnreadable(
        receiptOf(rig, 'own.alpha', '2.0.0'),
      );
      if (release == null) return;
      // Joining the same server again saves nothing new, then tidies up.
      expect(
        await rig.repository.prepareJoinProfile(
          rig.install,
          preparingJoin(server, plan),
        ),
        isA<JoinProfilePrepared>(),
      );
      await release();
      expect(storedRecord(rig).entryFor('own.alpha', '2.0.0'), isNotNull);
      expect(await playerLoads(rig), before);
    });

    test('keeps its version out of a cleanup plan and its record', () async {
      final (rig, _, _) = await joined();
      final before = await playerLoads(rig);
      final release = await holdUnreadable(
        receiptOf(rig, 'own.alpha', '2.0.0'),
      );
      if (release == null) return;
      final plan = await rig.repository.planServerContentCleanup(rig.install);
      expect(
        plan.versions.map((item) => '${item.modId}@${item.version}'),
        isNot(contains('own.alpha@2.0.0')),
      );
      final result = await rig.repository.applyServerContentCleanup(
        rig.install,
        plan,
      );
      await release();
      expect(result.applied, isTrue, reason: result.message);
      expect(
        packageFolder(rig.game, 'own.alpha', '2.0.0').existsSync(),
        isTrue,
      );
      expect(storedRecord(rig).entryFor('own.alpha', '2.0.0'), isNotNull);
      // The server profile went with the cleanup; the player's own profiles
      // still never see the server's Alpha.
      expect(await playerLoads(rig), before);
    });
  });

  test('a mod a join added that the player turned on is theirs', () async {
    final rig = await JoinRig.create();
    await preparePlayer(rig);
    // The player keeps explicit profiles only, so only the manager state
    // says what their own play loads.
    await rig.repository.saveProfiles(const [
      LauncherProfile(
        id: 'explicit',
        name: 'Explicit',
        enabledMods: {'own.alpha', 'own.gamma'},
        selectedVersions: {'own.gamma': '1.0.0'},
      ),
    ], 'explicit');
    final (server, plan) = await StandardJoin.plan();
    rig.cache(StandardJoin.downloads);
    await rig.repository.installJoinContent(
      rig.install,
      installingJoin(server, plan),
    );
    await rig.repository.prepareJoinProfile(
      rig.install,
      preparingJoin(server, plan),
    );
    // Delta came in new and off; the player turns it on, as the in-game
    // manager does.
    await rig.repository.setModEnabled(rig.install, 'server.delta', true);
    // The server drops Delta, and the player joins it again.
    final updated = await verifiedServer([
      StandardJoin.serverAlpha.package,
      StandardJoin.serverBeta.package,
      StandardJoin.gamma.package,
    ], issuedAt: 1791850200);
    final replanned = planJoin(updated, {
      for (final archive in [
        StandardJoin.serverAlpha,
        StandardJoin.serverBeta,
        StandardJoin.gamma,
      ])
        archive.sha256Hex: JoinPackageResolution.installed(archive.manifest),
    });
    expect(
      await rig.repository.prepareJoinProfile(
        rig.install,
        preparingJoin(updated, replanned),
      ),
      isA<JoinProfilePrepared>(),
    );
    expect(
      packageFolder(rig.game, 'server.delta', '1.0.0').existsSync(),
      isTrue,
    );
    final delta = jsonDecode(managerRecords(rig.game)['server.delta']!) as Map;
    expect(delta['enabled'], isTrue);
  });

  group('a damaged record', () {
    test(
      'is repaired failing closed, and the player loads what they did',
      () async {
        final (rig, _, _) = await joined();
        final before = await playerLoads(rig);
        final file = serverContentFile(rig.game);
        final document = jsonDecode(file.readAsStringSync()) as Map;
        for (final entry in document['versions'] as List) {
          if ((entry as Map)['id'] == 'own.alpha') entry['installedAt'] = 1.5;
        }
        final damaged = utf8.encode(jsonEncode(document));
        file.writeAsBytesSync(damaged);
        // The next start repairs it, before anything else reads it.
        rig.repository = rig.open();
        expect(await playerLoads(rig), before);
        final record = storedRecord(rig);
        expect(
          record.withheld.map((item) => '${item.modId}@${item.version}'),
          contains('own.alpha@2.0.0'),
        );
        expect(record.entryFor('own.beta', '1.5.0')!.hidden, isTrue);
        expect(record.addedRecords, {'server.delta'});
        final copies = [
          for (final entity in file.parent.listSync())
            if (p.basename(entity.path).startsWith('server-content.damaged-'))
              entity as File,
        ];
        expect(copies.single.readAsBytesSync(), damaged);
      },
    );

    test('that holds nothing withholds every version no profile of the '
        'player\'s own names', () async {
      final (rig, _, _) = await joined();
      final before = await playerLoads(rig);
      serverContentFile(rig.game).writeAsStringSync('not a record');
      rig.repository = rig.open();
      expect(await playerLoads(rig), before);
      final withheld = {
        for (final item in storedRecord(rig).withheld)
          '${item.modId}@${item.version}',
      };
      expect(withheld, containsAll(['own.alpha@2.0.0', 'own.beta@1.5.0']));
      // What the manager state names stays the player's.
      expect(withheld, isNot(contains('own.alpha@1.0.0')));
      expect(withheld, isNot(contains('server.delta@1.0.0')));
      // The server's profile still loads every version it pins.
      final serverProfile = (await rig.repository.loadSnapshot()).profiles
          .singleWhere(ServerProfiles.isServerProfile);
      expect(
        await rig.loadsOf(serverProfile),
        containsAll(['own.alpha@2.0.0', 'own.beta@1.5.0']),
      );
    });

    test('waits for its repair while a receipt cannot be read', () async {
      final (rig, _, _) = await joined();
      serverContentFile(rig.game).writeAsStringSync('not a record');
      final release = await holdUnreadable(
        receiptOf(rig, 'own.alpha', '2.0.0'),
      );
      if (release == null) return;
      rig.repository = rig.open();
      final snapshot = await rig.repository.loadSnapshot();
      final own = snapshot.profiles.firstWhere((item) => item.id == 'unpinned');
      final preview = await rig.repository.previewLaunch(rig.install, own);
      expect(preview.canLaunch, isFalse);
      expect(
        preview.issues.map((issue) => issue.message).join(' '),
        contains('restart the launcher'),
      );
      expect(serverContentFile(rig.game).readAsStringSync(), 'not a record');
      await release();
      rig.repository = rig.open();
      await rig.repository.loadSnapshot();
      expect(
        storedRecord(rig).withheld.map((item) => item.modId),
        contains('own.alpha'),
      );
    });
  });

  test('a newer launcher\'s record is never touched, and stops what would '
      'need it', () async {
    final (rig, server, plan) = await joined();
    const newer = '{"schemaVersion":2,"future":true}';
    serverContentFile(rig.game).writeAsStringSync(newer);
    rig.repository = rig.open();
    final snapshot = await rig.repository.loadSnapshot();
    final own = snapshot.profiles.firstWhere((item) => item.id == 'unpinned');
    final preview = await rig.repository.previewLaunch(rig.install, own);
    expect(preview.canLaunch, isFalse);
    expect(
      preview.issues.map((issue) => issue.message).join(' '),
      contains('Update the launcher'),
    );
    final install = await rig.repository.installJoinContent(
      rig.install,
      installingJoin(server, plan),
    );
    expect((install as JoinFailed).detail, contains('Update the launcher'));
    await expectLater(
      rig.repository.planServerContentCleanup(rig.install),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          contains('Update the launcher'),
        ),
      ),
    );
    expect(serverContentFile(rig.game).readAsStringSync(), newer);
  });
}
