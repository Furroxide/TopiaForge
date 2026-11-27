import 'dart:io';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'held_file_support.dart';
import 'multiplayer_join_content_fixtures.dart';
import 'multiplayer_join_rig.dart';

/// A join's install is one change: a failure at any step puts back
/// everything this join changed, and only that.
void main() {
  for (final step in [
    'staged',
    'state',
    'record',
    'committed 1',
    'committed 2',
    'committed 3',
  ]) {
    test('a failure at "$step" puts everything back', () async {
      final rig = await JoinRig.create(
        hook: (current) {
          if (current == step) throw StateError('Stopped at $step.');
        },
      );
      await preparePlayer(rig);
      final (server, plan) = await StandardJoin.plan();
      rig.cache(StandardJoin.downloads);
      final before = await rig.everything(StandardJoin.downloads);
      final event = await rig.repository.installJoinContent(
        rig.install,
        installingJoin(server, plan),
      );
      final failure = event as JoinFailed;
      expect(failure.code, JoinFailureCode.installFailed);
      expect(failure.rollbackComplete, isTrue);
      expect(failure.detail, isNot(contains('Stopped at')));
      expect(failure.detail, contains('then join again'));
      expect(await rig.everything(StandardJoin.downloads), before);
      expect(joinFolders(rig.game), isEmpty);
    });
  }

  test(
    'packages an earlier join installed stay when a later one fails',
    () async {
      final rig = await JoinRig.create(
        hook: (step) {
          if (step == 'committed 1') throw StateError('Stopped.');
        },
      );
      await preparePlayer(rig);
      final earlier = await verifiedServer([StandardJoin.delta.package]);
      final earlierPlan = planJoin(earlier, {
        StandardJoin.delta.sha256Hex: StandardJoin.delta.offeredAt(
          'https://packages.example/delta.topiaforgemod',
        ),
      });
      rig.cache([StandardJoin.delta]);
      final first = rig.open();
      expect(
        await first.installJoinContent(
          rig.install,
          installingJoin(earlier, earlierPlan),
        ),
        isA<JoinInstallCompleted>(),
      );
      final other = TestArchive('server.epsilon', '1.0.0');
      final server = await verifiedServer([
        StandardJoin.delta.package,
        StandardJoin.serverAlpha.package,
        other.package,
      ]);
      final plan = planJoin(server, {
        StandardJoin.delta.sha256Hex: JoinPackageResolution.installed(
          StandardJoin.delta.manifest,
        ),
        StandardJoin.serverAlpha.sha256Hex: StandardJoin.serverAlpha.offeredAt(
          'https://packages.example/alpha.topiaforgemod',
        ),
        other.sha256Hex: other.offeredAt('https://packages.example/e.zip'),
      });
      rig.cache([StandardJoin.serverAlpha, other]);
      final event = await rig.repository.installJoinContent(
        rig.install,
        installingJoin(server, plan),
      );
      expect((event as JoinFailed).rollbackComplete, isTrue);
      expect(
        packageFolder(rig.game, 'server.delta', '1.0.0').existsSync(),
        isTrue,
      );
      expect(
        packageFolder(rig.game, 'own.alpha', '2.0.0').existsSync(),
        isFalse,
      );
      expect(
        packageFolder(rig.game, 'server.epsilon', '1.0.0').existsSync(),
        isFalse,
      );
      expect(managerRecords(rig.game).keys, contains('server.delta'));
      expect(managerRecords(rig.game).keys, isNot(contains('server.epsilon')));
    },
  );

  test('folders another program holds stay out of the player\'s own play '
      'until the next start takes them out', () async {
    late JoinRig rig;
    final releases = <Future<void> Function()?>[];
    rig = await JoinRig.create(
      hook: (step) async {
        if (step != 'committed 3') return;
        // Something holds the server Alpha's and Delta's assemblies, as an
        // antivirus scan of files just moved into place can, so the rollback
        // cannot take their folders out.
        for (final (id, version, assembly) in [
          ('own.alpha', '2.0.0', 'OwnAlpha.dll'),
          ('server.delta', '1.0.0', 'ServerDelta.dll'),
        ]) {
          releases.add(
            await holdInPlace(
              File(p.join(packageFolder(rig.game, id, version).path, assembly)),
            ),
          );
        }
        throw StateError('Stopped.');
      },
    );
    await preparePlayer(rig);
    final (server, plan) = await StandardJoin.plan();
    rig.cache(StandardJoin.downloads);
    final before = await rig.everything(StandardJoin.downloads);
    final event = await rig.repository.installJoinContent(
      rig.install,
      installingJoin(server, plan),
    );
    // Skipped: this account moves any file.
    if (releases.length != 2 || releases.contains(null)) return;
    expect((event as JoinFailed).rollbackComplete, isFalse);
    expect(
      [
        for (final archive in StandardJoin.downloads)
          packageFolder(rig.game, archive.id, archive.version).existsSync(),
      ],
      [true, false, true],
    );
    // Their entries and Delta's added record stay, so neither the launcher
    // nor a game started without it loads the server's Alpha, or turns Delta
    // on, in the player's own play.
    final record = ServerContentRecord.read(
      serverContentFile(rig.game).readAsBytesSync(),
    ).record!;
    expect(record.entryFor('own.alpha', '2.0.0')?.hidden, isTrue);
    expect(record.entryFor('own.beta', '1.5.0'), isNull);
    expect(record.entryFor('server.delta', '1.0.0'), isNotNull);
    expect(record.addedRecords, {'server.delta'});
    final view = await rig.playerView();
    expect(view.loads, before['loads']);
    // Delta, new to the player, shows as it would after the join: off.
    expect(
      view.mods.where((mod) => !mod.startsWith('server.delta@')),
      before['mods'],
    );
    expect(view.mods, contains('server.delta@1.0.0 off 1.0.0'));

    for (final release in releases) {
      await release!();
    }
    rig.repository = rig.open();
    expect(await rig.everything(StandardJoin.downloads), before);
    expect(joinFolders(rig.game), isEmpty);
  });

  test('a held folder is never half deleted, by the rollback or by a start '
      'while it is still held', () async {
    late JoinRig rig;
    Future<void> Function()? release;
    rig = await JoinRig.create(
      hook: (step) async {
        if (step != 'committed 3') return;
        // The manifest sorts after the install receipt, so a recursive
        // delete in place would take the receipt first and stop there.
        release = await holdInPlace(
          File(
            p.join(
              packageFolder(rig.game, 'own.alpha', '2.0.0').path,
              'topiaforge.mod.json',
            ),
          ),
        );
        throw StateError('Stopped.');
      },
    );
    await preparePlayer(rig);
    final (server, plan) = await StandardJoin.plan();
    rig.cache(StandardJoin.downloads);
    final before = await rig.everything(StandardJoin.downloads);
    final event = await rig.repository.installJoinContent(
      rig.install,
      installingJoin(server, plan),
    );
    if (release == null) return; // Skipped: this account moves any file.
    expect((event as JoinFailed).rollbackComplete, isFalse);
    final alpha = packageFolder(rig.game, 'own.alpha', '2.0.0');
    final receipt = File(p.join(alpha.path, 'topiaforge.install.json'));
    expect(receipt.existsSync(), isTrue, reason: 'the folder stays whole');
    // A start while it is still held leaves it whole too.
    rig.repository = rig.open();
    expect((await rig.playerView()).loads, before['loads']);
    expect(receipt.existsSync(), isTrue);
    await release!();
    rig.repository = rig.open();
    expect(await rig.everything(StandardJoin.downloads), before);
    expect(joinFolders(rig.game), isEmpty);
    // So nothing is left to block joining the same server again.
    expect(
      await rig.repository.installJoinContent(
        rig.install,
        installingJoin(server, plan),
      ),
      isA<JoinInstallCompleted>(),
    );
  });

  test('a join while an earlier one cannot be put back yet is refused, and '
      'builds on none of its folders', () async {
    late JoinRig rig;
    final releases = <Future<void> Function()?>[];
    var held = false;
    rig = await JoinRig.create(
      hook: (step) async {
        if (step != 'committed 3' || held) return;
        held = true;
        // Every folder this join moved in is held, so the rollback can take
        // none of them out.
        for (final archive in StandardJoin.downloads) {
          final folder = packageFolder(rig.game, archive.id, archive.version);
          releases.add(
            await holdInPlace(
              File(p.join(folder.path, 'topiaforge.install.json')),
            ),
          );
        }
        throw StateError('Stopped.');
      },
    );
    await preparePlayer(rig);
    final (server, plan) = await StandardJoin.plan();
    rig.cache(StandardJoin.downloads);
    final before = await rig.everything(StandardJoin.downloads);
    final first = await rig.repository.installJoinContent(
      rig.install,
      installingJoin(server, plan),
    );
    if (releases.length != 3 || releases.contains(null)) return;
    expect((first as JoinFailed).rollbackComplete, isFalse);
    expect((await rig.playerView()).loads, before['loads']);
    // Trying again at once would count the held folders as installed, and a
    // later start would take them from under its server profile.
    final again = await rig.repository.installJoinContent(
      rig.install,
      installingJoin(server, plan),
    );
    expect(again, isA<JoinFailed>());
    expect((again as JoinFailed).rollbackComplete, isTrue);
    expect(again.detail, contains('still being put back'));
    expect(again.detail, contains('then join again'));
    for (final release in releases) {
      await release!();
    }
    // Every folder was still in place, yet the join had failed: the next
    // start puts it all back rather than keep it as finished.
    rig.repository = rig.open();
    expect(await rig.everything(StandardJoin.downloads), before);
    expect(joinFolders(rig.game), isEmpty);
    expect(
      await rig.repository.installJoinContent(
        rig.install,
        installingJoin(server, plan),
      ),
      isA<JoinInstallCompleted>(),
    );
  });

  test(
    'a rollback that cannot finish says so, and the next start finishes it',
    () async {
      late JoinRig rig;
      Future<void> Function()? release;
      rig = await JoinRig.create(
        hook: (step) async {
          if (step != 'committed 2') return;
          // The record can no longer be read, so the rollback cannot take this
          // join's entries out of it.
          release = await holdUnreadable(serverContentFile(rig.game));
          throw StateError('Stopped.');
        },
      );
      await preparePlayer(rig);
      final (server, plan) = await StandardJoin.plan();
      rig.cache(StandardJoin.downloads);
      final before = await rig.everything(StandardJoin.downloads);
      final event = await rig.repository.installJoinContent(
        rig.install,
        installingJoin(server, plan),
      );
      if (release == null) return; // Skipped: this account reads any file.
      final failure = event as JoinFailed;
      expect(failure.code, JoinFailureCode.installFailed);
      expect(failure.rollbackComplete, isFalse);
      expect(failure.detail, contains('Restart the launcher before you play'));
      // Nothing the game would load: the folders are gone, and the mod new to
      // the player has no record left that could turn it on.
      for (final archive in StandardJoin.downloads) {
        expect(
          packageFolder(rig.game, archive.id, archive.version).existsSync(),
          isFalse,
        );
      }
      expect(joinFolders(rig.game), hasLength(1));

      await release!();
      rig.repository = rig.open();
      expect(await rig.everything(StandardJoin.downloads), before);
      expect(joinFolders(rig.game), isEmpty);
    },
  );
}
