import 'dart:convert';
import 'dart:io';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'multiplayer_host_served_support.dart';
import 'multiplayer_join_content_fixtures.dart';

/// Developer mode: a package only the host has, downloaded from the host's
/// own descriptor endpoint and joined end to end through the launcher's real
/// data layer, from a descriptor host A signed. The host's endpoint is on
/// loopback; its beacons arrive through fake UDP sockets.
void main() {
  final hosted = TestArchive(
    'example.hosted',
    '1.0.0',
    name: 'Hosted Tools',
    payload: noisyPayload(150 * 1024),
  );

  test('a LAN host\'s package is downloaded, verified, installed and '
      'pinned', () async {
    final host = await ServingHost.start([hosted]);
    final join = await HostJoin.create(host);
    await join.hearHost();
    final state = await join.downloading();
    final progress = <JoinDownloadProgressed>[];
    final ended = await join.drive(state, progress: progress);
    expect(
      ended.stage,
      JoinStage.launching,
      reason: '${ended.outcome?.failure?.detail}',
    );
    expect(host.packageRequests, 1);
    expect(host.requests.last, 'TFD1 package ${hosted.sha256Hex}\n');
    expect(
      cachedArchive(join.rig.dataRoot, hosted).readAsBytesSync(),
      hosted.bytes,
    );
    expect(join.cacheFiles(), ['${hosted.sha256Hex}.topiaforgemod']);
    expect(progress.last.bytesDone, hosted.bytes.length);
    expect(progress.last.bytesTotal, hosted.bytes.length);

    // Installed as a registry archive is, from the verified cache; nothing
    // records where the host was.
    final snapshot = await join.rig.repository.loadSnapshot();
    final serverProfile = snapshot.profiles.singleWhere(
      ServerProfiles.isServerProfile,
    );
    expect(await join.rig.loadsOf(serverProfile), ['example.hosted@1.0.0']);
    final receipt =
        jsonDecode(
              File(
                p.join(
                  packageFolder(join.rig.game, 'example.hosted', '1.0.0').path,
                  'topiaforge.install.json',
                ),
              ).readAsStringSync(),
            )
            as Map;
    expect(receipt['source'], 'cache');
    expect(receipt['sourceSha256'], hosted.sha256Hex);
    expect(receipt['trust'], 'sha256-verified');
    // New to the player, so recorded off for their own play.
    expect(
      managerRecords(join.rig.game)['example.hosted'],
      contains('"enabled":false'),
    );
    // Browsing the player started keeps running for them.
    expect(join.lan.client.isListening, isTrue);
  });

  test(
    'registry archives come first, and progress is one total in bytes',
    () async {
      final first = TestArchive(
        'example.alpha',
        '1.0.0',
        payload: noisyPayload(90 * 1024, seed: 3),
      );
      final second = TestArchive(
        'example.beta',
        '2.0.0',
        payload: noisyPayload(120 * 1024, seed: 5),
      );
      final sourced = TestArchive(
        'example.sourced',
        '1.1.0',
        payload: noisyPayload(60 * 1024, seed: 9),
      );
      final host = await ServingHost.start(
        [first, second],
        lists: [first.package, second.package, sourced.package],
      );
      final join = await HostJoin.create(host);
      await join.hearHost();
      final dist = Directory(p.join(join.rig.root.path, 'dist'))..createSync();
      final file = sourced.writeTo(dist);
      final state = await join.downloading(
        resolutions: {
          sourced.sha256Hex: sourced.offeredAt(
            file.uri.toString(),
            size: sourced.bytes.length,
          ),
        },
      );
      expect(state.downloadRequest!.hostDownloads, hasLength(2));
      expect(state.downloadRequest!.registryDownloads, hasLength(1));
      final progress = <JoinDownloadProgressed>[];
      final installing = await join.drive(
        state,
        until: JoinStage.installing,
        progress: progress,
      );
      expect(installing.stage, JoinStage.installing);
      final total =
          first.bytes.length + second.bytes.length + sourced.bytes.length;
      expect(progress.last.bytesDone, total);
      expect(progress.last.bytesTotal, total);
      for (var index = 1; index < progress.length; index++) {
        expect(
          progress[index].bytesDone,
          greaterThanOrEqualTo(progress[index - 1].bytesDone),
        );
      }
      // The registry archive was in the cache before the host was asked.
      expect(progress.first.bytesDone, sourced.bytes.length);
      expect(host.requests.where((line) => line.contains('package')), [
        'TFD1 package ${first.sha256Hex}\n',
        'TFD1 package ${second.sha256Hex}\n',
      ]);
      expect(join.cacheFiles(), hasLength(3));
    },
  );

  test(
    'a second join takes the cached archive and asks the host for none',
    () async {
      final host = await ServingHost.start([hosted]);
      final join = await HostJoin.create(host);
      await join.hearHost();
      final first = await join.drive(
        await join.downloading(),
        until: JoinStage.installing,
      );
      expect(first.stage, JoinStage.installing);
      expect(host.packageRequests, 1);
      final again = await join.drive(
        await join.downloading(),
        until: JoinStage.installing,
      );
      expect(again.stage, JoinStage.installing);
      expect(host.packageRequests, 1);
    },
  );

  test('a direct link or typed address downloads from the address that '
      'served its descriptor', () async {
    final host = await ServingHost.start([hosted]);
    final join = await HostJoin.create(host);
    for (final target in <JoinTarget>[
      JoinLinkTarget(
        SessionLink.direct(
          serverId: join.serverId,
          host: '127.0.0.1',
          port: host.port,
        ),
      ),
      JoinAddressTarget(host: '127.0.0.1', port: host.port),
    ]) {
      final before = host.packageRequests;
      final state = await join.downloading(target: target);
      final installing = await join.drive(state, until: JoinStage.installing);
      expect(
        installing.stage,
        JoinStage.installing,
        reason: '$target: ${installing.outcome?.failure?.detail}',
      );
      // The first join downloads; the second finds it in the cache.
      expect(host.packageRequests, before == 0 ? 1 : before);
      // No LAN browsing was needed.
      expect(join.lan.binder.sockets, isEmpty);
    }
  });
}
