import 'dart:io';
import 'dart:typed_data';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_data/src/multiplayer/lan_datagrams.dart';
import 'package:launcher_data/src/multiplayer/lan_discovery_client.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_loopback_udp.dart';
import 'multiplayer_network_support.dart';

/// The LAN client over real UDP sockets. Every socket binds the loopback
/// interface, never every address, so no firewall prompt can appear.
///
/// What the client accepts and refuses is proven over fake sockets, where
/// nothing waits on the OS (`multiplayer_lan_discovery_test.dart`). These
/// tests show the same holds through the real socket code. Every wait in them
/// is one the host retries until it is met, so none can hang on a datagram
/// that was never sent or never arrived (`multiplayer_loopback_udp.dart`).
void main() {
  late ObservedLanBinder binder;
  late LanDiscoveryClient client;
  late LoopbackUdpHost host;

  setUp(() async {
    binder = ObservedLanBinder();
    client = LanDiscoveryClient(
      listenAddress: InternetAddress.loopbackIPv4,
      port: 0,
      queryAddress: InternetAddress.loopbackIPv4,
      bind: binder.call,
    );
    addTearDown(client.dispose);
    expect((await client.start()).status, LanDiscoveryStatus.listening);
    host = await LoopbackUdpHost.bind();
    addTearDown(host.close);
  });

  /// Beacons, as a game host does, until the client asks for its info.
  Future<Datagram> beaconUntilQueried() => host.repeatUntil(
    host.firstReceived,
    () => host.send(
      LanBeacon(serverId: hostAServerId, gamePort: host.port).encode(),
      InternetAddress.loopbackIPv4,
      client.listeningPort!,
    ),
    what: 'A query for the beacon',
  );

  Uint8List replyTo(Datagram query) => LanInfoReply.encode(
    queryId: query.data.sublist(2, 10),
    info: sessionInfoFixture('typical'),
  );

  /// Answers [query] with [reply] until the client lists the session.
  Future<LanBrowseSnapshot> answerUntilListed(Datagram query, Uint8List reply) {
    final listed = client.snapshots.firstWhere(
      (snapshot) => snapshot.listings.isNotEmpty,
    );
    return host.repeatUntil(
      listed,
      () => host.send(reply, query.address, query.port),
      what: 'A listing for the reply',
    );
  }

  test('hears a beacon, queries the host and lists its session', () async {
    final query = await beaconUntilQueried();
    expect(query.data, hasLength(LanInfoQuery.size));
    expect(query.data[0], LanInfoQuery.packetType);
    expect(query.data[1], LanInfoQuery.version);
    expect(query.data.sublist(10).every((value) => value == 0), isTrue);

    final snapshot = await answerUntilListed(query, replyTo(query));
    expect(snapshot.listings.single.serverId, hostAServerId);
    expect(snapshot.listings.single.source, ServerListingSource.lan);
    // How often one host is asked is the fake rig's to prove: a runner slow
    // enough to hold the first query past the requery window asks again.
    expect(snapshot.counters.queriesSent, greaterThanOrEqualTo(1));
    expect(snapshot.counters.repliesAccepted, 1);
  });

  test('ignores an oversized reply sent over a real socket', () async {
    final query = await beaconUntilQueried();
    final reply = replyTo(query);
    // One byte past the query, with a length field that agrees with it.
    const oversizedLength = LanInfoQuery.size + 1;
    final oversized = Uint8List(oversizedLength)
      ..setRange(0, reply.length, reply)
      ..[10] = (oversizedLength - LanInfoReply.headerBytes) & 0xFF
      ..[11] = (oversizedLength - LanInfoReply.headerBytes) >> 8;

    // The real socket hands the client the whole datagram, and the client
    // refuses it.
    await host.repeatUntil(
      binder.querier.handled.firstWhere((size) => size == oversizedLength),
      () => host.send(oversized, query.address, query.port),
      what: 'The oversized reply reaching the client whole',
    );
    expect(binder.querier.handledSizes, everyElement(oversizedLength));
    expect(
      client.snapshot.counters.repliesRefused,
      binder.querier.handledSizes.length,
    );
    expect(client.snapshot.counters.repliesAccepted, 0);
    expect(client.snapshot.listings, isEmpty);

    // The refusal left the query open: the real reply is still accepted.
    final snapshot = await answerUntilListed(query, reply);
    final beforeReal = binder.querier.handledSizes.takeWhile(
      (size) => size != reply.length,
    );
    expect(beforeReal, everyElement(oversizedLength));
    expect(snapshot.counters.repliesRefused, beforeReal.length);
    expect(snapshot.counters.repliesAccepted, 1);
    expect(snapshot.listings.single.serverId, hostAServerId);
  });
}
