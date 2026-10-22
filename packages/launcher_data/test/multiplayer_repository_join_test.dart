import 'dart:async';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_data/src/multiplayer/descriptor_client.dart';
import 'package:launcher_data/src/multiplayer/join_descriptor_resolver.dart';
import 'package:launcher_data/src/multiplayer/lan_datagrams.dart';
import 'package:launcher_data/src/multiplayer/lan_discovery_client.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_network_support.dart';

/// The repository answering the domain's join requests, fed back into the
/// domain's own reducer. Every real socket binds the loopback interface.
void main() {
  final loopback = InternetAddress.loopbackIPv4;
  late Directory root;

  setUp(() => root = Directory.systemTemp.createTempSync('tf-mp-join-'));
  tearDown(() => root.deleteSync(recursive: true));

  LocalMultiplayerRepository repository({
    LanDiscoveryClient? lan,
    HostLookup? lookup,
    DescriptorClient? descriptors,
  }) {
    final created = LocalMultiplayerRepository(
      dataRoot: root.path,
      service: MultiplayerServiceConfiguration.none,
      lan:
          lan ??
          LanDiscoveryClient(
            listenAddress: loopback,
            port: 0,
            queryAddress: loopback,
          ),
      descriptors: descriptors,
      lookup:
          lookup ?? (host) async => throw StateError('No lookups expected.'),
      diagnostics: MultiplayerNetworkDiagnostics(bindAddress: loopback),
      clock: () => DateTime.utc(2026, 10, 15, 12),
    );
    addTearDown(created.dispose);
    return created;
  }

  JoinSessionState start(
    JoinTarget target, {
    bool direct = false,
    String? trusted,
  }) => JoinSession.start(
    target,
    privacy: direct
        ? MultiplayerSettings()
              .acknowledgePrivacyDisclosure()
              .privacyForSession(allowDirect: true)
        : SessionPrivacy.relayOnly,
    trustedServerId: trusted,
  );

  Future<LoopbackDescriptorHost> serving(List<int> Function() body) async {
    final host = await LoopbackDescriptorHost.start(
      (socket, _) => LoopbackDescriptorHost.answer(socket, body()),
    );
    addTearDown(host.close);
    return host;
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

  test(
    'a LAN listing is fetched from its beacon\'s sender, end to end',
    () async {
      // The host's UDP game socket and TCP descriptor endpoint share a port
      // number, as the game's do.
      final (game, endpoint) = await bindPortPair();
      addTearDown(game.close);
      final descriptorHost = LoopbackDescriptorHost.on(
        endpoint,
        (socket, _) => LoopbackDescriptorHost.answer(socket, hostADescriptor),
      );
      addTearDown(descriptorHost.close);
      game.listen((event) {
        if (event != RawSocketEvent.read) return;
        final query = game.receive();
        if (query == null || query.data.length != LanInfoQuery.size) return;
        game.send(
          LanInfoReply.encode(
            queryId: query.data.sublist(2, 10),
            info: sessionInfoFixture('typical'),
          ),
          query.address,
          query.port,
        );
      });

      final lan = LanDiscoveryClient(
        listenAddress: loopback,
        port: 0,
        queryAddress: loopback,
      );
      final repo = repository(lan: lan);
      expect(
        (await repo.startLanBrowsing()).status,
        LanDiscoveryStatus.listening,
      );
      final listed = repo.lanSnapshots.firstWhere(
        (snapshot) => snapshot.listings.isNotEmpty,
      );
      game.send(
        LanBeacon(serverId: hostAServerId, gamePort: game.port).encode(),
        loopback,
        lan.listeningPort!,
      );
      final listing = (await listed.timeout(
        const Duration(seconds: 10),
      )).listings.single;

      var state = start(
        JoinListingTarget(serverId: listing.serverId, source: listing.source),
      );
      expect(state.stage, JoinStage.fetchingDescriptor);
      final event = await repo.answerJoin(state);
      expect(event, isA<JoinDescriptorVerified>());
      state = JoinSession.reduce(state, event!);
      expect(state.stage, JoinStage.checking);
      expect(state.serverId, hostAServerId);
      expect(descriptorHost.requests, ['TFD1 descriptor\n']);
    },
  );

  test('a typed address learns its server, then reports a change', () async {
    var served = hostADescriptor;
    final host = await serving(() => served);
    final repo = repository();
    final target = JoinAddressTarget(host: '127.0.0.1', port: host.port);

    var state = start(target);
    var event = await repo.answerJoin(state);
    state = JoinSession.reduce(state, event!);
    expect(state.stage, JoinStage.checking);
    expect(state.serverId, hostAServerId);

    // The same address now serves host B.
    served = hostBDescriptor;
    state = start(target);
    event = await repo.answerJoin(state);
    expect((event! as JoinFailed).code, JoinFailureCode.identityChanged);
    state = JoinSession.reduce(state, event);
    expect(state.outcome!.failure!.code, JoinFailureCode.identityChanged);
    expect(state.outcome!.failure!.servedServerId, hostBServerId);

    // The player trusts B: accepted, and remembered in place of A.
    state = start(target, trusted: hostBServerId);
    event = await repo.answerJoin(state);
    expect(JoinSession.reduce(state, event!).serverId, hostBServerId);
    expect(await repo.answerJoin(start(target)), isA<JoinDescriptorVerified>());

    // While B is trusted, A answering there is a change naming A.
    served = hostADescriptor;
    state = start(target, trusted: hostBServerId);
    final changed = await repo.answerJoin(state) as JoinFailed;
    expect(changed.code, JoinFailureCode.identityChanged);
    expect(changed.servedServerId, hostAServerId);
    expect(
      JoinSession.reduce(state, changed).outcome!.failure!.servedServerId,
      hostAServerId,
    );
  });

  test(
    'a direct link pins its server: another is an invalid descriptor',
    () async {
      final host = await serving(() => hostBDescriptor);
      final repo = repository();
      final state = start(
        JoinLinkTarget(
          SessionLink.direct(
            serverId: hostAServerId,
            host: '127.0.0.1',
            port: host.port,
          ),
        ),
      );
      final event = await repo.answerJoin(state) as JoinFailed;
      expect(event.code, JoinFailureCode.descriptorInvalid);
      final ended = JoinSession.reduce(state, event);
      expect(ended.outcome!.failure!.code, JoinFailureCode.descriptorInvalid);
    },
  );

  test('a host that is not there is unreachable, naming no address', () async {
    final repo = repository(
      descriptors: DescriptorClient(
        connect: (address, port) async => throw SocketException(
          'Connection refused',
          osError: const OSError('refused', 10061),
          address: address,
          port: port,
        ),
      ),
    );
    final event =
        await repo.answerJoin(
              start(JoinAddressTarget(host: '127.0.0.1', port: 27250)),
            )
            as JoinFailed;
    expect(event.code, JoinFailureCode.unreachable);
    for (final address in addressTexts) {
      expect(event.detail, isNot(contains(address)));
    }
  });

  group('relay-only contact rules', () {
    test('a local name resolving beyond the local network is refused before '
        'any connection', () async {
      final lookups = <String>[];
      final repo = repository(
        lookup: (host) async {
          lookups.add(host);
          return [publicHost, publicHostV6];
        },
      );
      final state = start(JoinAddressTarget(host: 'game.local', port: 27250));
      expect(state.stage, JoinStage.fetchingDescriptor);
      final (event, attempts) = await noConnections(
        () => repo.answerJoin(state),
      );
      final failed = event! as JoinFailed;
      expect(failed.code, JoinFailureCode.directNotAllowed);
      // The rules filtered the addresses themselves, before the descriptor
      // client's last-line check would.
      expect(failed.detail, contains('Allow a direct path for this session'));
      expect(lookups, ['game.local']);
      expect(attempts, 0);
      for (final address in addressTexts) {
        expect(failed.detail, isNot(contains(address)));
      }
      expect(
        JoinSession.reduce(state, failed).outcome!.failure!.code,
        JoinFailureCode.directNotAllowed,
      );
    });

    test('a direct link to such a name is refused the same way', () async {
      final repo = repository(lookup: (host) async => [publicHost]);
      final state = start(
        JoinLinkTarget(
          SessionLink.direct(
            serverId: hostAServerId,
            host: 'game.localhost',
            port: 27250,
          ),
        ),
      );
      final (event, attempts) = await noConnections(
        () => repo.answerJoin(state),
      );
      expect((event! as JoinFailed).code, JoinFailureCode.directNotAllowed);
      expect(attempts, 0);
    });

    test('only the local addresses a name resolves to are contacted, IPv4 '
        'first', () async {
      final connects = <InternetAddress>[];
      final linkLocal = InternetAddress('fe80::1');
      final repo = repository(
        lookup: (host) async => [publicHost, linkLocal, lanHost, publicHostV6],
        descriptors: DescriptorClient(
          connect: (address, _) async {
            connects.add(address);
            throw const SocketException('refused');
          },
        ),
      );
      final event = await repo.answerJoin(
        start(JoinAddressTarget(host: 'game.local', port: 27250)),
      );
      expect((event! as JoinFailed).code, JoinFailureCode.unreachable);
      expect(connects, [lanHost, linkLocal]);
    });

    test('a direct session contacts every address, IPv4 first', () async {
      final connects = <InternetAddress>[];
      final linkLocal = InternetAddress('fe80::1');
      final repo = repository(
        lookup: (host) async => [publicHostV6, publicHost, linkLocal, lanHost],
        descriptors: DescriptorClient(
          connect: (address, _) async {
            connects.add(address);
            throw const SocketException('refused');
          },
        ),
      );
      await repo.answerJoin(
        start(
          JoinAddressTarget(host: 'play.example.com', port: 27250),
          direct: true,
        ),
      );
      expect(connects, [publicHost, lanHost, publicHostV6, linkLocal]);
    });

    test('the domain itself ends joins that relay-only cannot make', () {
      for (final target in [
        JoinAddressTarget(host: '203.0.113.7', port: 27250),
        JoinAddressTarget(host: 'play.example.com', port: 27250),
      ]) {
        final state = start(target);
        expect(state.descriptorRequest, isNull);
        expect(state.outcome!.failure!.code, JoinFailureCode.directNotAllowed);
      }
    });

    test(
      'a name that cannot be found, or has no address, names nothing',
      () async {
        for (final lookup in <HostLookup>[
          (host) async => throw SocketException('Failed host lookup: $host'),
          (host) async => const [],
        ]) {
          final repo = repository(lookup: lookup);
          final event =
              await repo.answerJoin(
                    start(JoinAddressTarget(host: 'game.local', port: 27250)),
                  )
                  as JoinFailed;
          expect(event.code, JoinFailureCode.unreachable);
          expect(event.detail, isNot(contains('game.local')));
        }
      },
    );
  });

  group('without a configured service', () {
    test(
      'invites, server links and Internet listings contact nothing',
      () async {
        var clients = 0;
        final repo = repository();
        final (_, attempts) = await noConnections(
          () => HttpOverrides.runZoned(
            () async {
              for (final target in [
                JoinLinkTarget(SessionLink.invite('ABCDE12345')),
                JoinLinkTarget(SessionLink.server(hostAServerId)),
                JoinListingTarget(
                  serverId: hostAServerId,
                  source: ServerListingSource.internet,
                ),
              ]) {
                final state = start(target);
                expect(state.stage, JoinStage.resolving);
                final event = await repo.answerJoin(state) as JoinFailed;
                expect(event.code, JoinFailureCode.serviceUnavailable);
                final failure = JoinSession.reduce(
                  state,
                  event,
                ).outcome!.failure!;
                expect(failure.code, JoinFailureCode.serviceUnavailable);
                expect(failure.stage, JoinStage.resolving);
                expect(
                  failure.detail,
                  contains('LAN and direct play still work'),
                );
              }
              // A target the service resolved still gets its descriptor from
              // the service, never from the host.
              final resolved = JoinSession.reduce(
                start(JoinLinkTarget(SessionLink.server(hostAServerId))),
                JoinTargetResolved(hostAServerId),
              );
              expect(resolved.stage, JoinStage.fetchingDescriptor);
              final event = await repo.answerJoin(resolved) as JoinFailed;
              expect(event.code, JoinFailureCode.serviceUnavailable);
              final browse = await repo.browseInternet(
                ServerBrowseFilter(gameBuild: '2545', protocolMajor: 1),
              );
              expect(browse.isAvailable, isFalse);
              expect(browse.listings, isEmpty);
              return null;
            },
            createHttpClient: (_) {
              clients++;
              throw StateError('No HTTP client may be created.');
            },
          ),
        );
        expect(attempts, 0);
        expect(clients, 0);
        expect(repo.serviceStatus, MultiplayerServiceStatus.unconfigured);
      },
    );
  });

  test('stages these clients do not answer get no event', () async {
    final host = await serving(() => hostADescriptor);
    final repo = repository();
    final target = JoinAddressTarget(host: '127.0.0.1', port: host.port);
    final checking = JoinSession.reduce(
      start(target),
      (await repo.answerJoin(start(target)))!,
    );
    expect(checking.stage, JoinStage.checking);
    expect(await repo.answerJoin(checking), isNull);
    expect(
      () => repo.resolveTarget(target),
      throwsArgumentError,
      reason: 'a typed address is never resolved through the service',
    );
  });

  test('a cancelled fetch gives no event and contacts nothing', () async {
    final repo = repository();
    final cancellation = CancellationToken()..cancel();
    final (event, attempts) = await noConnections(
      () => repo.answerJoin(
        start(JoinAddressTarget(host: '127.0.0.1', port: 27250)),
        cancellation: cancellation,
      ),
    );
    expect(event, isNull);
    expect(attempts, 0);
  });
}
