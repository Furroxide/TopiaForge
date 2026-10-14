import 'dart:io';
import 'dart:typed_data';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_data/src/multiplayer/lan_datagrams.dart';
import 'package:launcher_data/src/multiplayer/lan_discovery_client.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_lan_rig.dart';
import 'multiplayer_network_support.dart';

/// LAN discovery over fake sockets: what is queried, listed and refused.
void main() {
  final lan = readMultiplayerFixture('lan/cases.json');
  final beacons = asObject(lan['beacons']);
  final typical = sessionInfoFixture('typical');

  test(
    'binds the listener with address reuse and the query socket apart',
    () async {
      final rig = LanRig.fresh();
      final started = await rig.client.start();
      expect(started.status, LanDiscoveryStatus.listening);
      expect(rig.binder.binds, [
        (InternetAddress.loopbackIPv4, 0, true),
        (InternetAddress.loopbackIPv4, 0, false),
      ]);
      expect(rig.client.listeningPort, rig.listener.port);
      expect(rig.ticker.started, 1);
    },
  );

  test(
    'replays the shared beacons: each valid one queries its sender',
    () async {
      final rig = LanRig.fresh();
      await rig.client.start();
      for (final item in asObjects(beacons['valid'])) {
        rig.listener.deliver(hexBytes(item['hex']! as String), lanHost, 40000);
        final query = rig.querier.sent.last;
        expect(query.address, lanHost);
        expect(query.port, item['gamePort']);
        expect(query.data, hasLength(1200));
        expect(query.data[0], LanInfoQuery.packetType);
      }
      expect(rig.querier.sent, hasLength(2));
      expect(rig.client.snapshot.pending, 2);
    },
  );

  test('drops every invalid fixture beacon without a query', () async {
    final rig = LanRig.fresh();
    await rig.client.start();
    final invalid = asObjects(beacons['invalid']);
    for (final item in invalid) {
      rig.listener.deliver(hexBytes(item['hex']! as String), lanHost, 40000);
    }
    expect(rig.querier.sent, isEmpty);
    expect(rig.client.snapshot.counters.invalidBeacons, invalid.length);
    expect(rig.client.snapshot.pending, 0);
  });

  test('lists a session once its info names the beacon\'s server', () async {
    final rig = LanRig.fresh();
    await rig.client.start();
    final published = <LanBrowseSnapshot>[];
    rig.client.snapshots.listen(published.add);
    rig.beacon(hostAServerId, lanHost);
    rig.clock.advance(const Duration(milliseconds: 12));
    rig.answer(typical);
    await Future<void>.delayed(Duration.zero);
    final snapshot = rig.client.snapshot;
    expect(snapshot.listings, hasLength(1));
    final listing = snapshot.listings.single;
    expect(listing.serverId, hostAServerId);
    expect(listing.name, "Charl's Sandbox");
    expect(listing.source, ServerListingSource.lan);
    expect(listing.players, 3);
    expect(listing.maxPlayers, 8);
    expect(listing.pingMilliseconds, 12);
    expect(snapshot.pending, 0);
    expect(published.last.listings, hasLength(1));
    expect(snapshot.counters.repliesAccepted, 1);
  });

  test('replays the shared invalid session info: nothing is listed', () async {
    final invalid = asObjects(asObject(lan['sessionInfo'])['invalid']);
    final rig = LanRig.fresh();
    await rig.client.start();
    for (final item in invalid) {
      rig.clock.advance(const Duration(seconds: 3));
      rig.beacon(hostAServerId, lanHost);
      rig.answer(textBytes(item['text']! as String));
    }
    expect(rig.client.snapshot.listings, isEmpty);
    expect(rig.client.snapshot.counters.unusableInfo, invalid.length);
  });

  test('never queries a beacon from beyond the local network', () async {
    final rig = LanRig.fresh();
    await rig.client.start();
    for (final (index, sender) in [
      publicHost,
      publicHostV6,
      InternetAddress('100.64.0.1'),
      InternetAddress('::ffff:203.0.113.9'),
    ].indexed) {
      rig.beacon(serverIdOf(index), sender);
      rig.clock.advance(const Duration(seconds: 3));
      rig.beacon(serverIdOf(index), sender);
    }
    expect(rig.querier.sent, isEmpty);
    final snapshot = rig.client.snapshot;
    expect(snapshot.beyondLocalNetwork, 4);
    expect(snapshot.listings, isEmpty);
    expect(snapshot.pending, 0);
  });

  test(
    'an IPv4-mapped sender on the local network is queried as IPv4',
    () async {
      final rig = LanRig.fresh();
      await rig.client.start();
      rig.beacon(hostAServerId, InternetAddress('::ffff:192.168.1.20'));
      expect(rig.querier.sent.single.address, lanHost);
    },
  );

  test('accepts a reply only from the socket the query went to', () async {
    final rig = LanRig.fresh();
    await rig.client.start();
    rig.beacon(hostAServerId, lanHost);
    rig.answer(typical, from: otherLanHost);
    rig.answer(typical, port: 27251);
    expect(rig.client.snapshot.listings, isEmpty);
    expect(rig.client.snapshot.counters.repliesRefused, 2);
    rig.answer(typical);
    expect(rig.client.snapshot.listings, hasLength(1));
  });

  test('accepts a reply only when it is no larger than the query', () async {
    final rig = LanRig.fresh();
    await rig.client.start();
    rig.beacon(hostAServerId, lanHost);
    final query = rig.querier.sent.last;
    // One byte past the query, with a length field that agrees with it.
    final valid = LanInfoReply.encode(
      queryId: query.data.sublist(2, 10),
      info: typical,
    );
    final oversized = Uint8List(LanInfoQuery.size + 1)
      ..setRange(0, valid.length, valid)
      ..[10] = (LanInfoQuery.size + 1 - 12) & 0xFF
      ..[11] = (LanInfoQuery.size + 1 - 12) >> 8;
    rig.querier.deliver(oversized, query.address, query.port);
    expect(rig.client.snapshot.listings, isEmpty);
    expect(rig.client.snapshot.counters.repliesRefused, 1);
    rig.answer(typical);
    expect(rig.client.snapshot.listings, hasLength(1));
  });

  test('refuses unknown query ids, repeats and late replies', () async {
    final rig = LanRig.fresh();
    await rig.client.start();
    rig.beacon(hostAServerId, lanHost);
    rig.answer(typical, queryId: List.filled(8, 9));
    expect(rig.client.snapshot.listings, isEmpty);
    rig.clock.advance(LanDiscoveryClient.queryLifetime);
    rig.answer(typical);
    expect(rig.client.snapshot.listings, isEmpty);
    expect(rig.client.snapshot.counters.repliesRefused, 2);

    rig.beacon(hostAServerId, lanHost);
    rig.answer(typical);
    rig.answer(typical);
    expect(rig.client.snapshot.counters.repliesAccepted, 1);
    expect(rig.client.snapshot.counters.repliesRefused, 3);
  });

  test('a beacon naming another server never borrows the reply', () async {
    final rig = LanRig.fresh();
    await rig.client.start();
    rig.beacon(hostBServerId, lanHost);
    rig.answer(typical);
    expect(rig.client.snapshot.listings, isEmpty);
    expect(rig.client.snapshot.pending, 1);
    expect(rig.client.snapshot.counters.unusableInfo, 1);
  });

  test('a moved server drops what its old socket answered', () async {
    final rig = LanRig.fresh();
    await rig.client.start();
    rig.beacon(hostAServerId, lanHost);
    rig.answer(typical);
    expect(rig.client.snapshot.listings, hasLength(1));
    rig.beacon(hostAServerId, otherLanHost);
    expect(rig.client.snapshot.listings, isEmpty);
    expect(rig.client.snapshot.pending, 1);
    expect(rig.querier.sent.last.address, otherLanHost);
    rig.beacon(hostAServerId, lanHost, gamePort: 27260);
    expect(rig.querier.sent.last.port, 27260);
    rig.answer(typical);
    expect(rig.client.snapshot.listings, hasLength(1));
  });

  test(
    'a host of another transport version is listed as incompatible',
    () async {
      final rig = LanRig.fresh();
      await rig.client.start();
      rig.beacon(hostAServerId, lanHost);
      rig.answer(const [], version: 2);
      final snapshot = rig.client.snapshot;
      expect(snapshot.incompatibleServerIds, [hostAServerId]);
      expect(snapshot.listings, isEmpty);
      expect(snapshot.pending, 0);
    },
  );

  test('sessions silent for six seconds leave the list', () async {
    final rig = LanRig.fresh();
    await rig.client.start();
    rig.beacon(hostAServerId, lanHost);
    rig.answer(typical);
    rig.clock.advance(const Duration(milliseconds: 5999));
    rig.ticker.fire();
    expect(rig.client.snapshot.listings, hasLength(1));
    rig.clock.advance(const Duration(milliseconds: 1));
    rig.ticker.fire();
    expect(rig.client.snapshot.listings, isEmpty);
    expect(rig.client.snapshot.pending, 0);
  });

  test('asks a host again at most every two seconds', () async {
    final rig = LanRig.fresh();
    await rig.client.start();
    rig.beacon(hostAServerId, lanHost);
    rig.clock.advance(const Duration(milliseconds: 1999));
    rig.beacon(hostAServerId, lanHost);
    expect(rig.querier.sent, hasLength(1));
    rig.clock.advance(const Duration(milliseconds: 1));
    rig.beacon(hostAServerId, lanHost);
    expect(rig.querier.sent, hasLength(2));
  });
}
