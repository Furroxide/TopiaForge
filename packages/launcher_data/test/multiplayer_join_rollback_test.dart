import 'package:launcher_domain/launcher_domain.dart';
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
