/// Network diagnostics for multiplayer, as data: what the service, the ports
/// and LAN discovery look like from this machine. Nothing here names an
/// address, so a report can go into a diagnostic bundle as it is.
///
/// The developer-only reachability probe (`reachability_probe_service.dart`)
/// is separate and unchanged: it contacts STUN servers and stays behind
/// developer mode.
library;

import 'dart:io';

import 'package:launcher_domain/launcher_domain.dart';

import '../cancellation_token.dart';
import 'control_plane.dart';
import 'lan_discovery_state.dart';

/// What checking one port found.
enum PortCheckResult {
  /// The check was not run; it binds sockets, so it runs only on request.
  notChecked,

  /// The port could be bound, so hosting or browsing can use it.
  available,

  /// Another program holds the port.
  inUse,

  /// The system refused the port, as a firewall, policy or reserved range
  /// can.
  denied,

  /// The port could not be bound for another reason.
  failed,
}

enum PortProtocol { udp, tcp }

/// One port the game or the launcher needs.
final class PortCheck {
  const PortCheck(this.port, this.protocol, this.result);

  final int port;
  final PortProtocol protocol;
  final PortCheckResult result;

  Map<String, Object?> toJson() => {
    'port': port,
    'protocol': protocol.name,
    'result': result.name,
  };
}

/// The state of a check a later slice provides.
enum NetworkCheckState {
  /// This build has no such check yet.
  notAvailable,

  /// Not run here, as the relay check outside developer mode.
  skipped,
  passed,
  failed,
}

/// Whether a gateway on the local network answers UPnP port-mapping
/// requests. The client arrives with the hosting slice; this is its seam.
abstract interface class UpnpPresenceCheck {
  Future<NetworkCheckState> check({CancellationToken? cancellation});
}

/// Whether relay credentials can be minted and a relay reached, for
/// developer mode. The client arrives with the relay slice; this is its seam.
abstract interface class TurnRelayCheck {
  Future<NetworkCheckState> check({CancellationToken? cancellation});
}

/// One run of [MultiplayerNetworkDiagnostics].
final class MultiplayerNetworkReport {
  const MultiplayerNetworkReport({
    required this.service,
    required this.gamePortUdp,
    required this.gamePortTcp,
    required this.lanPort,
    required this.lan,
    required this.upnp,
    required this.turn,
  });

  static const schema = 'topiaforge.launcher-network-report/1';

  final MultiplayerServiceStatus service;

  /// The game's UDP port, which carries sessions.
  final PortCheck gamePortUdp;

  /// The same port number over TCP, where a host serves its descriptor.
  final PortCheck gamePortTcp;

  /// The LAN discovery port, shared with the game.
  final PortCheck lanPort;
  final LanBrowseSnapshot lan;
  final NetworkCheckState upnp;
  final NetworkCheckState turn;

  Map<String, Object?> toJson() => {
    'schema': schema,
    'service': service.name,
    'ports': [gamePortUdp.toJson(), gamePortTcp.toJson(), lanPort.toJson()],
    'lanDiscovery': lan.toJson(),
    'upnp': upnp.name,
    'turn': turn.name,
  };
}

/// Builds [MultiplayerNetworkReport]s.
///
/// Port checks bind each port for a moment and close it. Binding every
/// address is what the game does, and on Windows it can show a firewall
/// prompt, so the checks run only when asked ([run]'s `checkPorts`) and the
/// address is a parameter; tests pass loopback.
final class MultiplayerNetworkDiagnostics {
  MultiplayerNetworkDiagnostics({
    InternetAddress? bindAddress,
    this.gamePort = LanBeacon.defaultGamePort,
    this.lanPort = LanBeacon.discoveryPort,
    UpnpPresenceCheck? upnp,
    TurnRelayCheck? turn,
  }) : _bindAddress = bindAddress ?? InternetAddress.anyIPv4,
       _upnp = upnp,
       _turn = turn;

  final int gamePort;
  final int lanPort;
  final InternetAddress _bindAddress;
  final UpnpPresenceCheck? _upnp;
  final TurnRelayCheck? _turn;

  /// Reports on the service, the ports and LAN discovery. Never throws.
  Future<MultiplayerNetworkReport> run({
    required MultiplayerServiceStatus service,
    required LanBrowseSnapshot lan,
    bool checkPorts = false,
    bool developerMode = false,
    CancellationToken? cancellation,
  }) async {
    PortCheckResult skipped() => PortCheckResult.notChecked;
    final udp = checkPorts
        ? await _checkUdp(gamePort, reuse: false)
        : skipped();
    final tcp = checkPorts ? await _checkTcp(gamePort) : skipped();
    // The launcher's own browser already holds the discovery port.
    final discovery = !checkPorts
        ? skipped()
        : lan.status == LanDiscoveryStatus.listening
        ? PortCheckResult.available
        : await _checkUdp(lanPort, reuse: true);
    return MultiplayerNetworkReport(
      service: service,
      gamePortUdp: PortCheck(gamePort, PortProtocol.udp, udp),
      gamePortTcp: PortCheck(gamePort, PortProtocol.tcp, tcp),
      lanPort: PortCheck(lanPort, PortProtocol.udp, discovery),
      lan: lan,
      upnp: await _seam(_upnp?.check, cancellation),
      turn: developerMode
          ? await _seam(_turn?.check, cancellation)
          : NetworkCheckState.skipped,
    );
  }

  Future<PortCheckResult> _checkUdp(int port, {required bool reuse}) async {
    RawDatagramSocket? socket;
    try {
      socket = await RawDatagramSocket.bind(
        _bindAddress,
        port,
        reuseAddress: reuse,
      );
      return PortCheckResult.available;
    } on SocketException catch (error) {
      return _resultOf(error);
    } finally {
      socket?.close();
    }
  }

  Future<PortCheckResult> _checkTcp(int port) async {
    ServerSocket? socket;
    try {
      socket = await ServerSocket.bind(_bindAddress, port);
      return PortCheckResult.available;
    } on SocketException catch (error) {
      return _resultOf(error);
    } finally {
      await socket?.close();
    }
  }

  /// Address-in-use and access-denied codes on Windows, Linux and macOS.
  static PortCheckResult _resultOf(SocketException error) =>
      switch (error.osError?.errorCode) {
        10048 || 98 || 48 => PortCheckResult.inUse,
        10013 || 13 => PortCheckResult.denied,
        _ => PortCheckResult.failed,
      };

  static Future<NetworkCheckState> _seam(
    Future<NetworkCheckState> Function({CancellationToken? cancellation})?
    check,
    CancellationToken? cancellation,
  ) async {
    if (check == null) return NetworkCheckState.notAvailable;
    try {
      return await check(cancellation: cancellation);
    } on Object {
      return NetworkCheckState.failed;
    }
  }
}
