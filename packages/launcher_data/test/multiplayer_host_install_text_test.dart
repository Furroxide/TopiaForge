import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'multiplayer_host_served_support.dart';
import 'multiplayer_join_content_fixtures.dart';

void main() {
  test(
    'an install refusal uses the descriptor identity before validation',
    () async {
      final archive = TestArchive(
        'example.hosted',
        '1.0.0',
        name: '${'2:' * 2000} ' * 250,
      );
      final host = await ServingHost.start([archive]);
      final join = await HostJoin.create(host);
      await join.hearHost();
      final installing = await join.drive(
        await join.downloading(),
        until: JoinStage.installing,
      );
      packageFolder(
        join.rig.game,
        'EXAMPLE.HOSTED',
        '1.0.0',
      ).parent.createSync(recursive: true);
      final failure = (await join.drive(installing)).outcome!.failure!;
      expect(failure.code, JoinFailureCode.installFailed);
      expect(failure.detail.length, lessThan(512));
      // The descriptor's id, not the host's unvalidated name; and no
      // version, since the folder under another casing can hold any.
      expect(
        failure.detail,
        contains('You have example.hosted installed under a folder named'),
      );
      expect(failure.detail, contains('Uninstall example.hosted in Mods'));
      expect(failure.detail, isNot(contains('1.0.0')));
      expect(failure.detail, isNot(contains('2:')));
    },
  );

  test(
    'a megabyte of install validation text is scrubbed within the log cap',
    () async {
      final text = '${'2:' * 2000} ' * 250;
      final watch = Stopwatch()..start();
      final log = await _refusedInstallLog(text);
      expect(watch.elapsed, lessThan(const Duration(seconds: 20)));
      final line = log
          .split('\n')
          .firstWhere((line) => line.contains('join install failed'));
      expect(line.length, lessThan(4200));
      expect(line, endsWith('…'));
    },
    timeout: const Timeout(Duration(minutes: 4)),
  );

  test('an install log never cuts a word into an address', () async {
    for (var shift = 0; shift < 15; shift++) {
      final log = await _refusedInstallLog(
        '${'x' * shift} ${'203.0.113.7777 ' * 300}',
      );
      expect(
        log,
        isNot(matches(RegExp(r'203\.0\.113\.77?(?![0-9])'))),
        reason: 'shift $shift',
      );
    }
  });
}

Future<String> _refusedInstallLog(String hostText) async {
  final archive = TestArchive(
    'example.hosted',
    '1.0.0',
    manifestChanges: {
      'loadAfter': [hostText],
    },
  );
  final host = await ServingHost.start([archive]);
  final join = await HostJoin.create(host);
  await join.hearHost();
  final installing = await join.drive(
    await join.downloading(),
    until: JoinStage.installing,
  );
  final repository = LocalLauncherRepository(
    dataRoot: join.rig.dataRoot,
    repositoryRoot: join.rig.repositoryRoot.path,
    gameRunningProbe: (_) async => false,
    // The managed validator reports invalid loadAfter entries verbatim too.
    // Read the staged host manifest, keeping this log-boundary test Dart-only.
    packageMetadataValidator: (root) async {
      final json =
          jsonDecode(
                File(
                  p.join(root.path, 'topiaforge.mod.json'),
                ).readAsStringSync(),
              )
              as Map<String, Object?>;
      return ModManifest.fromJson(json)
          .validate()
          .where((issue) => issue.isBlocking)
          .map((issue) => issue.message)
          .toList();
    },
  );
  addTearDown(repository.dispose);
  final answer = await repository.installJoinContent(
    join.rig.install,
    installing,
  );
  expect(
    JoinSession.reduce(installing, answer!).outcome!.failure!.code,
    JoinFailureCode.installFailed,
  );
  expect(
    packageFolder(join.rig.game, archive.id, archive.version).existsSync(),
    isFalse,
  );
  return join.launcherLog();
}
