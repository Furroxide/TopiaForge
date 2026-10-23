import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_data/src/multiplayer/lan_discovery_client.dart';
import 'package:launcher_data/src/multiplayer/multiplayer_preferences_keeper.dart';
import 'package:launcher_data/src/multiplayer/multiplayer_store_salvage.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'multiplayer_unreadable_settings.dart';

/// The game's journal of in-game changes.
const _epoch = '5f0c2a9e8d7b6a5f4e3d2c1b0a998877';

/// The player's multiplayer preferences through the launcher's facade: kept
/// in the store beside the settings, changed one at a time, and kept in step
/// with the copy the game reads.
void main() {
  final loopback = InternetAddress.loopbackIPv4;
  late Directory root;
  late Directory game;
  late GameInstall install;
  late String folder;

  setUp(() {
    root = Directory(
      Directory.systemTemp
          .createTempSync('tf-mp-preferences-')
          .resolveSymbolicLinksSync(),
    );
    game = Directory(p.join(root.path, 'game'))..createSync();
    install = GameInstall(
      path: game.path,
      executablePath: p.join(game.path, 'Robotopia.exe'),
      bepInExStatus: ComponentState.ready,
      loaderStatus: ComponentState.ready,
    );
    folder = p.join(root.path, 'data', 'multiplayer');
  });
  tearDown(() => root.deleteSync(recursive: true));

  LocalMultiplayerRepository repository({
    MultiplayerStore? store,
    LanDiscoveryClient? lan,
    GameProcessProbe? gameProbe,
  }) {
    final created = LocalMultiplayerRepository(
      dataRoot: p.join(root.path, 'data'),
      service: MultiplayerServiceConfiguration.none,
      store: store,
      lan:
          lan ??
          LanDiscoveryClient(
            listenAddress: loopback,
            port: 0,
            queryAddress: loopback,
          ),
      diagnostics: MultiplayerNetworkDiagnostics(
        bindAddress: loopback,
        gamePort: 0,
        lanPort: 0,
      ),
      gameProbe: gameProbe,
    );
    addTearDown(created.dispose);
    return created;
  }

  File settingsFile() => File(p.join(folder, 'settings.json'));
  Map<String, Object?> stored() =>
      jsonDecode(settingsFile().readAsStringSync()) as Map<String, Object?>;
  File staged(String name) =>
      File(p.join(game.path, 'BepInEx', 'TopiaForge', 'staging', name));
  File projection() => staged('multiplayer-preferences-v1.json');
  MultiplayerPreferencesProjection projected() =>
      LaunchTransport.readPreferences(projection().readAsStringSync());
  void journal(List<MultiplayerPreferenceChange> changes) {
    staged('multiplayer-preferences-journal-v1.json')
      ..parent.createSync(recursive: true)
      ..writeAsStringSync(
        LaunchTransport.writePreferencesJournal(
          MultiplayerPreferencesJournal(_epoch, changes),
        ),
      );
  }

  group('the store', () {
    test(
      'keeps the hosting defaults and the notice beside the settings',
      () async {
        final repo = repository();
        final fresh = await repo.loadPreferences();
        expect(fresh.health, MultiplayerPreferencesHealth.sound);
        expect(fresh.hosting.port, LanBeacon.defaultGamePort);
        expect(fresh.needsNetworkNotice, isTrue);

        final changed = await repo.changePreferences(
          const MultiplayerPreferencesChange(
            displayName: 'Sam',
            hostingPort: 27300,
            acknowledgeNetworkNotice: true,
          ),
        );
        expect(changed.settings.displayName, 'Sam');
        expect(changed.hosting.port, 27300);
        expect(stored()['hosting'], {
          'maxPlayers': 8,
          'name': "Player's server",
          'port': 27300,
          'visibility': 'lan',
          'voice': false,
        });
        expect(stored()['networkNotice'], 1);

        // A save of the settings alone, as the browser's favorites make, keeps
        // them; so does a change through the settings' own update.
        await repo.addFavorite(serverId: 'a' * 64, name: "Sam's Sandbox");
        await repo.saveSettings(
          (await repo.loadSettings()).settings.withUpnp(true),
        );
        final again = await repository().loadPreferences();
        expect(again.hosting.port, 27300);
        expect(again.networkNotice, 1);
        expect(again.settings.upnpEnabled, isTrue);
        expect(again.settings.favorites.entries.single.serverId, 'a' * 64);
      },
    );

    test('keeps no hosting defaults until the player sets one', () async {
      final repo = repository();
      await repo.changePreferences(
        const MultiplayerPreferencesChange(upnp: true),
      );
      expect(stored().containsKey('hosting'), isFalse);
      expect(stored().containsKey('networkNotice'), isFalse);
      expect(stored().containsKey('projection'), isFalse);
    });

    test(
      'drops a damaged member beside the settings and keeps the rest',
      () async {
        final damaged = <String, Object?>{
          'hosting': {'port': 'any'},
          'networkNotice': -1,
          'projection': {'revision': 2, 'journalApplied': 3},
        };
        for (final MapEntry(:key, :value) in damaged.entries) {
          settingsFile()
            ..parent.createSync(recursive: true)
            ..writeAsStringSync(
              jsonEncode({
                'schemaVersion': 1,
                'displayName': 'Sam',
                'networkNotice': 1,
                key: value,
              }),
            );
          final read = await repository().loadPreferences();
          expect(
            read.health,
            MultiplayerPreferencesHealth.salvaged,
            reason: key,
          );
          expect(read.settings.displayName, 'Sam', reason: key);
          expect(stored().containsKey(key), isFalse, reason: key);
        }
        // A position in the game's journal counts only with the journal's epoch.
        for (final record in [
          {'revision': 1, 'journalEpoch': 'NOT-AN-EPOCH', 'journalApplied': 1},
          {'revision': -1, 'journalApplied': 0},
          {'revision': 1, 'journalEpoch': _epoch, 'journalApplied': 1, 'x': 1},
        ]) {
          final salvaged = salvageMultiplayerSettingsFor({
            'projection': record,
          });
          expect(salvaged.damaged, isTrue, reason: '$record');
          expect(salvaged.extras.revision, isNull, reason: '$record');
        }
        final kept = salvageMultiplayerSettingsFor({
          'projection': {
            'revision': 4,
            'journalEpoch': _epoch,
            'journalApplied': 2,
          },
          'futureMember': true,
        });
        expect(kept.damaged, isFalse);
        expect(
          (
            kept.extras.revision,
            kept.extras.journalEpoch,
            kept.extras.journalApplied,
          ),
          (4, _epoch, 2),
        );
      },
    );

    test(
      'runs a round with no other write of the settings in between',
      () async {
        final store = MultiplayerStore(folder);
        final inRound = Completer<void>();
        final release = Completer<void>();
        final order = <String>[];
        final round = store.withSettings((current, write) async {
          inRound.complete();
          await release.future;
          await write(
            current.settings.withDisplayName('Round'),
            const MultiplayerSettingsExtras(revision: 0),
          );
          order.add('round');
        });
        await inRound.future;
        final link = store
            .updateSettings((settings) => settings.withLinkHandling(false))
            .then((_) => order.add('link'));
        await pumpEventQueue();
        expect(order, isEmpty);
        release.complete();
        await Future.wait([round, link]);
        expect(order, ['round', 'link']);
        final read = await store.loadSettings();
        expect(read.settings.displayName, 'Round');
        expect(read.settings.linkHandlingEnabled, isFalse);
        expect(read.extras.revision, 0);
      },
    );

    test('reports what it reads and writes, never stand-ins', () async {
      final store = MultiplayerStore(folder);
      final seen = <String>[];
      store.settingsChanges.listen(
        (settings) => seen.add(settings.displayName),
      );
      await store.saveSettings(MultiplayerSettings(displayName: 'Sam'));
      await store.loadSettings();
      await whileSettingsUnreadable(() => store.loadSettings());
      await pumpEventQueue();
      expect(seen, ['Sam', 'Sam']);
    });
  });

  group('a round with the game', () {
    test(
      'writes the game its copy, then takes in what changed in game',
      () async {
        final repo = repository();
        await repo.changePreferences(
          const MultiplayerPreferencesChange(displayName: 'Sam'),
        );
        final first = await repo.syncWithGame(install);
        expect(first, isA<MultiplayerPreferencesSynced>());
        expect((first as MultiplayerPreferencesSynced).changedInGame, isFalse);
        expect(projected().displayName, 'Sam');
        expect(projected().revision, 0);
        expect(stored()['projection'], {'revision': 0, 'journalApplied': 0});
        expect(stored()['hosting'], isNotNull);

        journal([
          DisplayNameChange(sequence: 1, displayName: 'Robin'),
          HostingChange(
            sequence: 2,
            hosting: MultiplayerHostingDefaults(name: 'Robin', port: 27400),
          ),
        ]);
        final second = await repo.syncWithGame(install);
        second as MultiplayerPreferencesSynced;
        expect(second.changedInGame, isTrue);
        expect(second.preferences.settings.displayName, 'Robin');
        expect(second.preferences.hosting.port, 27400);
        expect(projected().displayName, 'Robin');
        expect(projected().revision, 1);
        expect(stored()['projection'], {
          'revision': 1,
          'journalEpoch': _epoch,
          'journalApplied': 2,
        });

        // Taken in once: another round changes nothing.
        final third = await repo.syncWithGame(install);
        expect((third as MultiplayerPreferencesSynced).changedInGame, isFalse);
        expect(projected().revision, 1);
      },
    );

    test('a cancelled round writes nothing', () async {
      final repo = repository();
      final token = CancellationToken()..cancel();
      journal([DisplayNameChange(sequence: 1, displayName: 'Robin')]);
      final round = await repo.syncWithGame(install, cancellation: token);
      expect(round, isA<MultiplayerPreferencesRoundSkipped>());
      expect(projection().existsSync(), isFalse);
      expect(settingsFile().existsSync(), isFalse);
      expect(round.preferences.settings.displayName, 'Player');
    });

    test(
      'settings that cannot be read change neither the store nor the game',
      () async {
        final repo = repository();
        await repo.changePreferences(
          const MultiplayerPreferencesChange(displayName: 'Sam', upnp: true),
        );
        final before = settingsFile().readAsStringSync();
        journal([DisplayNameChange(sequence: 1, displayName: 'Robin')]);

        final round = await whileSettingsUnreadable(
          () => repo.syncWithGame(install),
        );
        expect(round, isA<MultiplayerPreferencesRoundSkipped>());
        expect(
          round.preferences.health,
          MultiplayerPreferencesHealth.unreadable,
        );
        // The stand-ins never reach the game's copy.
        expect(projection().existsSync(), isFalse);
        await whileSettingsUnreadable(() async {
          await expectLater(
            repo.changePreferences(
              const MultiplayerPreferencesChange(displayName: 'Lost'),
            ),
            throwsA(
              isA<MultiplayerPreferencesException>().having(
                (error) => error.unreadable,
                'unreadable',
                isTrue,
              ),
            ),
          );
        });
        expect(settingsFile().readAsStringSync(), before);

        // Readable again, the round takes the journal in.
        final next = await repo.syncWithGame(install);
        expect(next.preferences.settings.displayName, 'Robin');
        expect(next.preferences.settings.upnpEnabled, isTrue);
      },
    );

    test('a copy the game cannot be given fails plainly, and the next round '
        'writes it', () async {
      final repo = repository();
      journal([DisplayNameChange(sequence: 1, displayName: 'Robin')]);
      // Something that is not a folder stands where staging's parent is.
      final topia = Directory(p.join(game.path, 'BepInEx', 'TopiaForge'));
      final staging = Directory(p.join(topia.path, 'staging'));
      final journalText = staged(
        'multiplayer-preferences-journal-v1.json',
      ).readAsStringSync();
      topia.deleteSync(recursive: true);
      File(topia.path).writeAsStringSync('not a folder');

      final failed = await repo.syncWithGame(install);
      failed as MultiplayerPreferencesRoundFailed;
      expect(failed.problem, MultiplayerRoundProblem.gameCopyNotWritten);
      // The store was saved before the copy failed, so the next round writes
      // the copy from it.
      expect(stored()['projection'], {'revision': 0, 'journalApplied': 0});

      File(topia.path).deleteSync();
      staging.createSync(recursive: true);
      staged(
        'multiplayer-preferences-journal-v1.json',
      ).writeAsStringSync(journalText);
      final round = await repo.syncWithGame(install);
      expect(round, isA<MultiplayerPreferencesSynced>());
      expect(projected().displayName, 'Robin');
    });
  });

  group('changes', () {
    test('turning LAN discovery off stops browsing before the save', () async {
      final lan = LanDiscoveryClient(
        listenAddress: loopback,
        port: 0,
        queryAddress: loopback,
      );
      final repo = repository(lan: lan);
      expect(
        (await repo.startLanBrowsing()).status,
        LanDiscoveryStatus.listening,
      );
      final saving = repo.changePreferences(
        const MultiplayerPreferencesChange(lanDiscovery: false),
      );
      expect(lan.isListening, isFalse);
      expect((await saving).settings.lanDiscoveryEnabled, isFalse);
      expect(
        (await repo.startLanBrowsing()).status,
        LanDiscoveryStatus.disabled,
      );
    });

    test('a value the preferences refuse changes nothing', () async {
      final repo = repository();
      await repo.changePreferences(
        const MultiplayerPreferencesChange(displayName: 'Sam'),
      );
      final before = settingsFile().readAsStringSync();
      for (final change in const [
        MultiplayerPreferencesChange(displayName: 'S'),
        MultiplayerPreferencesChange(hostingPort: 70000),
        MultiplayerPreferencesChange(voice: true),
      ]) {
        await expectLater(
          repo.changePreferences(change),
          throwsA(
            isA<MultiplayerPreferencesException>().having(
              (error) => error.unreadable,
              'unreadable',
              isFalse,
            ),
          ),
        );
      }
      expect(settingsFile().readAsStringSync(), before);
    });
  });

  test('whether the game runs comes from its executable', () async {
    final asked = <String>[];
    for (final (answer, expected) in [
      (true, GameRunState.running),
      (false, GameRunState.notRunning),
      (null, GameRunState.unknown),
    ]) {
      final repo = repository(
        gameProbe: (path) async {
          asked.add(path);
          return answer;
        },
      );
      expect(await repo.gameRunState(install), expected);
    }
    expect(asked.toSet(), {install.executablePath});
    final failing = repository(
      gameProbe: (path) async => throw const FileSystemException('denied'),
    );
    expect(await failing.gameRunState(install), GameRunState.unknown);
  });
}

/// What [salvageMultiplayerSettings] makes of a settings document with
/// [members] beside a valid name.
SalvagedMultiplayerSettings salvageMultiplayerSettingsFor(
  Map<String, Object?> members,
) => salvageMultiplayerSettings({
  'schemaVersion': 1,
  'displayName': 'Sam',
  ...members,
})!;
