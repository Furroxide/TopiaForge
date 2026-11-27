/// Network diagnostics for multiplayer: the checks behind a
/// [MultiplayerNetworkReport], which says what the service, the ports and LAN
/// discovery look like from this machine. The report is the domain's, so the
/// launcher's screens can hold it; this library keeps exporting it. Nothing
/// in it names an address, so a report can go into a diagnostic bundle as it
/// is.
///
/// The developer-only reachability probe (`reachability_probe_service.dart`)
/// is separate and unchanged: it contacts STUN servers and stays behind
/// developer mode.
library;

import 'dart:io';

import 'package:launcher_domain/launcher_domain.dart';

export 'package:launcher_domain/launcher_domain.dart'
    show
        MultiplayerNetworkReport,
        NetworkCheckState,
        PortCheck,
        PortCheckResult,
        PortProtocol;

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
  ///
  /// [gamePort], when given, is the port the player hosts sessions on, in
  /// place of [MultiplayerNetworkDiagnostics.gamePort]. A session hosted on
  /// port 0 takes any free port, which no check can speak for, so the game
  /// port is then not checked.
  Future<MultiplayerNetworkReport> run({
    required MultiplayerServiceStatus service,
    required LanBrowseSnapshot lan,
    bool checkPorts = false,
    int? gamePort,
    bool developerMode = false,
    CancellationToken? cancellation,
  }) async {
    PortCheckResult skipped() => PortCheckResult.notChecked;
    final port = gamePort ?? this.gamePort;
    final checkGamePort = checkPorts && gamePort != 0;
    final udp = checkGamePort ? await _checkUdp(port, reuse: false) : skipped();
    final tcp = checkGamePort ? await _checkTcp(port) : skipped();
    // The launcher's own browser already holds the discovery port.
    final discovery = !checkPorts
        ? skipped()
        : lan.status == LanDiscoveryStatus.listening
        ? PortCheckResult.available
        : await _checkUdp(lanPort, reuse: true);
    return MultiplayerNetworkReport(
      service: service,
      gamePortUdp: PortCheck(port, PortProtocol.udp, udp),
      gamePortTcp: PortCheck(port, PortProtocol.tcp, tcp),
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
      return _resultOf(error, shared: reuse);
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
      return _resultOf(error, shared: false);
    } finally {
      await socket?.close();
    }
  }

  /// Address-in-use and access-denied codes on Windows, Linux and macOS. On
  /// Windows a [shared] bind, as the discovery port's is, fails with
  /// WSAEACCES (10013) on a port another program holds exclusively, so there
  /// it means in use.
  static PortCheckResult _resultOf(
    SocketException error, {
    required bool shared,
  }) => switch (error.osError?.errorCode) {
    10048 || 98 || 48 => PortCheckResult.inUse,
    10013 when shared => PortCheckResult.inUse,
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
