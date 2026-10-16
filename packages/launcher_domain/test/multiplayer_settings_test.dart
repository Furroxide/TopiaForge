import 'dart:convert';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  final when = DateTime.utc(2026, 10, 13, 12);
  ServerBookmark bookmark(int index, [String? name]) => ServerBookmark(
    serverId: index.toRadixString(16).padLeft(64, '0'),
    name: name ?? 'Server $index',
    savedAt: when.add(Duration(minutes: index)),
  );

  group('multiplayer settings', () {
    test('defaults follow the plan', () {
      final settings = MultiplayerSettings();
      expect(settings.displayName, 'Player');
      expect(settings.defaultPrivacy, MultiplayerAddressPrivacy.relayOnly);
      expect(settings.needsPrivacyDisclosure, isTrue);
      expect(settings.upnpEnabled, isFalse);
      expect(settings.lanDiscoveryEnabled, isTrue);
      expect(settings.hasVoiceConsent, isFalse);
      expect(settings.voiceEnabled, isFalse);
      expect(settings.voiceActive, isFalse);
      expect(settings.linkHandlingEnabled, isTrue);
      expect(settings.favorites.entries, isEmpty);
      expect(settings.history.entries, isEmpty);
    });

    test('display names are 2 to 24 printable characters', () {
      for (final name in ['Al', 'x' * 24, 'Zoë', '\u{1F600}', 'A B']) {
        expect(MultiplayerSettings.isValidDisplayName(name), isTrue);
        expect(MultiplayerSettings(displayName: name).displayName, name);
      }
      for (final name in ['A', 'x' * 25, ' Al', 'Al ', 'A\nB', '\ud800A', '']) {
        expect(MultiplayerSettings.isValidDisplayName(name), isFalse);
        expect(
          () => MultiplayerSettings(displayName: name),
          throwsArgumentError,
        );
      }
      expect(MultiplayerSettings().withDisplayName('Sam').displayName, 'Sam');
    });

    test('direct is a per-session choice after the disclosure', () {
      final fresh = MultiplayerSettings();
      expect(
        fresh.privacyForSession(allowDirect: false),
        same(SessionPrivacy.relayOnly),
      );
      expect(
        () => fresh.privacyForSession(allowDirect: true),
        throwsStateError,
      );
      final informed = fresh.acknowledgePrivacyDisclosure();
      expect(informed.needsPrivacyDisclosure, isFalse);
      final direct = informed.privacyForSession(allowDirect: true);
      expect(direct.addressPrivacy, MultiplayerAddressPrivacy.direct);
      expect(direct.allowsDirect, isTrue);
      expect(SessionPrivacy.relayOnly.allowsDirect, isFalse);
      expect(
        informed.defaultPrivacy,
        MultiplayerAddressPrivacy.relayOnly,
        reason: 'choosing direct for one session changes no default',
      );
      final stored = jsonEncode(informed.toJson());
      expect(stored, isNot(contains('direct')));
      expect(stored, isNot(contains('relay')));
    });

    test('nothing stored makes direct a default', () {
      final settings = MultiplayerSettings.fromJson({
        'acknowledgedPrivacyDisclosure': 1,
        'privacy': 'direct',
        'allowDirect': true,
        'defaultPrivacy': 'direct',
      });
      expect(settings.defaultPrivacy, MultiplayerAddressPrivacy.relayOnly);
      expect(
        settings.privacyForSession(allowDirect: false).addressPrivacy,
        MultiplayerAddressPrivacy.relayOnly,
      );
    });

    test('voice stays off until the player consents to the wording', () {
      final fresh = MultiplayerSettings();
      expect(() => fresh.withVoice(true), throwsArgumentError);
      expect(
        () => MultiplayerSettings(voiceEnabled: true),
        throwsArgumentError,
      );
      final consented = fresh.giveVoiceConsent().withVoice(true);
      expect(consented.voiceActive, isTrue);
      final withdrawn = consented.withdrawVoiceConsent();
      expect(withdrawn.voiceEnabled, isFalse);
      expect(withdrawn.hasVoiceConsent, isFalse);
      // Consent to older wording does not count, and stored voice reads off.
      final stale = MultiplayerSettings.fromJson({
        'voiceConsent': 0,
        'voiceEnabled': true,
      });
      expect(stale.voiceEnabled, isFalse);
      expect(stale.voiceActive, isFalse);
    });

    test('switches round-trip through JSON', () {
      final settings = MultiplayerSettings()
          .withDisplayName('Sam')
          .acknowledgePrivacyDisclosure()
          .withUpnp(true)
          .withLanDiscovery(false)
          .giveVoiceConsent()
          .withVoice(true)
          .withLinkHandling(false)
          .withFavorites(ServerFavorites().add(bookmark(1)))
          .withHistory(ServerHistory().record(bookmark(2)));
      final read = MultiplayerSettings.fromJson(
        (jsonDecode(jsonEncode(settings.toJson())) as Map)
            .cast<String, Object?>(),
      );
      expect(read.toJson(), settings.toJson());
      expect(read.displayName, 'Sam');
      expect(read.upnpEnabled, isTrue);
      expect(read.lanDiscoveryEnabled, isFalse);
      expect(read.voiceActive, isTrue);
      expect(read.linkHandlingEnabled, isFalse);
      expect(read.favorites.contains(bookmark(1).serverId), isTrue);
      expect(read.history.entries.single.serverId, bookmark(2).serverId);
      expect(read.history.entries.single.savedAt, bookmark(2).savedAt);
    });

    test('missing members take defaults; wrong ones are refused', () {
      expect(
        MultiplayerSettings.fromJson(const {}).toJson(),
        MultiplayerSettings().toJson(),
      );
      for (final json in <Map<String, Object?>>[
        {'schemaVersion': 2},
        {'displayName': 7},
        {'displayName': 'A'},
        {'upnpEnabled': 'yes'},
        {'voiceConsent': -1},
        {'acknowledgedPrivacyDisclosure': 1.5},
        {'favorites': 'none'},
        {
          'history': [{}],
        },
      ]) {
        expect(
          () => MultiplayerSettings.fromJson(json),
          throwsFormatException,
          reason: '$json',
        );
      }
    });
  });

  group('favorites and history', () {
    test('favorites are unique, newest first, and never dropped', () {
      var favorites = ServerFavorites();
      for (var index = 0; index < ServerFavorites.maximumEntries; index++) {
        favorites = favorites.add(bookmark(index));
      }
      expect(favorites.isFull, isTrue);
      expect(favorites.entries.first.serverId, bookmark(99).serverId);
      expect(() => favorites.add(bookmark(100)), throwsStateError);
      final renamed = favorites.add(bookmark(5, 'Renamed'));
      expect(renamed.entries, hasLength(100));
      expect(renamed.entries.first.name, 'Renamed');
      final removed = renamed.remove(bookmark(5).serverId);
      expect(removed.entries, hasLength(99));
      expect(removed.contains(bookmark(5).serverId), isFalse);
      expect(
        () => ServerFavorites([bookmark(1), bookmark(1)]),
        throwsArgumentError,
      );
      expect(
        () => ServerFavorites([for (var i = 0; i < 101; i++) bookmark(i)]),
        throwsArgumentError,
      );
    });

    test('history keeps the 50 most recent joins, each once', () {
      var history = ServerHistory();
      for (var index = 0; index < 60; index++) {
        history = history.record(bookmark(index));
      }
      expect(history.entries, hasLength(ServerHistory.maximumEntries));
      expect(history.entries.first.serverId, bookmark(59).serverId);
      expect(history.entries.last.serverId, bookmark(10).serverId);
      history = history.record(bookmark(30, 'Again'));
      expect(history.entries, hasLength(50));
      expect(history.entries.first.name, 'Again');
      expect(
        history.entries.where((e) => e.serverId == bookmark(30).serverId),
        hasLength(1),
      );
      expect(history.remove(bookmark(30).serverId).entries, hasLength(49));
      expect(history.clear().entries, isEmpty);
    });

    test('bookmarks hold a server id, a name and a UTC time only', () {
      final local = ServerBookmark(
        serverId: 'a' * 64,
        name: 'Server',
        savedAt: DateTime(2026, 10, 13, 12),
      );
      expect(local.savedAt.isUtc, isTrue);
      expect(local.toJson().keys.toSet(), {'serverId', 'name', 'savedAt'});
      expect(
        () => ServerBookmark(serverId: 'A' * 64, name: 'S', savedAt: when),
        throwsArgumentError,
      );
      expect(
        () => ServerBookmark(serverId: 'a' * 64, name: ' S', savedAt: when),
        throwsArgumentError,
      );
      expect(
        () => ServerBookmark.fromJson({
          'serverId': 'a' * 64,
          'name': 'S',
          'savedAt': 'yesterday',
        }),
        throwsFormatException,
      );
      expect(
        () => ServerFavorites.fromJson([
          bookmark(1).toJson(),
          bookmark(1).toJson(),
        ]),
        throwsFormatException,
      );
      expect(
        () => ServerHistory.fromJson([
          for (var i = 0; i < 51; i++) bookmark(i).toJson(),
        ]),
        throwsFormatException,
      );
    });
  });
}
