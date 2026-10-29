/// A host that serves its own packages, on the loopback interface, and a
/// join of it through the launcher's real data layer: for the tests of
/// host-served downloads.
library;

import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:math';
import 'dart:typed_data';

import 'package:cryptography/cryptography.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_data/src/multiplayer/host_package_client.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'multiplayer_join_content_fixtures.dart';
import 'multiplayer_join_rig.dart';
import 'multiplayer_lan_rig.dart';
import 'multiplayer_network_support.dart';

/// A descriptor of host A's server, signed with host A's key from the shared
/// fixtures (`tests/fixtures/multiplayer/descriptor/cases.json`), so the
/// launcher verifies it as it verifies any host's.
Future<Uint8List> signedByHostA(
  List<DescriptorPackage> packages, {
  bool hostServesPackages = true,
}) async {
  final fixture = readMultiplayerFixture('descriptor/cases.json');
  final key = asObjects(
    fixture['keys'],
  ).singleWhere((item) => item['id'] == 'host-a');
  final algorithm = Ed25519();
  final pair = await algorithm.newKeyPairFromSeed(
    hexBytes(key['signingSeed']! as String),
  );
  final descriptor = ServerDescriptorV1(
    signingKey: WireText.tryParseBase64Url(testSigningKey, 32)!,
    transportKey: WireText.tryParseBase64Url(testTransportKey, 32)!,
    issuedAt: 1791849600,
    name: "Sam's Workshop",
    gameBuild: '2545',
    runtimeVersion: '0.2.0',
    protocolVersion: TopiaForgeRuntimeVersions.multiplayerProtocolVersion,
    maxPlayers: 8,
    passwordRequired: false,
    voice: false,
    visibility: ServerVisibility.lan,
    hostServesPackages: hostServesPackages,
    gamemode: null,
    world: null,
    packages: [...packages]..sort((a, b) => a.id.compareTo(b.id)),
  );
  final unsigned = SignedServerDescriptorV1.assemble(
    descriptor,
    Uint8List(SignedServerDescriptorV1.signatureBytes),
  );
  final signature = await algorithm.sign(unsigned.signedMessage, keyPair: pair);
  return SignedServerDescriptorV1.assemble(descriptor, signature.bytes).bytes;
}

/// What a test host does with one package request: null answers as the
/// game does.
typedef PackageAnswer =
    FutureOr<void> Function(Socket socket, TestArchive? archive);

/// A host's descriptor endpoint on loopback, answering as the game's
/// `DescriptorEndpoint` writes: the signed descriptor for `TFD1 descriptor`,
/// and for `TFD1 package <sha256>` the archive it holds, in 64 KiB writes, or
/// `TFD1 404`.
final class ServingHost {
  ServingHost._(this._endpoint, this.descriptor, this._archives);

  /// A host serving [serves], whose descriptor lists [lists], or what it
  /// serves.
  static Future<ServingHost> start(
    List<TestArchive> serves, {
    List<DescriptorPackage>? lists,
  }) async {
    final descriptor = await signedByHostA(
      lists ?? [for (final archive in serves) archive.package],
    );
    late final ServingHost host;
    final endpoint = await LoopbackDescriptorHost.start(
      (socket, request) => host._answer(socket, request),
    );
    host = ServingHost._(endpoint, descriptor, {
      for (final archive in serves) archive.sha256Hex: archive,
    });
    addTearDown(endpoint.close);
    return host;
  }

  final LoopbackDescriptorHost _endpoint;
  final Uint8List descriptor;
  final Map<String, TestArchive> _archives;

  /// Overrides how package requests are answered.
  PackageAnswer? answerPackages;

  int get port => _endpoint.port;
  List<String> get requests => _endpoint.requests;
  Stream<void> get disconnected => _endpoint.disconnected;
  int get packageRequests =>
      requests.where((line) => line.startsWith('TFD1 package ')).length;

  Future<void> _answer(Socket socket, String request) async {
    if (request == 'TFD1 descriptor\n') {
      return LoopbackDescriptorHost.answer(socket, descriptor);
    }
    const prefix = 'TFD1 package ';
    final sha = request.startsWith(prefix)
        ? request.substring(prefix.length).trim()
        : '';
    final archive = _archives[sha];
    final custom = answerPackages;
    if (custom != null) return custom(socket, archive);
    if (archive == null) {
      return LoopbackDescriptorHost.raw(socket, ascii.encode('TFD1 404\n'));
    }
    await serveArchive(socket, archive.bytes);
  }

  /// Writes [body] as the game does: the status line, the bytes in 64 KiB
  /// writes, then the end of the stream.
  static Future<void> serveArchive(Socket socket, List<int> body) async {
    socket.add(ascii.encode('TFD1 200 ${body.length}\n'));
    for (var at = 0; at < body.length; at += 65536) {
      socket.add(body.sublist(at, min(body.length, at + 65536)));
      await socket.flush();
    }
    await socket.close();
  }
}

/// A join of a [ServingHost] through the launcher's real data layer: the
/// launcher repository over a test game, and the multiplayer facade with a
/// LAN client over fake UDP sockets, whose beacons point at the host's real
/// TCP endpoint on loopback.
final class HostJoin {
  HostJoin._(this.rig, this.lan, this.multiplayer, this.host);

  static Future<HostJoin> create(
    ServingHost host, {
    bool developerMode = true,
    HostPackageClient? packages,
  }) async {
    final rig = await JoinRig.create();
    if (developerMode) await rig.repository.setDeveloperMode(true);
    final lan = LanRig(packages: packages);
    final multiplayer = LocalMultiplayerRepository(
      dataRoot: rig.dataRoot,
      service: MultiplayerServiceConfiguration.none,
      lan: lan.client,
      lookup: (_) async => throw StateError('No lookups expected.'),
      diagnostics: MultiplayerNetworkDiagnostics(
        bindAddress: InternetAddress.loopbackIPv4,
        gamePort: 0,
        lanPort: 0,
      ),
      content: rig.repository,
    );
    addTearDown(multiplayer.dispose);
    return HostJoin._(rig, lan, multiplayer, host);
  }

  final JoinRig rig;
  final LanRig lan;
  final LocalMultiplayerRepository multiplayer;
  final ServingHost host;

  String get serverId => hostAServerId;

  JoinListingTarget get listing =>
      JoinListingTarget(serverId: serverId, source: ServerListingSource.lan);

  /// Starts browsing and hears the host's beacon from loopback, at the port
  /// its endpoint listens on.
  Future<void> hearHost() async {
    await multiplayer.startLanBrowsing();
    beacon();
  }

  /// A beacon of the host, from [from], to the listener that is open now.
  void beacon({InternetAddress? from}) {
    final listener = lan.binder.sockets[lan.binder.sockets.length - 2];
    listener.deliver(
      LanBeacon(serverId: serverId, gamePort: host.port).encode(),
      from ?? InternetAddress.loopbackIPv4,
      40000,
    );
  }

  /// A join of [target] fetched, planned and consented to, now downloading.
  /// Every package the host lists comes from the host unless [resolutions]
  /// says otherwise; the descriptor comes through [multiplayer] itself.
  Future<JoinSessionState> downloading({
    JoinTarget? target,
    SessionPrivacy privacy = SessionPrivacy.relayOnly,
    Map<String, JoinPackageResolution> resolutions = const {},
  }) async {
    var state = JoinSession.start(target ?? listing, privacy: privacy);
    final event = await multiplayer.answerJoin(state);
    state = JoinSession.reduce(state, event!);
    expect(state.stage, JoinStage.checking, reason: '$event');
    final server = state.descriptor!;
    final plan = const JoinPlanner().plan(
      server: server,
      resolutions: {
        for (final package in server.descriptor.packages)
          package.sha256:
              resolutions[package.sha256] ??
              JoinPackageResolution.hostOffered(),
      },
      environment: JoinEnvironment(
        gameBuild: '2545',
        runtimeVersion: '0.2.0',
        sdkVersion: '0.2.0',
        protocolVersion: TopiaForgeRuntimeVersions.multiplayerProtocolVersion,
        platform: 'windows',
        architecture: 'x64',
        developerMode: true,
      ),
      mayContactHost: state.mayContactHost,
    );
    state = [
      JoinPlanReady(plan),
      JoinConsentGiven(hostServedAcknowledged: plan.hasUnverifiedPackages),
    ].fold(state, JoinSession.reduce);
    expect(state.stage, JoinStage.downloading);
    return state;
  }

  /// Answers [state] with the facade and reduces what comes back, progress
  /// included, until the join ends or reaches [until].
  Future<JoinSessionState> drive(
    JoinSessionState state, {
    JoinStage until = JoinStage.launching,
    CancellationToken? cancellation,
    List<JoinDownloadProgressed>? progress,
  }) async {
    var current = state;
    while (current.stage != until && !current.isEnded) {
      final event = await multiplayer.answerJoin(
        current,
        install: rig.install,
        cancellation: cancellation,
        onProgress: (item) {
          if (item is JoinDownloadProgressed) progress?.add(item);
          current = JoinSession.reduce(current, item);
        },
      );
      if (event == null) return current;
      current = JoinSession.reduce(current, event);
    }
    return current;
  }

  /// The names of what the package cache holds.
  List<String> cacheFiles() {
    final cache = Directory(p.join(rig.dataRoot, 'package-cache'));
    if (!cache.existsSync()) return const [];
    return [for (final entity in cache.listSync()) p.basename(entity.path)]
      ..sort();
  }

  /// The launcher's own log so far.
  String launcherLog() {
    final log = File(p.join(rig.dataRoot, 'logs', 'launcher.log'));
    return log.existsSync() ? log.readAsStringSync() : '';
  }
}

/// Runs [body] where opening any TCP connection is counted and refused.
Future<(T, int)> noConnections<T>(Future<T> Function() body) async {
  var attempts = 0;
  final result = await IOOverrides.runZoned(
    body,
    socketStartConnect: (host, port, {sourceAddress, sourcePort = 0}) {
      attempts++;
      throw const SocketException('No connection may open.');
    },
    socketConnect: (host, port, {sourceAddress, sourcePort = 0, timeout}) {
      attempts++;
      throw const SocketException('No connection may open.');
    },
  );
  return (result, attempts);
}
