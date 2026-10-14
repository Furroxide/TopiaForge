import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:test/test.dart';

import 'multiplayer_network_support.dart';

final class _Check implements UpnpPresenceCheck, TurnRelayCheck {
  _Check(this._result);

  final Future<NetworkCheckState> Function() _result;
  var runs = 0;

  @override
  Future<NetworkCheckState> check({CancellationToken? cancellation}) {
    runs++;
    return _result();
  }
}

/// Binds one port number over TCP and UDP on loopback, prints it, and holds
/// both until its parent ends it.
///
/// Candidate ports are random, because Windows reserves whole ranges for one
/// protocol and hands out system-chosen ports in sequence.
const _holderScript = r'''
import 'dart:io';
import 'dart:math';

Future<void> main() async {
  final random = Random(20261015);
  for (var attempt = 0; attempt < 64; attempt++) {
    final port = 20000 + random.nextInt(40000);
    RawDatagramSocket? udp;
    try {
      udp = await RawDatagramSocket.bind(
        InternetAddress.loopbackIPv4,
        port,
        reuseAddress: false,
      );
      final tcp = await ServerSocket.bind(InternetAddress.loopbackIPv4, port);
      stdout.writeln(port);
      await stdout.flush();
      await stdin.drain<void>();
      udp.close();
      await tcp.close();
      return;
    } on SocketException {
      udp?.close();
    }
  }
  exit(1);
}
''';

/// Port checks bind the loopback interface only.
void main() {
  final loopback = InternetAddress.loopbackIPv4;

  test('reports without binding anything unless asked to', () async {
    final report = await MultiplayerNetworkDiagnostics(bindAddress: loopback)
        .run(
          service: MultiplayerServiceStatus.unconfigured,
          lan: LanBrowseSnapshot.stopped,
        );
    expect(report.service, MultiplayerServiceStatus.unconfigured);
    for (final check in [
      report.gamePortUdp,
      report.gamePortTcp,
      report.lanPort,
    ]) {
      expect(check.result, PortCheckResult.notChecked);
    }
    expect(report.gamePortUdp.port, 27250);
    expect(report.gamePortTcp.protocol, PortProtocol.tcp);
    expect(report.lanPort.port, 27251);
    expect(report.upnp, NetworkCheckState.notAvailable);
    expect(report.turn, NetworkCheckState.skipped);
  });

  test('finds free ports free', () async {
    final report =
        await MultiplayerNetworkDiagnostics(
          bindAddress: loopback,
          gamePort: 0,
          lanPort: 0,
        ).run(
          service: MultiplayerServiceStatus.unconfigured,
          lan: LanBrowseSnapshot.stopped,
          checkPorts: true,
        );
    expect(report.gamePortUdp.result, PortCheckResult.available);
    expect(report.gamePortTcp.result, PortCheckResult.available);
    expect(report.lanPort.result, PortCheckResult.available);
  });

  test(
    'finds a game port another program holds in use, over UDP and TCP',
    () async {
      // Another process holds the port, as the game does while hosting; the
      // VM refuses a second bind inside one process before the system can.
      final folder = Directory.systemTemp.createTempSync('tf-mp-port-');
      final script = File('${folder.path}${Platform.pathSeparator}hold.dart')
        ..writeAsStringSync(_holderScript);
      final holder = await Process.start(Platform.resolvedExecutable, [
        script.path,
      ], workingDirectory: folder.path);
      addTearDown(() async {
        // Closing its input lets the holder release the ports and exit.
        await holder.stdin.close();
        await holder.exitCode.timeout(
          const Duration(seconds: 30),
          onTimeout: () {
            holder.kill();
            return holder.exitCode;
          },
        );
        folder.deleteSync(recursive: true);
      });
      final port = int.parse(
        await holder.stdout
            .transform(utf8.decoder)
            .transform(const LineSplitter())
            .first
            .timeout(const Duration(seconds: 60)),
      );
      final report =
          await MultiplayerNetworkDiagnostics(
            bindAddress: loopback,
            gamePort: port,
          ).run(
            service: MultiplayerServiceStatus.unconfigured,
            lan: LanBrowseSnapshot.stopped,
            checkPorts: true,
          );
      expect(report.gamePortUdp.result, PortCheckResult.inUse);
      expect(report.gamePortTcp.result, PortCheckResult.inUse);
      expect(report.gamePortUdp.port, port);
    },
  );

  test(
    'takes the discovery port as available while the launcher holds it',
    () async {
      final report =
          await MultiplayerNetworkDiagnostics(
            bindAddress: loopback,
            gamePort: 0,
          ).run(
            service: MultiplayerServiceStatus.unconfigured,
            lan: LanBrowseSnapshot(status: LanDiscoveryStatus.listening),
            checkPorts: true,
          );
      expect(report.lanPort.result, PortCheckResult.available);
    },
  );

  test('runs the UPnP and relay seams only where they belong', () async {
    final upnp = _Check(() async => NetworkCheckState.passed);
    final turn = _Check(() async => NetworkCheckState.failed);
    final diagnostics = MultiplayerNetworkDiagnostics(
      bindAddress: loopback,
      upnp: upnp,
      turn: turn,
    );
    final player = await diagnostics.run(
      service: MultiplayerServiceStatus.configured,
      lan: LanBrowseSnapshot.stopped,
    );
    expect(player.upnp, NetworkCheckState.passed);
    expect(player.turn, NetworkCheckState.skipped);
    expect(turn.runs, 0);
    final developer = await diagnostics.run(
      service: MultiplayerServiceStatus.configured,
      lan: LanBrowseSnapshot.stopped,
      developerMode: true,
    );
    expect(developer.turn, NetworkCheckState.failed);
    expect(turn.runs, 1);

    final failing =
        await MultiplayerNetworkDiagnostics(
          bindAddress: loopback,
          upnp: _Check(() async => throw const SocketException('gateway gone')),
        ).run(
          service: MultiplayerServiceStatus.unconfigured,
          lan: LanBrowseSnapshot.stopped,
          developerMode: true,
        );
    expect(failing.upnp, NetworkCheckState.failed);
    expect(failing.turn, NetworkCheckState.notAvailable);
  });

  test(
    'the report\'s JSON holds counts and states, never an address',
    () async {
      final report =
          await MultiplayerNetworkDiagnostics(
            bindAddress: loopback,
            gamePort: 0,
            lanPort: 0,
          ).run(
            service: MultiplayerServiceStatus.unconfigured,
            lan: LanBrowseSnapshot(
              status: LanDiscoveryStatus.listening,
              pending: 2,
              beyondLocalNetwork: 1,
              incompatibleServerIds: const [hostBServerId],
            ),
            checkPorts: true,
          );
      final json = report.toJson();
      expect(json['schema'], MultiplayerNetworkReport.schema);
      expect(json['service'], 'unconfigured');
      expect(json['lanDiscovery'], containsPair('pending', 2));
      expect(json['lanDiscovery'], containsPair('incompatible', 1));
      final text = jsonEncode(json);
      expect(text, isNot(contains(hostBServerId)));
      for (final address in addressTexts) {
        expect(text, isNot(contains(address)));
      }
    },
  );
}
