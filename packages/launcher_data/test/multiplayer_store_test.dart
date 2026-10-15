import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'multiplayer_network_support.dart';

void main() {
  late Directory root;
  late String folder;
  var now = DateTime.utc(2026, 10, 15, 12);

  setUp(() {
    root = Directory.systemTemp.createTempSync('tf-mp-store-');
    folder = p.join(root.resolveSymbolicLinksSync(), 'multiplayer');
    now = DateTime.utc(2026, 10, 15, 12);
  });
  tearDown(() => root.deleteSync(recursive: true));

  MultiplayerStore store() => MultiplayerStore(folder, clock: () => now);
  File settingsFile() => File(p.join(folder, 'settings.json'));
  File knownHostsFile() => File(p.join(folder, 'known-hosts.json'));
  List<String> damagedCopies(String stem) =>
      Directory(folder)
          .listSync()
          .map((entity) => p.basename(entity.path))
          .where((name) => name.startsWith('$stem.damaged-'))
          .toList()
        ..sort();
  void writeSettings(String text) {
    Directory(folder).createSync(recursive: true);
    settingsFile().writeAsStringSync(text);
  }

  ServerBookmark bookmark(int index, [String? name]) => ServerBookmark(
    serverId: PureSha256.hex(utf8.encode('server $index')),
    name: name ?? 'Server $index',
    savedAt: DateTime.utc(2026, 10, 1).add(Duration(minutes: index)),
  );

  group('settings', () {
    test('a missing file reads as the defaults and writes nothing', () async {
      final load = await store().loadSettings();
      expect(load.recovery, MultiplayerStoreRecovery.none);
      expect(load.settings.toJson(), MultiplayerSettings().toJson());
      expect(Directory(folder).existsSync(), isFalse);
    });

    test('round-trip with a schema version and no temporary files', () async {
      final settings = MultiplayerSettings(
        displayName: 'Charl',
        acknowledgedPrivacyDisclosure: 1,
        upnpEnabled: true,
        lanDiscoveryEnabled: false,
        voiceConsent: 1,
        voiceEnabled: true,
        linkHandlingEnabled: false,
        favorites: ServerFavorites([bookmark(1), bookmark(2)]),
        history: ServerHistory([bookmark(3)]),
      );
      await store().saveSettings(settings);
      final stored = jsonDecode(settingsFile().readAsStringSync()) as Map;
      expect(stored['schemaVersion'], MultiplayerSettings.schemaVersion);
      final load = await store().loadSettings();
      expect(load.recovery, MultiplayerStoreRecovery.none);
      expect(load.settings.toJson(), settings.toJson());
      expect(
        Directory(folder).listSync().map((entity) => p.basename(entity.path)),
        ['settings.json'],
      );
    });

    for (final (about, text) in [
      ('text that is not JSON', '{"displayName": "Charl"'),
      ('JSON that is not an object', '["Charl"]'),
      (
        'a newer schema version',
        '{"schemaVersion": 2, "displayName": "Charl"}',
      ),
      ('bytes that are not UTF-8', null),
    ]) {
      test('$about is set aside and replaced with the defaults', () async {
        Directory(folder).createSync(recursive: true);
        final original = text == null ? [0xFF, 0xFE, 0x7B] : utf8.encode(text);
        settingsFile().writeAsBytesSync(original);
        final load = await store().loadSettings();
        expect(load.recovery, MultiplayerStoreRecovery.reset);
        expect(load.settings.toJson(), MultiplayerSettings().toJson());
        final copies = damagedCopies('settings');
        expect(copies, ['settings.damaged-20261015T120000000Z.json']);
        expect(File(p.join(folder, copies.single)).readAsBytesSync(), original);
        expect(
          jsonDecode(settingsFile().readAsStringSync()),
          MultiplayerSettings().toJson(),
        );
      });
    }

    test('a file past the size bound is damaged', () async {
      writeSettings('{"displayName":"${'x' * (300 * 1024)}"}');
      final load = await store().loadSettings();
      expect(load.recovery, MultiplayerStoreRecovery.reset);
      expect(damagedCopies('settings'), hasLength(1));
    });

    test('partial damage keeps everything that is still valid', () async {
      writeSettings(
        jsonEncode({
          'schemaVersion': 1,
          'displayName': 5,
          'upnpEnabled': true,
          'lanDiscoveryEnabled': 'yes',
          'favorites': [
            bookmark(1).toJson(),
            {'serverId': 'not a server', 'name': 'Bad', 'savedAt': 'x'},
            bookmark(1).toJson(),
            bookmark(2).toJson(),
          ],
          'history': 'none',
        }),
      );
      final load = await store().loadSettings();
      expect(load.recovery, MultiplayerStoreRecovery.salvaged);
      final settings = load.settings;
      expect(settings.displayName, MultiplayerSettings.defaultDisplayName);
      expect(settings.upnpEnabled, isTrue);
      expect(settings.lanDiscoveryEnabled, isTrue);
      expect(settings.favorites.entries.map((entry) => entry.serverId), [
        bookmark(1).serverId,
        bookmark(2).serverId,
      ]);
      expect(settings.history.entries, isEmpty);
      expect(damagedCopies('settings'), hasLength(1));
      // What replaced the file reads cleanly from now on.
      expect(
        (await store().loadSettings()).recovery,
        MultiplayerStoreRecovery.none,
      );
    });

    test('lists past their bounds keep their first entries', () async {
      writeSettings(
        jsonEncode({
          'favorites': [for (var i = 0; i < 120; i++) bookmark(i).toJson()],
          'history': [for (var i = 0; i < 60; i++) bookmark(i).toJson()],
        }),
      );
      final load = await store().loadSettings();
      expect(load.recovery, MultiplayerStoreRecovery.salvaged);
      expect(load.settings.favorites.entries, hasLength(100));
      expect(load.settings.history.entries, hasLength(50));
      expect(
        load.settings.favorites.entries.first.serverId,
        bookmark(0).serverId,
      );
    });

    test(
      'voice switched on without consent reads as off, not as damage',
      () async {
        writeSettings(jsonEncode({'voiceEnabled': true, 'voiceConsent': 0}));
        final load = await store().loadSettings();
        expect(load.recovery, MultiplayerStoreRecovery.none);
        expect(load.settings.voiceEnabled, isFalse);
      },
    );

    test('nothing stored makes a direct path a default', () async {
      writeSettings(
        jsonEncode({
          'privacy': 'direct',
          'allowDirect': true,
          'host': '1.2.3.4',
        }),
      );
      final load = await store().loadSettings();
      expect(load.settings.defaultPrivacy, MultiplayerAddressPrivacy.relayOnly);
      await store().saveSettings(load.settings);
      expect(settingsFile().readAsStringSync(), isNot(contains('1.2.3.4')));
    });

    test('a folder in the file\'s place is set aside', () async {
      Directory(p.join(folder, 'settings.json')).createSync(recursive: true);
      final load = await store().loadSettings();
      expect(load.recovery, MultiplayerStoreRecovery.reset);
      expect(settingsFile().existsSync(), isTrue);
      expect(damagedCopies('settings'), hasLength(1));
    });

    test('keeps only the newest three damaged copies', () async {
      for (var round = 0; round < 5; round++) {
        writeSettings('broken $round');
        now = now.add(const Duration(minutes: 1));
        await store().loadSettings();
      }
      final copies = damagedCopies('settings');
      expect(copies, hasLength(MultiplayerStore.keptDamagedCopies));
      expect(File(p.join(folder, copies.last)).readAsStringSync(), 'broken 4');
    });

    test('updates run one after another, so none is lost', () async {
      final shared = store();
      await Future.wait([
        for (var index = 0; index < 20; index++)
          shared.updateSettings(
            (settings) =>
                settings.withFavorites(settings.favorites.add(bookmark(index))),
          ),
      ]);
      final load = await shared.loadSettings();
      expect(load.settings.favorites.entries, hasLength(20));
    });

    test(
      'a save that cannot be written fails with a message naming no path',
      () async {
        File(p.join(root.path, 'blocker')).writeAsStringSync('');
        final blocked = MultiplayerStore(
          p.join(root.path, 'blocker', 'multiplayer'),
        );
        await expectLater(
          blocked.saveSettings(MultiplayerSettings()),
          throwsA(
            isA<MultiplayerStoreException>().having(
              (error) => error.message,
              'message',
              allOf(isNot(contains('blocker')), isNot(contains(root.path))),
            ),
          ),
        );
        final load = await blocked.loadSettings();
        expect(load.settings.toJson(), MultiplayerSettings().toJson());
      },
    );
  });

  group('known hosts', () {
    final typed = JoinAddressTarget(host: '127.0.0.1', port: 27250);

    test('round-trip and never hold the address', () async {
      await store().updateKnownHosts(
        (hosts) => hosts.remember(typed, hostAServerId, now),
      );
      final text = knownHostsFile().readAsStringSync();
      expect(text, contains(hostAServerId));
      expect(text, isNot(contains('127.0.0.1')));
      final hosts = await store().loadKnownHosts();
      expect(hosts.check(typed, hostAServerId), KnownHostCheck.match);
    });

    test('a damaged record is set aside and reads as empty', () async {
      Directory(folder).createSync(recursive: true);
      knownHostsFile().writeAsStringSync('not json');
      final hosts = await store().loadKnownHosts();
      expect(hosts.length, 0);
      expect(damagedCopies('known-hosts'), hasLength(1));
    });

    test('a partly damaged record keeps its valid entries', () async {
      Directory(folder).createSync(recursive: true);
      final valid = KnownHosts.empty().remember(typed, hostAServerId, now);
      final document = jsonDecode(utf8.decode(valid.encode())) as Map;
      final entries = [
        ...(document['hosts'] as List),
        {'target': 'nope', 'serverId': hostBServerId, 'seenAt': 1},
      ];
      knownHostsFile().writeAsBytesSync(
        CanonicalJson.encode({'hosts': entries, 'schema': document['schema']}),
      );
      final hosts = await store().loadKnownHosts();
      expect(hosts.check(typed, hostAServerId), KnownHostCheck.match);
      expect(hosts.length, 1);
      expect(damagedCopies('known-hosts'), hasLength(1));
      expect(knownHostsFile().readAsBytesSync(), valid.encode());
    });

    test('an unchanged record is not rewritten', () async {
      await store().updateKnownHosts(
        (hosts) => hosts.remember(typed, hostAServerId, now),
      );
      final before = knownHostsFile().lastModifiedSync();
      knownHostsFile().setLastModifiedSync(DateTime(2020));
      await store().updateKnownHosts((hosts) => hosts);
      expect(knownHostsFile().lastModifiedSync(), DateTime(2020));
      expect(before.isAfter(DateTime(2020)), isTrue);
    });
  });

  group('a write stopped between its two moves', () {
    /// Leaves what a write that died after moving [file] aside leaves: the
    /// old file as a backup, the new one as a temporary file, and no file.
    void interrupt(File file, String token) {
      file.renameSync('${file.path}.$token.bak');
      File('${file.path}.$token.tmp').writeAsStringSync('{"unfinished": ');
    }

    test('is undone from its backup when the settings are read', () async {
      final kept = MultiplayerSettings(
        displayName: 'Charl',
        lanDiscoveryEnabled: false,
        favorites: ServerFavorites([bookmark(1)]),
      );
      await store().saveSettings(kept);
      interrupt(settingsFile(), '4242-1-1');
      final load = await store().loadSettings();
      expect(load.recovery, MultiplayerStoreRecovery.none);
      expect(load.settings.toJson(), kept.toJson());
      expect(settingsFile().existsSync(), isTrue);
      expect(
        Directory(folder).listSync().map((entity) => p.basename(entity.path)),
        isNot(contains(endsWith('.bak'))),
      );
    });

    test('restores the newest backup and drops the older ones', () async {
      // The newest sorts between the others by name, so only times tell.
      Directory(folder).createSync(recursive: true);
      for (final (token, name, day) in [
        ('1000-1-1', 'Oldest', 1),
        ('5000-5-5', 'Newer', 3),
        ('9000-9-9', 'Older', 2),
      ]) {
        File(p.join(folder, 'settings.json.$token.bak'))
          ..writeAsStringSync(
            jsonEncode(MultiplayerSettings(displayName: name).toJson()),
          )
          ..setLastModifiedSync(DateTime(2026, 10, day));
      }
      final updated = await store().updateSettings(
        (settings) => settings.withUpnp(true),
      );
      expect(updated.displayName, 'Newer');
      expect(updated.upnpEnabled, isTrue);
      expect(
        Directory(folder).listSync().map((entity) => p.basename(entity.path)),
        isNot(contains(endsWith('.bak'))),
      );
    });

    test('is undone for the known hosts too', () async {
      final typed = JoinAddressTarget(host: '127.0.0.1', port: 27250);
      await store().updateKnownHosts(
        (hosts) => hosts.remember(typed, hostAServerId, now),
      );
      final original = knownHostsFile().readAsBytesSync();
      interrupt(knownHostsFile(), '4242-3-3');
      final hosts = await store().loadKnownHosts();
      expect(hosts.check(typed, hostAServerId), KnownHostCheck.match);
      expect(knownHostsFile().readAsBytesSync(), original);
    });

    test('never restores a folder named like a backup', () async {
      Directory(
        p.join(folder, 'settings.json.4242-4-4.bak'),
      ).createSync(recursive: true);
      final load = await store().loadSettings();
      expect(load.recovery, MultiplayerStoreRecovery.none);
      expect(load.settings.toJson(), MultiplayerSettings().toJson());
      expect(settingsFile().existsSync(), isFalse);
      expect(
        Directory(p.join(folder, 'settings.json.4242-4-4.bak')).existsSync(),
        isTrue,
      );
    });
  });

  test('no stored file names an address', () async {
    final shared = store();
    await shared.updateSettings(
      (settings) => settings.withHistory(
        settings.history.record(bookmark(1, "Charl's Sandbox")),
      ),
    );
    await shared.updateKnownHosts(
      (hosts) => hosts.remember(
        JoinAddressTarget(host: 'game.local', port: 27250),
        hostAServerId,
        now,
      ),
    );
    for (final entity in Directory(folder).listSync()) {
      final text = File(entity.path).readAsStringSync();
      for (final address in addressTexts) {
        expect(text, isNot(contains(address)), reason: entity.path);
      }
    }
  });
}
