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
    displayName: 'Charl',
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

    test('is taken once, then gone', () async {
      final file = channel('session-handoff', 'process-1')
        ..writeAsStringSync(
          LaunchTransport.writeSessionHandoff(handoff('process-1')),
        );
      final taken = await store.takeSessionHandoff('process-1');
      expect((taken!.target as LaunchHandoffInvite).inviteCode, 'ABCDE12345');
      expect(file.existsSync(), isFalse);
      expect(await store.takeSessionHandoff('process-1'), isNull);
    });

    test('a foreign or malformed handoff is never taken', () async {
      final foreign = channel('session-handoff', 'process-1')
        ..writeAsStringSync(
          LaunchTransport.writeSessionHandoff(handoff('process-2')),
        );
      expect(await store.takeSessionHandoff('process-1'), isNull);
      expect(foreign.existsSync(), isTrue);
      foreign.writeAsStringSync(
        '{"handoffId":"h","requestId":"process-1","schemaVersion":1,'
        '"target":{"host":"192.168.1.20","kind":"direct","port":27250}}',
      );
      expect(await store.takeSessionHandoff('process-1'), isNull);
      expect(foreign.existsSync(), isTrue);
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
      await store.withdrawSessionJoin(live('join-1'));
      expect(second.existsSync(), isTrue);
      await store.withdrawSessionJoin(live('join-2'));
      expect(second.existsSync(), isFalse);
    });
  });

  group('multiplayer preferences', () {
    test('the projection is rewritten in place', () async {
      final projection = MultiplayerPreferencesProjection.fromSettings(
        settings,
        hosting: MultiplayerHostingDefaults.forPlayer('Charl'),
        revision: 1,
      );
      final file = await store.writePreferences(projection);
      expect(p.basename(file.path), 'multiplayer-preferences-v1.json');
      expect(
        file.readAsStringSync(),
        LaunchTransport.writePreferences(projection),
      );
      final next = MultiplayerPreferencesJournal([
        DisplayNameChange(sequence: 1, displayName: 'Charlie'),
      ]).applyTo(projection);
      await store.writePreferences(next);
      expect(
        LaunchTransport.readPreferences(file.readAsStringSync()).displayName,
        'Charlie',
      );
    });

    test('a sync writes from the store and takes in the journal', () async {
      final source = MultiplayerPreferencesValues(
        settings: settings,
        hostingDefaults: MultiplayerHostingDefaults.forPlayer('Charl'),
      );
      final first = await store.syncPreferences(source);
      expect(first.needsWrite, isTrue);
      expect((await store.readPreferences())!.revision, 0);
      expect((await store.syncPreferences(source)).needsWrite, isFalse);

      File(
        p.join(staging.path, 'multiplayer-preferences-journal-v1.json'),
      ).writeAsStringSync(
        LaunchTransport.writePreferencesJournal(
          MultiplayerPreferencesJournal([
            DisplayNameChange(sequence: 1, displayName: 'Charlie'),
          ]),
        ),
      );
      final round = await store.syncPreferences(source);
      expect(round.changedInGame, isTrue);
      expect(round.settings.displayName, 'Charlie');
      final written = (await store.readPreferences())!;
      expect(written.revision, 1);
      expect(written.journalApplied, 1);

      File(
        p.join(staging.path, 'multiplayer-preferences-v1.json'),
      ).writeAsStringSync('{"schemaVersion":1}');
      expect(await store.readPreferences(), isNull);
    });

    test('the journal is read, and a damaged one is never applied', () async {
      expect(await store.readPreferencesJournal(), isNull);
      final file = File(
        p.join(staging.path, 'multiplayer-preferences-journal-v1.json'),
      );
      final journal = MultiplayerPreferencesJournal([
        DisclosureChange(sequence: 3, disclosure: 1),
      ]);
      file.writeAsStringSync(LaunchTransport.writePreferencesJournal(journal));
      expect(
        (await store.readPreferencesJournal())!.changes.single.sequence,
        3,
      );
      file.writeAsStringSync(
        '{"changes":[{"sequence":1,"privacy":"direct"}],"schemaVersion":1}',
      );
      expect(await store.readPreferencesJournal(), isNull);
      file.writeAsStringSync('{"changes":[],"changes":[],"schemaVersion":1}');
      expect(await store.readPreferencesJournal(), isNull);
    });
  });
}
