part of '../multiplayer.dart';

// Network diagnostics for multiplayer, as data: what the service, the ports
// and LAN discovery look like from this machine. Nothing here names an
// address, so a report can go into a diagnostic bundle as it is. The data
// layer's MultiplayerNetworkDiagnostics makes them.

/// Whether this build names a multiplayer service.
enum MultiplayerServiceStatus {
  /// No origin was configured; nothing is ever contacted.
  unconfigured,

  /// The configured origin is not an absolute HTTPS origin, so it is
  /// ignored and nothing is contacted.
  invalid,

  /// An origin is configured.
  configured,
}

/// What checking one port found.
enum PortCheckResult {
  /// The check was not run; it binds sockets, so it runs only on request.
  notChecked,

  /// The port could be bound just now. That is all it means: it says nothing
  /// of whether a firewall, the router or other players let anything reach
  /// it.
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

/// One run of the data layer's network diagnostics.
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
