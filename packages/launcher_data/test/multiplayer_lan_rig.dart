import 'dart:convert';
import 'dart:io';
import 'dart:math';

import 'package:launcher_data/src/multiplayer/descriptor_client.dart';
import 'package:launcher_data/src/multiplayer/host_package_client.dart';
import 'package:launcher_data/src/multiplayer/lan_datagrams.dart';
import 'package:launcher_data/src/multiplayer/lan_discovery_client.dart';
import 'package:launcher_domain/launcher_domain.dart';

import 'multiplayer_network_support.dart';

/// A LAN client over fake sockets, with time and timers in the test's hands.
final class LanRig {
  LanRig({DescriptorClient? descriptors, HostPackageClient? packages}) {
    client = LanDiscoveryClient(
      listenAddress: InternetAddress.loopbackIPv4,
      port: 0,
      queryAddress: InternetAddress.loopbackIPv4,
      bind: binder.call,
      descriptors: descriptors,
      packages: packages,
      random: Random(7),
      clock: clock.call,
      ticker: ticker.call,
    );
  }

  static LanRig fresh({DescriptorClient? descriptors}) =>
      LanRig(descriptors: descriptors);

  late final LanDiscoveryClient client;
  final binder = FakeLanBinder();
  final clock = FakeClock();
  final ticker = ManualTicker();

  FakeLanSocket get listener => binder.listener;
  FakeLanSocket get querier => binder.querier;

  void beacon(String serverId, InternetAddress from, {int gamePort = 27250}) =>
      listener.deliver(
        LanBeacon(serverId: serverId, gamePort: gamePort).encode(),
        from,
        40000,
      );

  /// Answers the newest query with [info], from where it went unless
  /// [from] or [port] say otherwise.
  void answer(
    List<int> info, {
    InternetAddress? from,
    int? port,
    int version = 1,
    List<int>? queryId,
  }) {
    final query = querier.sent.last;
    querier.deliver(
      LanInfoReply.encode(
        queryId: queryId ?? query.data.sublist(2, 10),
        info: info,
        version: version,
      ),
      from ?? query.address,
      port ?? query.port,
    );
  }
}

String serverIdOf(int index) =>
    PureSha256.hex(utf8.encode('server $index')).toLowerCase();
