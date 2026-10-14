import 'dart:convert';
import 'dart:io';
import 'dart:math';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_data/src/multiplayer/descriptor_client.dart';
import 'package:launcher_data/src/multiplayer/lan_datagrams.dart';
import 'package:launcher_data/src/multiplayer/lan_discovery_client.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_lan_rig.dart';
import 'multiplayer_network_support.dart';

/// LAN discovery's bounds, rate limits, failures and descriptor fetches.
void main() {
  final typical = sessionInfoFixture('typical');

  test('keeps at most 64 sessions', () async {
    final rig = LanRig.fresh();
    await rig.client.start();
    for (var index = 0; index < 70; index++) {
      rig.beacon(serverIdOf(index), InternetAddress('10.0.${index ~/ 8}.1'));
    }
    final snapshot = rig.client.snapshot;
    expect(snapshot.pending, LanDiscoveryClient.maximumServers);
    expect(snapshot.counters.serversDropped, 6);
  });

  test('limits queries per host and overall', () async {
    final rig = LanRig.fresh();
    await rig.client.start();
    for (var index = 0; index < 20; index++) {
      rig.beacon(serverIdOf(index), lanHost);
    }
    expect(
      rig.querier.sent,
      hasLength(LanDiscoveryClient.maximumQueriesPerHost),
    );
    for (var index = 20; index < 64; index++) {
      rig.beacon(serverIdOf(index), InternetAddress('10.1.0.$index'));
    }
    expect(
      rig.querier.sent,
      hasLength(LanDiscoveryClient.maximumQueriesPerSecond),
    );
    expect(rig.client.snapshot.counters.queriesThrottled, 64 - 32);
  });

  test('keeps at most 64 queries outstanding, forgetting the oldest', () async {
    final rig = LanRig.fresh();
    await rig.client.start();
    // Two rounds fill the list; the third asks the first round again, with
    // all 64 earlier queries still within their lifetime.
    for (var round = 0; round < 3; round++) {
      for (var index = 0; index < 32; index++) {
        final server = round == 2 ? index : round * 32 + index;
        rig.beacon(
          serverIdOf(server),
          InternetAddress('10.2.${round % 2}.$index'),
        );
      }
      rig.clock.advance(const Duration(seconds: 1));
    }
    expect(rig.client.snapshot.counters.queriesSent, 96);
    expect(rig.client.snapshot.counters.serversDropped, 0);
    final first = rig.querier.sent.first;
    rig.querier.deliver(
      LanInfoReply.encode(queryId: first.data.sublist(2, 10), info: typical),
      first.address,
      first.port,
    );
    expect(rig.client.snapshot.counters.repliesRefused, 1);
    final newest = rig.querier.sent.last;
    rig.querier.deliver(
      LanInfoReply.encode(queryId: newest.data.sublist(2, 10), info: typical),
      newest.address,
      newest.port,
    );
    expect(rig.client.snapshot.counters.repliesAccepted, 1);
  });

  test('never throws on hostile datagrams, and keeps working', () async {
    final rig = LanRig.fresh();
    await rig.client.start();
    final random = Random(20261015);
    for (var round = 0; round < 2000; round++) {
      final length = random.nextInt(1400);
      final data = List.generate(length, (_) => random.nextInt(256));
      if (length > 0 && round.isEven) data[0] = 0xE6;
      if (length > 0 && round % 3 == 0) data[0] = LanInfoReply.packetType;
      final from = InternetAddress('192.168.7.${random.nextInt(250) + 1}');
      rig.listener.deliver(data, from, random.nextInt(65535) + 1);
      rig.querier.deliver(data, from, random.nextInt(65535) + 1);
    }
    expect(rig.client.snapshot.counters.faults, 0);
    rig.clock.advance(const Duration(seconds: 10));
    rig.ticker.fire();
    rig.beacon(hostAServerId, lanHost);
    rig.answer(typical);
    expect(rig.client.snapshot.listings.single.serverId, hostAServerId);
  });

  test(
    'a discovery port that cannot be opened is reported, not thrown',
    () async {
      for (final (code, problem) in [
        (10048, LanDiscoveryProblem.portInUse),
        (98, LanDiscoveryProblem.portInUse),
        (10013, LanDiscoveryProblem.permissionDenied),
        (1, LanDiscoveryProblem.failed),
      ]) {
        final rig = LanRig.fresh();
        rig.binder.failWith = SocketException(
          'bind failed',
          osError: OSError('denied', code),
          address: lanHost,
          port: 27251,
        );
        final snapshot = await rig.client.start();
        expect(snapshot.status, LanDiscoveryStatus.unavailable);
        expect(snapshot.problem, problem);
        expect(snapshot.message, isNot(contains('192.168')));
        expect(rig.client.isListening, isFalse);
        rig.binder.failWith = null;
        expect((await rig.client.start()).status, LanDiscoveryStatus.listening);
      }
    },
  );

  test('stopping forgets every session and closes both sockets', () async {
    final rig = LanRig.fresh();
    await rig.client.start();
    rig.beacon(hostAServerId, lanHost);
    rig.answer(typical);
    final stopped = await rig.client.stop();
    expect(stopped.status, LanDiscoveryStatus.stopped);
    expect(stopped.listings, isEmpty);
    expect(rig.listener.closed && rig.querier.closed, isTrue);
    expect(
      (await rig.client.stop(disabled: true)).status,
      LanDiscoveryStatus.disabled,
    );
  });

  test('nothing it publishes carries an address', () async {
    final rig = LanRig.fresh();
    await rig.client.start();
    rig.beacon(hostAServerId, lanHost);
    rig.answer(typical);
    rig.beacon(hostBServerId, publicHost);
    final snapshot = rig.client.snapshot;
    final text = [
      jsonEncode(snapshot.toJson()),
      snapshot.toString(),
      for (final listing in snapshot.listings) ...[
        listing.toString(),
        listing.serverId,
        listing.name,
      ],
    ].join('\n');
    for (final address in addressTexts) {
      expect(text, isNot(contains(address)));
    }
  });

  group('fetching a listing\'s descriptor', () {
    JoinDescriptorRequest lanRequest(
      String serverId, {
      MultiplayerAddressPrivacy privacy = MultiplayerAddressPrivacy.relayOnly,
    }) => JoinSession.start(
      JoinListingTarget(serverId: serverId, source: ServerListingSource.lan),
      privacy: privacy == MultiplayerAddressPrivacy.direct
          ? MultiplayerSettings()
                .acknowledgePrivacyDisclosure()
                .privacyForSession(allowDirect: true)
          : SessionPrivacy.relayOnly,
    ).descriptorRequest!;

    test('relay-only refuses a sender beyond the local network before any '
        'connection', () async {
      final connects = <(InternetAddress, int)>[];
      final rig = LanRig.fresh(
        descriptors: DescriptorClient(
          connect: (address, port) async {
            connects.add((address, port));
            throw const SocketException('refused');
          },
        ),
      );
      await rig.client.start();
      rig.beacon(hostAServerId, lanHost);
      rig.answer(typical);
      rig.beacon(hostAServerId, publicHost);
      final fetch =
          await rig.client.fetchDescriptor(lanRequest(hostAServerId))
              as DescriptorFetchFailed;
      expect(fetch.failure, DescriptorFetchFailure.directNotAllowed);
      // The LAN client refuses on its own, before the descriptor client's
      // last-line check would.
      expect(fetch.message, contains('announced from beyond your local'));
      expect(connects, isEmpty);
      for (final address in addressTexts) {
        expect(fetch.message, isNot(contains(address)));
      }
    });

    test('a session allowed direct contact may reach it', () async {
      final connects = <(InternetAddress, int)>[];
      final rig = LanRig.fresh(
        descriptors: DescriptorClient(
          connect: (address, port) async {
            connects.add((address, port));
            throw const SocketException('refused');
          },
        ),
      );
      await rig.client.start();
      rig.beacon(hostAServerId, publicHost, gamePort: 27300);
      final fetch =
          await rig.client.fetchDescriptor(
                lanRequest(
                  hostAServerId,
                  privacy: MultiplayerAddressPrivacy.direct,
                ),
              )
              as DescriptorFetchFailed;
      expect(fetch.failure, DescriptorFetchFailure.unreachable);
      expect(connects, [(publicHost, 27300)]);
    });

    test('a server no longer heard is reported as gone', () async {
      final rig = LanRig.fresh();
      await rig.client.start();
      final fetch =
          await rig.client.fetchDescriptor(lanRequest(hostAServerId))
              as DescriptorFetchFailed;
      expect(fetch.failure, DescriptorFetchFailure.unreachable);
      expect(fetch.message, contains('no longer on the local network'));
    });
  });
}
