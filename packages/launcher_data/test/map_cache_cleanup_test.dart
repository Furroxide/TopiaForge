import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'multiplayer_join_rig.dart';

void main() {
  Directory cache(JoinRig rig) =>
      Directory(p.join(rig.game.path, 'BepInEx', 'TopiaForge', 'cache', 'maps'))
        ..createSync(recursive: true);
  File blob(Directory root) =>
      File(p.join(root.path, '${'a' * 64}.map'))..writeAsStringSync('cached');
  test('clears only this install map cache while confirmed closed', () async {
    final rig = await JoinRig.create();
    final root = cache(rig);
    final map = blob(root);
    File(p.join(root.path, 'example.json')).writeAsStringSync('{}');
    File(p.join(root.path, 'id-${'x' * 64}.json')).writeAsStringSync('{}');
    File(p.join(root.path, 'id-example.gone')).writeAsStringSync('');
    final kept = File(
      p.join(rig.dataRoot, 'package-cache', 'archive.topiaforgemod'),
    );
    kept.parent.createSync(recursive: true);
    kept.writeAsStringSync('keep');
    expect(await rig.repository.clearDownloadedMaps(rig.install), 4);
    expect(map.existsSync(), isFalse);
    expect(kept.readAsStringSync(), 'keep');
    expect(await rig.repository.clearDownloadedMaps(rig.install), 0);
  });
  for (final unknown in [false, true]) {
    test(
      'refuses a ${unknown ? 'unknown' : 'running'} game before deleting',
      () async {
        final rig = await JoinRig.create(
          probe: (_) async {
            if (unknown) throw StateError('probe unavailable');
            return true;
          },
        );
        final map = blob(cache(rig));
        await expectLater(
          rig.repository.clearDownloadedMaps(rig.install),
          throwsStateError,
        );
        expect(map.existsSync(), isTrue);
      },
    );
  }
  test('a cross-repository launch lease refuses cleanup', () async {
    final rig = await JoinRig.create();
    final map = blob(cache(rig));
    final lease = await LaunchStagingStore(
      rig.install.path,
    ).acquireLaunchLease();
    expect(lease, isNotNull);
    try {
      await expectLater(
        rig.repository.clearDownloadedMaps(rig.install),
        throwsStateError,
      );
    } finally {
      await lease!.release();
    }
    expect(map.existsSync(), isTrue);
  });
  test('unexpected nested content refuses the entire cleanup', () async {
    final rig = await JoinRig.create();
    final root = cache(rig);
    final map = blob(root);
    Directory(p.join(root.path, 'keep')).createSync();
    await expectLater(
      rig.repository.clearDownloadedMaps(rig.install),
      throwsStateError,
    );
    expect(map.existsSync(), isTrue);
  });
}
