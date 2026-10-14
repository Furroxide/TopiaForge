import 'dart:async';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_data/src/multiplayer/lan_datagrams.dart';
import 'package:launcher_data/src/multiplayer/lan_discovery_client.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_network_support.dart';

/// The LAN client over real UDP sockets. Every socket binds the loopback
/// interface, never every address, so no firewall prompt can appear.
void main() {
  test('hears a beacon, queries the host and lists its session', () async {
    final client = LanDiscoveryClient(
      listenAddress: InternetAddress.loopbackIPv4,
      port: 0,
      queryAddress: InternetAddress.loopbackIPv4,
    );
    addTearDown(client.dispose);
    expect((await client.start()).status, LanDiscoveryStatus.listening);

    final host = await RawDatagramSocket.bind(InternetAddress.loopbackIPv4, 0);
    addTearDown(host.close);
    final queried = Completer<Datagram>();
    host.listen((event) {
      if (event != RawSocketEvent.read) return;
      final datagram = host.receive();
      if (datagram != null && !queried.isCompleted) queried.complete(datagram);
    });

    final listed = client.snapshots.firstWhere(
      (snapshot) => snapshot.listings.isNotEmpty,
    );
    host.send(
      LanBeacon(serverId: hostAServerId, gamePort: host.port).encode(),
      InternetAddress.loopbackIPv4,
      client.listeningPort!,
    );
    final query = await queried.future.timeout(const Duration(seconds: 10));
    expect(query.data, hasLength(LanInfoQuery.size));
    expect(query.data[0], LanInfoQuery.packetType);
    expect(query.data[1], LanInfoQuery.version);
    expect(query.data.sublist(10).every((value) => value == 0), isTrue);

    host.send(
      LanInfoReply.encode(
        queryId: query.data.sublist(2, 10),
        info: sessionInfoFixture('typical'),
      ),
      query.address,
      query.port,
    );
    final snapshot = await listed.timeout(const Duration(seconds: 10));
    expect(snapshot.listings.single.serverId, hostAServerId);
    expect(snapshot.listings.single.source, ServerListingSource.lan);
    expect(snapshot.counters.queriesSent, 1);
    expect(snapshot.counters.repliesAccepted, 1);
  });

  test('ignores an oversized reply sent over a real socket', () async {
    final client = LanDiscoveryClient(
      listenAddress: InternetAddress.loopbackIPv4,
      port: 0,
      queryAddress: InternetAddress.loopbackIPv4,
    );
    addTearDown(client.dispose);
    await client.start();
    final host = await RawDatagramSocket.bind(InternetAddress.loopbackIPv4, 0);
    addTearDown(host.close);
    final queried = Completer<Datagram>();
    host.listen((event) {
      if (event != RawSocketEvent.read) return;
      final datagram = host.receive();
      if (datagram != null && !queried.isCompleted) queried.complete(datagram);
    });
    host.send(
      LanBeacon(serverId: hostAServerId, gamePort: host.port).encode(),
      InternetAddress.loopbackIPv4,
      client.listeningPort!,
    );
    final query = await queried.future.timeout(const Duration(seconds: 10));
    final reply = LanInfoReply.encode(
      queryId: query.data.sublist(2, 10),
      info: sessionInfoFixture('typical'),
    );
    final oversized = [...reply, ...List.filled(1201 - reply.length, 0)]
      ..[10] = (1201 - 12) & 0xFF
      ..[11] = (1201 - 12) >> 8;
    host.send(oversized, query.address, query.port);
    // A real reply right behind it shows the oversized one arrived first and
    // was refused, not lost.
    final listed = client.snapshots.firstWhere(
      (snapshot) => snapshot.listings.isNotEmpty,
    );
    host.send(reply, query.address, query.port);
    final snapshot = await listed.timeout(const Duration(seconds: 10));
    expect(snapshot.counters.repliesRefused, 1);
    expect(snapshot.counters.repliesAccepted, 1);
  });
}
