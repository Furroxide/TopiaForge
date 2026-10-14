import 'dart:convert';
import 'dart:typed_data';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_fixtures.dart';

void main() {
  final fixture = readMultiplayerFixture('lan/cases.json');
  final beacons = asObject(fixture['beacons']);
  final info = asObject(fixture['sessionInfo']);

  group('LAN beacons', () {
    test('encode to and decode from the fixture bytes', () {
      for (final item in asObjects(beacons['valid'])) {
        final beacon = LanBeacon(
          serverId: item['serverId']! as String,
          gamePort: item['gamePort']! as int,
        );
        final bytes = hexBytes(item['hex']! as String);
        expect(beacon.encode(), bytes);
        expect(LanBeacon.tryDecode(bytes), beacon);
        final padded = Uint8List(bytes.length + 4)..setRange(2, 38, bytes);
        expect(
          LanBeacon.tryDecode(Uint8List.sublistView(padded, 2, 38)),
          beacon,
          reason: 'decodes from inside a larger buffer',
        );
      }
    });

    test('refuses every invalid beacon', () {
      for (final item in asObjects(beacons['invalid'])) {
        expect(
          LanBeacon.tryDecode(hexBytes(item['hex']! as String)),
          isNull,
          reason: item['about']! as String,
        );
      }
      expect(LanBeacon.tryDecode(const []), isNull);
    });

    test('uses the shared ports and timing', () {
      expect(LanBeacon.discoveryPort, 27251);
      expect(LanBeacon.defaultGamePort, 27250);
      expect(LanBeacon.size, 36);
      expect(LanBeacon.interval, const Duration(seconds: 2));
      expect(
        () => LanBeacon(serverId: 'a' * 64, gamePort: 0),
        throwsRangeError,
      );
      expect(
        () => LanBeacon(serverId: 'A' * 64, gamePort: 1),
        throwsArgumentError,
      );
      expect(LanBeacon(serverId: 'a' * 64, gamePort: 65535).encode()[35], 0xFF);
    });
  });

  group('session info', () {
    test('the limit is the info query reply size', () {
      expect(info['maximumBytes'], SessionInfo.maximumBytes);
    });

    for (final item in asObjects(info['valid'])) {
      final id = item['id']! as String;
      test('$id decodes and encodes back to the same bytes', () {
        final bytes = textBytes(item['canonical']! as String);
        final decoded = SessionInfo.decode(bytes);
        expect(decoded.encode(), bytes);
        expect(bytes.length, lessThanOrEqualTo(SessionInfo.maximumBytes));
        if (item.containsKey('bytes')) expect(bytes, hasLength(item['bytes']));
        final fields = asObject(item['info']);
        expect(decoded.toJson(), fields);
        expect(decoded.name, fields['name']);
        expect(decoded.players, fields['players']);
        expect(decoded.gamemode, fields['gamemode']);
        expect(decoded.descriptorSha256, fields['descriptorSha256']);
      });
    }

    test('refuses every invalid info document', () {
      for (final item in asObjects(info['invalid'])) {
        final bytes = textBytes(item['text']! as String);
        expect(
          () => SessionInfo.decode(bytes),
          throwsFormatException,
          reason: item['about']! as String,
        );
        expect(SessionInfo.tryDecode(bytes), isNull);
      }
    });

    test('holds no member that could carry an address', () {
      SessionInfo make({int players = 1, int maxPlayers = 2}) => SessionInfo(
        serverId: 'a' * 64,
        descriptorSha256: 'b' * 64,
        name: 'Name',
        gameBuild: '2545',
        runtimeVersion: '0.2.0',
        protocolVersion: '1.0.0',
        gamemode: null,
        world: null,
        players: players,
        maxPlayers: maxPlayers,
        passwordRequired: false,
        voice: false,
      );
      expect(() => make(players: 3), throwsRangeError);
      expect(() => make(players: -1), throwsRangeError);
      expect(() => make(maxPlayers: 17, players: 0), throwsRangeError);
      final members = (jsonDecode(utf8.decode(make().encode())) as Map).keys;
      expect(members.toSet(), {
        'schema',
        'serverId',
        'descriptorSha256',
        'name',
        'gameBuild',
        'runtimeVersion',
        'protocolVersion',
        'gamemode',
        'world',
        'players',
        'maxPlayers',
        'passwordRequired',
        'voice',
      });
    });

    test('refuses oversized documents before parsing', () {
      final bytes = Uint8List(SessionInfo.maximumBytes + 1)
        ..fillRange(0, SessionInfo.maximumBytes + 1, 0x20);
      expect(() => SessionInfo.decode(bytes), throwsFormatException);
    });
  });

  group('server listings', () {
    final typical = SessionInfo.decode(
      textBytes(asObjects(info['valid']).first['canonical']! as String),
    );

    test('a LAN row comes from session info and holds no address', () {
      final listing = ServerListing.fromSessionInfo(
        typical,
        pingMilliseconds: 12,
      );
      expect(listing.source, ServerListingSource.lan);
      expect(listing.serverId, typical.serverId);
      expect(listing.name, typical.name);
      expect(listing.players, 3);
      expect(listing.maxPlayers, 8);
      expect(listing.isFull, isFalse);
      expect(listing.pingMilliseconds, 12);
      expect(listing.readiness, ServerReadiness.unknown);
      expect(listing.descriptorSha256, typical.descriptorSha256);
      final ready = listing.withReadiness(ServerReadiness.ready).withPing(null);
      expect(ready.readiness, ServerReadiness.ready);
      expect(ready.pingMilliseconds, isNull);
      expect(ready.serverId, listing.serverId);
    });

    test('rows are validated like the formats they come from', () {
      ServerListing make({
        String serverId = 'a',
        String name = 'Server',
        int players = 0,
        int maxPlayers = 8,
        int? ping,
        String? gameBuild,
      }) => ServerListing(
        serverId: serverId.length == 1 ? serverId * 64 : serverId,
        name: name,
        source: ServerListingSource.internet,
        players: players,
        maxPlayers: maxPlayers,
        passwordRequired: true,
        voice: false,
        pingMilliseconds: ping,
        gameBuild: gameBuild,
      );
      expect(make(players: 8).isFull, isTrue);
      expect(() => make(serverId: 'A'), throwsArgumentError);
      expect(() => make(name: ' padded'), throwsArgumentError);
      expect(() => make(players: 9), throwsRangeError);
      expect(() => make(maxPlayers: 1), throwsRangeError);
      expect(() => make(ping: -1), throwsRangeError);
      expect(() => make(ping: 60001), throwsRangeError);
      expect(() => make(gameBuild: '25 45'), throwsArgumentError);
      expect(make(ping: 60000).pingMilliseconds, 60000);
    });
  });
}
