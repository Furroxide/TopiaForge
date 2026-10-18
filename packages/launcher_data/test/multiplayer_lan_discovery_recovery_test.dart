import 'dart:async';
import 'dart:io';

import 'package:launcher_data/src/multiplayer/lan_datagrams.dart';
import 'package:launcher_data/src/multiplayer/lan_discovery_client.dart';
import 'package:launcher_data/src/multiplayer/lan_sockets.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_lan_rig.dart';
import 'multiplayer_network_support.dart';

/// LAN discovery after the system closed its query socket, which it does
/// after a send it refused, such as a query to a network it cannot reach.
/// Nothing sent through that socket arrives, so a new one takes its place.
void main() {
  final typical = sessionInfoFixture('typical');

  test('replaces a query socket the system closed, and asks the host again '
      'on its next beacon', () async {
    final rig = LanRig.fresh();
    await rig.client.start();
    final closed = rig.querier..closedBySystem = true;

    rig.beacon(hostAServerId, lanHost);
    expect(closed.sent, isEmpty);
    expect(rig.client.snapshot.counters.queriesUnsent, 1);
    await pumpEventQueue();
    expect(rig.binder.binds.last, (InternetAddress.loopbackIPv4, 0, false));
    expect(closed.closed, isTrue);
    expect(rig.client.snapshot.counters.querySocketsReplaced, 1);

    // The host was left unqueried, so its next beacon is asked at once.
    final replacement = rig.binder.sockets.last;
    rig.beacon(hostAServerId, lanHost);
    final query = replacement.sent.single;
    replacement.deliver(
      LanInfoReply.encode(queryId: query.data.sublist(2, 10), info: typical),
      query.address,
      query.port,
    );
    expect(rig.client.snapshot.listings.single.serverId, hostAServerId);
    expect(rig.client.snapshot.counters.queriesSent, 1);
  });

  test(
    'binds one replacement however many beacons find the socket closed',
    () async {
      final rig = LanRig.fresh();
      await rig.client.start();
      rig.querier.closedBySystem = true;
      rig
        ..beacon(hostAServerId, lanHost)
        ..beacon(hostBServerId, otherLanHost);
      expect(rig.client.snapshot.counters.queriesUnsent, 2);
      await pumpEventQueue();
      expect(rig.binder.sockets, hasLength(3));
      expect(rig.client.snapshot.counters.querySocketsReplaced, 1);
    },
  );

  test(
    'a replacement bound after browsing restarted is closed, not used',
    () async {
      final held = Completer<void>();
      final binds = <int, FakeLanSocket>{};
      var calls = 0;
      final client = LanDiscoveryClient(
        listenAddress: InternetAddress.loopbackIPv4,
        port: 0,
        queryAddress: InternetAddress.loopbackIPv4,
        bind: (address, port, {required reuseAddress}) async {
          // The third bind is the replacement; it waits until the test lets
          // it finish.
          final call = ++calls;
          if (call == 3) await held.future;
          return binds[call] = FakeLanSocket(50000 + call);
        },
        clock: FakeClock().call,
        ticker: ManualTicker().call,
      );
      addTearDown(client.dispose);
      await client.start();
      binds[2]!.closedBySystem = true;
      binds[1]!.deliver(
        LanBeacon(serverId: hostAServerId, gamePort: 27250).encode(),
        lanHost,
        40000,
      );
      await client.stop();
      await client.start();
      held.complete();
      await pumpEventQueue();
      // Binds 4 and 5 are the restarted browser's own sockets.
      expect(binds.keys, unorderedEquals([1, 2, 3, 4, 5]));
      expect(binds[3]!.closed, isTrue);
      expect(binds[5]!.closed, isFalse);
      expect(client.snapshot.counters.querySocketsReplaced, 0);
      binds[4]!.deliver(
        LanBeacon(serverId: hostAServerId, gamePort: 27250).encode(),
        lanHost,
        40000,
      );
      expect(binds[5]!.sent, hasLength(1));
    },
  );

  test(
    'a replacement that cannot be bound is tried again by a later query',
    () async {
      var refuse = true;
      final sockets = <LanDatagramSocket>[];
      final client = LanDiscoveryClient(
        listenAddress: InternetAddress.loopbackIPv4,
        port: 0,
        queryAddress: InternetAddress.loopbackIPv4,
        bind: (address, port, {required reuseAddress}) async {
          if (sockets.length == 2 && refuse) {
            refuse = false;
            throw const SocketException('No buffer space is available.');
          }
          final socket = FakeLanSocket(50000 + sockets.length);
          sockets.add(socket);
          return socket;
        },
        clock: FakeClock().call,
        ticker: ManualTicker().call,
      );
      addTearDown(client.dispose);
      await client.start();
      (sockets[1] as FakeLanSocket).closedBySystem = true;
      final listener = sockets[0] as FakeLanSocket;
      void beacon() => listener.deliver(
        LanBeacon(serverId: hostAServerId, gamePort: 27250).encode(),
        lanHost,
        40000,
      );
      beacon();
      await pumpEventQueue();
      expect(sockets, hasLength(2));
      beacon();
      await pumpEventQueue();
      expect(sockets, hasLength(3));
      expect(client.snapshot.counters.querySocketsReplaced, 1);
    },
  );
}
