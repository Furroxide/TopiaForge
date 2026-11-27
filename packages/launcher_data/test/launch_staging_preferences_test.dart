import 'dart:io';

import 'package:launcher_data/src/launch_staging_store.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

/// The game's journal of in-game changes.
const _epoch = '5f0c2a9e8d7b6a5f4e3d2c1b0a998877';

/// The multiplayer preferences projection and the game's change journal in
/// manager staging: written in place, kept in step with the launcher's store
/// across lost and restarted files.
void main() {
  late Directory root;
  late Directory game;
  late Directory staging;
  late LaunchStagingStore store;
  final settings = MultiplayerSettings(
    displayName: 'Sam',
  ).acknowledgePrivacyDisclosure();

  setUp(() {
    root = Directory(
      Directory.systemTemp
          .createTempSync('topiaforge-preferences-')
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

  group('multiplayer preferences', () {
    test('the projection is rewritten in place', () async {
      final projection = MultiplayerPreferencesProjection.fromSettings(
        settings,
        hosting: MultiplayerHostingDefaults.forPlayer('Sam'),
        revision: 1,
      );
      final file = await store.writePreferences(projection);
      expect(p.basename(file.path), 'multiplayer-preferences-v1.json');
      expect(
        file.readAsStringSync(),
        LaunchTransport.writePreferences(projection),
      );
      final next = MultiplayerPreferencesJournal(_epoch, [
        DisplayNameChange(sequence: 1, displayName: 'Charlie'),
      ]).applyTo(projection);
      await store.writePreferences(next);
      expect(
        LaunchTransport.readPreferences(file.readAsStringSync()).displayName,
        'Charlie',
      );
    });

    File projection() =>
        File(p.join(staging.path, 'multiplayer-preferences-v1.json'));
    void journal(
      List<MultiplayerPreferenceChange> changes, {
      String epoch = _epoch,
    }) => File(p.join(staging.path, 'multiplayer-preferences-journal-v1.json'))
        .writeAsStringSync(
          LaunchTransport.writePreferencesJournal(
            MultiplayerPreferencesJournal(epoch, changes),
          ),
        );

    test('a sync writes from the store and takes in the journal', () async {
      final kept = _PreferencesStore(settings);
      final first = await store.syncPreferences(kept.source, save: kept.save);
      expect(first.needsWrite, isTrue);
      expect((await store.readPreferences())!.revision, 0);
      expect(
        (await store.syncPreferences(kept.source, save: kept.save)).needsWrite,
        isFalse,
      );
      expect(kept.saves, 1);

      journal([DisplayNameChange(sequence: 1, displayName: 'Charlie')]);
      final round = await store.syncPreferences(kept.source, save: kept.save);
      expect(round.changedInGame, isTrue);
      expect(kept.settings.displayName, 'Charlie');
      final written = (await store.readPreferences())!;
      expect(written.revision, 1);
      expect((written.journalEpoch, written.journalApplied), (_epoch, 1));
      expect((kept.journalEpoch, kept.journalApplied), (_epoch, 1));

      projection().writeAsStringSync('{"schemaVersion":1}');
      expect(await store.readPreferences(), isNull);
    });

    test('the store is saved before the projection is written', () async {
      final kept = _PreferencesStore(settings);
      await store.syncPreferences(kept.source, save: kept.save);
      journal([DisplayNameChange(sequence: 1, displayName: 'Charlie')]);
      String? seen;
      await store.syncPreferences(
        kept.source,
        save: (sync) async {
          seen = projection().readAsStringSync();
          await kept.save(sync);
        },
      );
      expect(LaunchTransport.readPreferences(seen!).revision, 0);
      expect((await store.readPreferences())!.revision, 1);

      kept.settings = kept.settings.withDisplayName('Launcher');
      await expectLater(
        store.syncPreferences(
          kept.source,
          save: (sync) async => throw const FileSystemException('full disk'),
        ),
        throwsA(isA<FileSystemException>()),
      );
      expect((await store.readPreferences())!.displayName, 'Charlie');
    });

    test('a lost projection is rewritten from the store', () async {
      final kept = _PreferencesStore(settings);
      await store.syncPreferences(kept.source, save: kept.save);
      journal([DisplayNameChange(sequence: 1, displayName: 'InGame')]);
      await store.syncPreferences(kept.source, save: kept.save);
      kept.settings = kept.settings.withDisplayName('Launcher');
      await store.syncPreferences(kept.source, save: kept.save);
      expect((await store.readPreferences())!.revision, 2);

      projection().deleteSync();
      await store.syncPreferences(kept.source, save: kept.save);
      expect(kept.settings.displayName, 'Launcher');
      final rewritten = (await store.readPreferences())!;
      expect(rewritten.displayName, 'Launcher');
      expect(rewritten.revision, 3);

      projection().writeAsStringSync('{"schemaVersion":1}');
      await store.syncPreferences(kept.source, save: kept.save);
      expect(kept.settings.displayName, 'Launcher');
      expect((await store.readPreferences())!.revision, 4);
    });

    test('a journal started over after staging was lost is applied', () async {
      final kept = _PreferencesStore(settings);
      journal([DisplayNameChange(sequence: 6, displayName: 'Before')]);
      await store.syncPreferences(kept.source, save: kept.save);
      kept.settings = kept.settings.withDisplayName('Launcher');
      await store.syncPreferences(kept.source, save: kept.save);
      expect((kept.journalEpoch, kept.journalApplied), (_epoch, 6));

      // Staging is lost, and a game started without the launcher journals
      // the player's rename as change 1 of a new journal.
      projection().deleteSync();
      const restarted = '00112233445566778899aabbccddeeff';
      journal([
        DisplayNameChange(sequence: 1, displayName: 'InGame'),
      ], epoch: restarted);
      final round = await store.syncPreferences(kept.source, save: kept.save);
      expect(round.changedInGame, isTrue);
      expect(kept.settings.displayName, 'InGame');
      expect((kept.journalEpoch, kept.journalApplied), (restarted, 1));
      final written = (await store.readPreferences())!;
      expect(written.displayName, 'InGame');
      expect((written.journalEpoch, written.journalApplied), (restarted, 1));
    });

    test('the journal is read, and a damaged one is never applied', () async {
      expect(await store.readPreferencesJournal(), isNull);
      final file = File(
        p.join(staging.path, 'multiplayer-preferences-journal-v1.json'),
      );
      final journal = MultiplayerPreferencesJournal(_epoch, [
        DisclosureChange(sequence: 3, disclosure: 1),
      ]);
      file.writeAsStringSync(LaunchTransport.writePreferencesJournal(journal));
      final read = (await store.readPreferencesJournal())!;
      expect((read.epoch, read.changes.single.sequence), (_epoch, 3));
      file.writeAsStringSync(
        '{"changes":[{"privacy":"direct","sequence":1}],"epoch":"$_epoch",'
        '"schemaVersion":1}',
      );
      expect(await store.readPreferencesJournal(), isNull);
      file.writeAsStringSync(
        '{"changes":[],"changes":[],"epoch":"$_epoch","schemaVersion":1}',
      );
      expect(await store.readPreferencesJournal(), isNull);
      file.writeAsStringSync(
        '{"changes":[{"displayName":"Sam","sequence":1}],"schemaVersion":1}',
      );
      expect(
        await store.readPreferencesJournal(),
        isNull,
        reason: 'A journal without its epoch is never applied.',
      );
    });
  });
}

/// The launcher's multiplayer store as a preferences source: it keeps the
/// settings with the projection's revision and its position in the journal,
/// by epoch.
final class _PreferencesStore {
  _PreferencesStore(this.settings);
  MultiplayerSettings settings;
  int? revision;
  String? journalEpoch;
  int journalApplied = 0;
  int saves = 0;

  MultiplayerPreferencesSource get source => MultiplayerPreferencesValues(
    settings: settings,
    revision: revision,
    journalEpoch: journalEpoch,
    journalApplied: journalApplied,
  );

  Future<void> save(MultiplayerPreferencesSync sync) async {
    saves++;
    settings = sync.settings;
    revision = sync.revision;
    journalEpoch = sync.journalEpoch;
    journalApplied = sync.journalApplied;
  }
}
