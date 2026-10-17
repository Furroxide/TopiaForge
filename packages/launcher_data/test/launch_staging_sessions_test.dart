import 'dart:io';

import 'package:launcher_data/src/launch_staging_store.dart';
import 'package:launcher_data/src/launch_storage_keys.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory root;
  late Directory game;
  late Directory staging;
  late LaunchStagingStore store;
  final settings = MultiplayerSettings(
    displayName: 'Sam',
  ).acknowledgePrivacyDisclosure();
  const serverId =
      'c5f39060271ebd2f22d9bf155a26bf459274e31b795145fd9d862dc206d98bb3';

  ProfileLaunchConfigurationV4 menu(String requestId) =>
      ProfileLaunchConfigurationV4(
        profileId: 'profile',
        profileRevision: 1,
        requestId: requestId,
        command: 'main-menu',
        safeMode: false,
        inheritManagerModState: false,
        enabledMods: const [],
        selectedVersions: const {},
        packages: const [],
      );

  LaunchSessionRequest join([LaunchSessionTarget? target]) =>
      LaunchSessionRequest.join(
        intentId: 'intent-1',
        settings: settings,
        privacy: SessionPrivacy.relayOnly,
        target:
            target ??
            LaunchListingTarget(
              source: ServerListingSource.lan,
              serverId: serverId,
              descriptorSha256: 'a' * 64,
              packageDigest: 'b' * 64,
            ),
      );

  File channel(String prefix, String id) => File(
    p.join(staging.path, '$prefix-${LaunchStorageKeys.request(id)}.json'),
  );

  setUp(() {
    root = Directory(
      Directory.systemTemp
          .createTempSync('topiaforge-sessions-')
          .resolveSymbolicLinksSync(),
    );
    game = Directory(p.join(root.path, 'game'))..createSync();
    staging = Directory(p.join(game.path, 'BepInEx', 'TopiaForge', 'staging'))
      ..createSync(recursive: true);
    store = LaunchStagingStore(game.path);
  });
  tearDown(() {
    if (root.existsSync()) root.deleteSync(recursive: true);
  });

  group('launch requests', () {
    test('V4 is staged by the V4 writer and V5 by its own', () async {
      final v4 = StagedLaunchRequest.forLaunch(menu('plain'));
      final v4File = await store.writeLaunchRequest(v4);
      expect(v4File.path, channel('launch-profile', 'plain').path);
      expect(
        v4File.readAsStringSync(),
        LaunchTransport.writeProfile(menu('plain')),
      );

      final v5 = StagedLaunchRequest.forLaunch(
        menu('session'),
        session: join(),
      );
      final v5File = await store.writeLaunchRequest(v5);
      expect(v5File.path, channel('launch-profile', 'session').path);
      expect(v5File.readAsStringSync(), v5.encode());
      expect(
        () => store.writeLaunchRequest(v5),
        throwsA(isA<FileSystemException>()),
      );
    });

    test('a staged V5 is removed only while unchanged', () async {
      final v5 = StagedLaunchRequest.forLaunch(
        menu('session'),
        session: join(),
      );
      final file = await store.writeLaunchRequest(v5);
      final other = StagedLaunchRequest.forLaunch(
        menu('session'),
        session: LaunchSessionRequest.join(
          intentId: 'intent-2',
          settings: settings,
          privacy: SessionPrivacy.relayOnly,
          target: join().target!,
        ),
      );
      await store.deleteLaunchRequest(other);
      expect(file.existsSync(), isTrue);
      await store.deleteLaunchRequest(v5);
      expect(file.existsSync(), isFalse);
      await store.deleteLaunchRequest(v5);
    });
  });

  group('session handoff', () {
    SessionHandoff handoff(String requestId) => SessionHandoff(
      requestId: requestId,
      handoffId: 'handoff-1',
      target: LaunchHandoffInvite('ABCDE12345'),
    );

    Iterable<String> leftovers() => staging
        .listSync()
        .map((item) => p.basename(item.path))
        .where((name) => name.contains('.taking-') || name.contains('.tmp-'));

    test('is taken once, then gone', () async {
      final file = channel('session-handoff', 'process-1')
        ..writeAsStringSync(
          LaunchTransport.writeSessionHandoff(handoff('process-1')),
        );
      final taken = await store.takeSessionHandoff('process-1');
      expect((taken!.target as LaunchHandoffInvite).inviteCode, 'ABCDE12345');
      expect(file.existsSync(), isFalse);
      expect(leftovers(), isEmpty);
      expect(await store.takeSessionHandoff('process-1'), isNull);
    });

    test('a foreign or malformed handoff is removed, never acted on', () async {
      final foreign = channel('session-handoff', 'process-1')
        ..writeAsStringSync(
          LaunchTransport.writeSessionHandoff(handoff('process-2')),
        );
      expect(await store.takeSessionHandoff('process-1'), isNull);
      expect(foreign.existsSync(), isFalse);
      foreign.writeAsStringSync(
        '{"handoffId":"h","requestId":"process-1","schemaVersion":1,'
        '"target":{"host":"192.168.1.20","kind":"direct","port":27250}}',
      );
      expect(await store.takeSessionHandoff('process-1'), isNull);
      expect(foreign.existsSync(), isFalse);
      expect(leftovers(), isEmpty);
    });

    test('a handoff written during a take waits for the next one', () async {
      final file = channel('session-handoff', 'process-1')
        ..writeAsStringSync(
          LaunchTransport.writeSessionHandoff(handoff('process-1')),
        );
      final newer = SessionHandoff(
        requestId: 'process-1',
        handoffId: 'handoff-2',
        target: const LaunchHandoffCurrent(),
      );
      // The game replaces the file between the claim and the read.
      final racing = LaunchStagingStore(
        game.path,
        afterClaim: () =>
            file.writeAsStringSync(LaunchTransport.writeSessionHandoff(newer)),
      );
      expect(
        (await racing.takeSessionHandoff('process-1'))!.handoffId,
        'handoff-1',
      );
      expect(file.existsSync(), isTrue);
      expect(
        (await store.takeSessionHandoff('process-1'))!.handoffId,
        'handoff-2',
      );
      expect(file.existsSync(), isFalse);
      expect(leftovers(), isEmpty);
    });

    test('a take sweeps only its own claims, once they are stale', () async {
      final now = DateTime.now().millisecondsSinceEpoch;
      final hour = const Duration(hours: 1).inMilliseconds;
      File claim(String prefix, String id, int claimedAt) =>
          File('${channel(prefix, id).path}.taking-$claimedAt-${'c' * 32}')
            ..writeAsStringSync('{}');
      final stale = claim('session-handoff', 'crashed', now - 2 * hour);
      final live = claim('session-handoff', 'running', now - 1000);
      final gameClaim = claim('session-join', 'crashed', now - 2 * hour);
      final undated = File(
        '${channel('session-handoff', 'other').path}.taking-${'c' * 32}',
      )..writeAsStringSync('{}');
      final held = claim('session-handoff', 'held', now - 2 * hour);
      final reader = await held.open();
      try {
        channel('session-handoff', 'process-1').writeAsStringSync(
          LaunchTransport.writeSessionHandoff(handoff('process-1')),
        );
        expect(await store.takeSessionHandoff('process-1'), isNotNull);
        expect(stale.existsSync(), isFalse);
        expect(
          live.existsSync(),
          isTrue,
          reason: 'A live take keeps its claim.',
        );
        expect(
          gameClaim.existsSync(),
          isTrue,
          reason: "The game's claims are the game's to sweep.",
        );
        expect(undated.existsSync(), isTrue, reason: 'Only dated claims go.');
        if (Platform.isWindows) {
          expect(held.existsSync(), isTrue, reason: 'Another handle holds it.');
        }
      } finally {
        await reader.close();
      }
      expect(store.sweepStaleClaims(), Platform.isWindows ? 1 : 0);
      expect(held.existsSync(), isFalse);
    });

    test('a linked handoff is refused', () async {
      final outside = File(p.join(root.path, 'outside.json'))
        ..writeAsStringSync(
          LaunchTransport.writeSessionHandoff(handoff('process-1')),
        );
      final link = Link(channel('session-handoff', 'process-1').path);
      try {
        link.createSync(outside.path);
      } on FileSystemException {
        markTestSkipped('This account cannot create symbolic links.');
        return;
      }
      expect(await store.takeSessionHandoff('process-1'), isNull);
      expect(outside.existsSync(), isTrue);
    });
  });

  group('live join', () {
    SessionJoinRequest live(String joinId) => SessionJoinRequest(
      requestId: 'process-1',
      joinId: joinId,
      packageSetDigest: menu('process-1').digest,
      session: join(),
    );

    test('a newer join replaces one not yet taken', () async {
      final first = await store.writeSessionJoin(live('join-1'));
      final second = await store.writeSessionJoin(live('join-2'));
      expect(second.path, first.path);
      expect(
        LaunchTransport.readSessionJoin(second.readAsStringSync()).joinId,
        'join-2',
      );
      expect(
        staging.listSync().where(
          (item) => p.basename(item.path).contains('.tmp-'),
        ),
        isEmpty,
      );
      expect(await store.withdrawSessionJoin(live('join-1')), isFalse);
      expect(second.existsSync(), isTrue);
      expect(await store.withdrawSessionJoin(live('join-2')), isTrue);
      expect(second.existsSync(), isFalse);
      expect(await store.withdrawSessionJoin(live('join-2')), isFalse);
    });

    // While another handle reads the file, Windows refuses a rename over it,
    // however that handle shares it.
    test('a replace waits for a reader that holds the file', () async {
      final file = await store.writeSessionJoin(live('join-1'));
      final reader = await file.open();
      var retries = 0;
      final waiting = LaunchStagingStore(
        game.path,
        beforeRetry: (attempt) async {
          retries++;
          await reader.close();
        },
      );
      try {
        await waiting.writeSessionJoin(live('join-2'));
      } finally {
        if (retries == 0) await reader.close();
      }
      expect(
        LaunchTransport.readSessionJoin(file.readAsStringSync()).joinId,
        'join-2',
      );
      if (Platform.isWindows) expect(retries, 1);
    });

    test(
      'a replace gives up on a reader that never lets go',
      () async {
        final file = await store.writeSessionJoin(live('join-1'));
        final reader = await file.open();
        var retries = 0;
        final waiting = LaunchStagingStore(
          game.path,
          beforeRetry: (attempt) async => retries++,
        );
        try {
          await expectLater(
            waiting.writeSessionJoin(live('join-2')),
            throwsA(isA<FileSystemException>()),
          );
          expect(retries, LaunchStagingStore.replaceRetries);
        } finally {
          await reader.close();
        }
        expect(
          LaunchTransport.readSessionJoin(file.readAsStringSync()).joinId,
          'join-1',
        );
        expect(
          staging.listSync().where(
            (item) => p.basename(item.path).contains('.tmp-'),
          ),
          isEmpty,
        );
      },
      skip: Platform.isWindows
          ? false
          : 'Only Windows refuses a rename over a file another handle reads.',
    );
  });
}
