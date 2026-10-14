import 'dart:typed_data';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_fixtures.dart';
import 'multiplayer_join_support.dart';

/// The session's privacy is chosen before the join starts and governs the
/// first contact: under relay-only nothing beyond the local network is
/// contacted, not even for the descriptor.
void main() {
  JoinLinkTarget direct(String host) => JoinLinkTarget(
    SessionLink.direct(serverId: hostAServerId, host: host, port: 27250),
  );

  group('addresses', () {
    test('are classified as the game classifies its paths', () {
      const kinds = {
        '10.0.0.7': NetworkPathKind.lan,
        '127.0.0.1': NetworkPathKind.lan,
        '172.16.0.1': NetworkPathKind.lan,
        '172.31.255.255': NetworkPathKind.lan,
        '192.168.1.20': NetworkPathKind.lan,
        '169.254.10.1': NetworkPathKind.lan,
        '::1': NetworkPathKind.lan,
        'fe80::1': NetworkPathKind.lan,
        'febf::1': NetworkPathKind.lan,
        'fc00::1': NetworkPathKind.lan,
        'fd12:3456::1': NetworkPathKind.lan,
        '::ffff:10.0.0.7': NetworkPathKind.lan,
        '172.15.255.255': NetworkPathKind.direct,
        '172.32.0.1': NetworkPathKind.direct,
        '100.64.0.1': NetworkPathKind.direct,
        '100.127.255.254': NetworkPathKind.direct,
        '8.8.8.8': NetworkPathKind.direct,
        '0.0.0.0': NetworkPathKind.direct,
        '255.255.255.255': NetworkPathKind.direct,
        'fec0::1': NetworkPathKind.direct,
        '2001:db8::1': NetworkPathKind.direct,
        '::': NetworkPathKind.direct,
        '::2': NetworkPathKind.direct,
        '::ffff:8.8.8.8': NetworkPathKind.direct,
      };
      kinds.forEach((host, kind) {
        expect(NetworkHost.pathKindOf(host), kind, reason: host);
      });
      expect(NetworkHost.pathKindOf('play.example.org'), isNull);
      expect(NetworkHost.pathKindOf('localhost'), isNull);
    });

    test('raw addresses classify the same way', () {
      expect(
        NetworkHost.pathKindOfAddress(const [192, 168, 1, 20]),
        NetworkPathKind.lan,
      );
      expect(
        NetworkHost.pathKindOfAddress(const [100, 64, 0, 1]),
        NetworkPathKind.direct,
      );
      final mapped = Uint8List(16)
        ..[10] = 0xFF
        ..[11] = 0xFF
        ..setRange(12, 16, const [10, 0, 0, 7]);
      expect(NetworkHost.pathKindOfAddress(mapped), NetworkPathKind.lan);
      final loopback = Uint8List(16)..[15] = 1;
      expect(NetworkHost.pathKindOfAddress(loopback), NetworkPathKind.lan);
      expect(
        () => NetworkHost.pathKindOfAddress(const [1, 2, 3]),
        throwsArgumentError,
      );
      expect(
        () => NetworkHost.pathKindOfAddress(const [192, 168, 1, 256]),
        throwsArgumentError,
      );
    });

    test('refusals never repeat the host', () {
      for (final call in <void Function()>[
        () => NetworkHost.pathKindOf('bad_host.192.0.2.7'),
        () => SessionLink.direct(
          serverId: hostAServerId,
          host: 'bad_host.192.0.2.7',
          port: 1,
        ),
        () => SessionLink.invite('192.0.2.7'),
        () => SessionLink.server('192.0.2.7'),
        () => SessionLink.direct(
          serverId: '192.0.2.7',
          host: 'example.org',
          port: 1,
        ),
        () => InviteCode.display('192.0.2.7'),
        () => JoinAddressTarget(host: 'bad_host.192.0.2.7', port: 1),
        () => JoinListingTarget(
          serverId: '192.0.2.7',
          source: ServerListingSource.lan,
        ),
        () => JoinTargetResolved('192.0.2.7'),
        () => KnownHosts.targetKey('bad_host.192.0.2.7', 1),
      ]) {
        try {
          call();
          fail('expected a refusal');
        } on ArgumentError catch (error) {
          expect(error.toString(), isNot(contains('192.0.2.7')));
        }
      }
    });
  });

  group('starting a join', () {
    test('relay-only refuses a host beyond the local network at once', () {
      for (final host in ['8.8.8.8', '100.64.0.1', '2001:db8::1']) {
        for (final target in [
          direct(host),
          JoinAddressTarget(host: host, port: 27250),
        ]) {
          final state = startJoin(target);
          expect(state.isEnded, isTrue, reason: host);
          final failure = state.outcome!.failure!;
          expect(failure.code, JoinFailureCode.directNotAllowed);
          expect(failure.stage, JoinStage.fetchingDescriptor);
          expect(failure.detail, isNot(contains(host)));
          expect(state.descriptorRequest, isNull);
        }
      }
    });

    test('allowing direct for the session lets the fetch go ahead', () {
      final state = startJoin(
        direct('8.8.8.8'),
        privacy: MultiplayerAddressPrivacy.direct,
      );
      expect(state.stage, JoinStage.fetchingDescriptor);
      expect(state.descriptorRequest!.allowsDirect, isTrue);
    });

    test('relay-only fetches from local hosts and local names only', () {
      for (final host in [
        '192.168.1.20',
        '::1',
        'fd00::7',
        'localhost',
        'printer.local',
      ]) {
        final state = startJoin(direct(host));
        expect(state.stage, JoinStage.fetchingDescriptor, reason: host);
        final request = state.descriptorRequest!;
        expect(request.privacy, MultiplayerAddressPrivacy.relayOnly);
        expect(request.allowsDirect, isFalse);
        expect(request.expectedServerId, hostAServerId);
        expect(request.target, same(state.target));
        expect(request.toString(), isNot(contains(host)));
      }
    });

    test('relay-only looks up no other name, before any lookup', () {
      for (final host in [
        'play.example.org',
        'host.3com',
        'local',
        'local.example',
        'localhost.example',
      ]) {
        for (final target in [
          direct(host),
          JoinAddressTarget(host: host, port: 27250),
        ]) {
          final state = startJoin(target);
          final failure = state.outcome!.failure!;
          expect(failure.code, JoinFailureCode.directNotAllowed, reason: host);
          expect(failure.stage, JoinStage.fetchingDescriptor);
          expect(failure.detail, isNot(contains(host)));
          final open = startJoin(
            target,
            privacy: MultiplayerAddressPrivacy.direct,
          );
          expect(open.stage, JoinStage.fetchingDescriptor, reason: host);
        }
      }
      expect(NetworkHost.isLocalName('Printer.LOCAL'), isTrue);
      expect(NetworkHost.isLocalName('127.0.0.1'), isFalse);
    });

    test('only control-plane targets resolve first', () {
      final resolving = [
        JoinLinkTarget(SessionLink.invite('ABCDE12345')),
        JoinLinkTarget(SessionLink.server(hostAServerId)),
        JoinListingTarget(
          serverId: hostAServerId,
          source: ServerListingSource.internet,
        ),
      ];
      for (final target in resolving) {
        final state = startJoin(target);
        expect(state.stage, JoinStage.resolving, reason: '$target');
        expect(state.descriptorRequest, isNull);
      }
      final lan = startJoin(
        JoinListingTarget(
          serverId: hostAServerId,
          source: ServerListingSource.lan,
        ),
      );
      expect(lan.stage, JoinStage.fetchingDescriptor);
      expect(lan.descriptorRequest!.expectedServerId, hostAServerId);
      expect(lan.target.directHost, isNull);
    });

    test('the request exists only while fetching, and a re-plan keeps its '
        'privacy', () async {
      final server = await verifiedForTest(testDescriptor());
      final plan = const JoinPlanner().plan(
        server: server,
        resolutions: const {},
        environment: testEnvironment,
        mayContactHost: true,
      );
      var state = startJoin(direct('192.168.1.20'));
      state = JoinSession.reduce(state, JoinDescriptorVerified(server));
      expect(state.descriptorRequest, isNull);
      state = JoinSession.reduce(state, JoinPlanReady(plan));
      state = [
        const JoinConsentGiven(),
        const JoinProfilePrepared(),
        const JoinGameStarted(),
        JoinFailed(JoinFailureCode.admissionChanged),
      ].fold(state, JoinSession.reduce);
      expect(state.stage, JoinStage.fetchingDescriptor);
      expect(
        state.descriptorRequest!.privacy,
        MultiplayerAddressPrivacy.relayOnly,
      );
      expect(state.descriptorRequest!.expectedServerId, hostAServerId);
    });
  });

  group('resolved names', () {
    final request = startJoin(direct('printer.local')).descriptorRequest!;
    final openRequest = startJoin(
      direct('play.example.org'),
      privacy: MultiplayerAddressPrivacy.direct,
    ).descriptorRequest!;
    final v6Local = Uint8List(16)
      ..[0] = 0xFE
      ..[1] = 0x80
      ..[15] = 1;
    final v6Public = Uint8List(16)
      ..[0] = 0x20
      ..[1] = 0x01
      ..[15] = 1;
    final resolved = <List<int>>[
      v6Public,
      const [8, 8, 8, 8],
      v6Local,
      const [192, 168, 1, 20],
      const [100, 64, 0, 1],
      const [10, 0, 0, 7],
    ];

    test('under relay-only keep only local addresses, IPv4 first', () {
      expect(request.contactableAddresses(resolved), [
        [192, 168, 1, 20],
        [10, 0, 0, 7],
        v6Local,
      ]);
      expect(
        request.contactableAddresses(const [
          [8, 8, 8, 8],
          [100, 64, 0, 1],
        ]),
        isEmpty,
        reason: 'which the data layer reports as direct not allowed',
      );
    });

    test('with direct allowed keep every address, IPv4 first', () {
      expect(openRequest.contactableAddresses(resolved), [
        [8, 8, 8, 8],
        [192, 168, 1, 20],
        [100, 64, 0, 1],
        [10, 0, 0, 7],
        v6Public,
        v6Local,
      ]);
    });

    test('drop anything that is not an address', () {
      expect(
        openRequest.contactableAddresses(const [
          [1, 2, 3],
          [1, 2, 3, 400],
          [192, 168, 1, 20],
        ]),
        [
          [192, 168, 1, 20],
        ],
      );
    });
  });
}
