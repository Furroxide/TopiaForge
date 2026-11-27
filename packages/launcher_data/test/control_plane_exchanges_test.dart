import 'dart:convert';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'control_plane_support.dart';
import 'multiplayer_network_support.dart';

/// Replays the control plane's own recorded exchanges
/// (`tests/fixtures/multiplayer/control-plane/exchanges.json`) for every
/// call the launcher's client makes: the status, a page of the server list,
/// a server's detail, and an invite's resolution and miss. Each request goes
/// out as recorded, its body byte for byte, and each recorded answer reads.
///
/// The recording's other exchanges are calls only a host or the game makes
/// (registration, heartbeats, invites, tickets, relay credentials), or
/// requests this client never sends (a malformed body, a wrong method, a
/// path that is no server id).
void main() {
  final exchanges = RecordedExchanges.load();
  const descriptorSha =
      '918ec546e59e643a2dc3c6b6791d2c1c66945cd872f29b8e109ad13ed6a5ceae';

  /// A fake service that answers each request as the recording did for the
  /// recorded request with the same method and path.
  Future<FakeControlPlane> recorded(Map<String, String> pathToId) =>
      FakeControlPlane.start((request) {
        final id = pathToId[request.target];
        return id == null
            ? CannedAnswer.error(404, 'not-found')
            : exchanges.answer(id);
      });

  /// [seen] went out exactly as the recording's request [id].
  void expectRecorded(SeenRequest seen, String id) {
    final request = exchanges.request(id);
    expect(seen.method, request['method'], reason: id);
    expect(seen.target, request['path'], reason: id);
    final body = request['body'] as String?;
    if (body == null) {
      expect(seen.body, isEmpty, reason: id);
      // The service refuses any body framing where a method takes none.
      expect(seen.headers['transfer-encoding'], isNull, reason: id);
      expect(seen.headers['content-length'], anyOf(isNull, ['0']));
    } else {
      expect(seen.body, utf8.encode(body), reason: '$id, byte for byte');
      expect(seen.headers['content-type'], ['application/json; charset=utf-8']);
    }
    expectOnlyHelperHeaders(seen);
  }

  test('the status goes out as recorded and reads every feature', () async {
    final service = await recorded({'/v1/status': 'status'});
    final check = await service.run(() => testClient().readStatus());
    expect(service.seen, hasLength(1));
    expectRecorded(service.seen.single, 'status');
    final features = check.features!;
    expect(features.hosting, isTrue);
    expect(features.invites, isTrue);
    expect(features.signaling, isTrue);
    expect(features.relay, isTrue);
    expect(features.killSwitch, isFalse);
    expect(features.heartbeat, const Duration(seconds: 30));
    expect(features.listingTtl, const Duration(seconds: 90));
    expect(features.relayCredentialTtl, const Duration(minutes: 10));
    expect(features.relayAvailable, isTrue);
    expect(features.paths, MultiplayerServicePaths.relayAndDirect);
    expect(features.featuresOff, isEmpty);
    expect(features.notices, isEmpty);
  });

  test('a page of the server list goes out as recorded and reads as '
      'listings', () async {
    final service = await recorded({
      '/v1/servers?build=2545&protocol=1&limit=10': 'servers-list',
    });
    final page = await service.run(
      () => testClient().browse(
        ServerBrowseFilter(gameBuild: '2545', protocolMajor: 1, pageSize: 10),
      ),
    );
    expectRecorded(service.seen.single, 'servers-list');
    expect(page.isAvailable, isTrue);
    expect(page.next, isNull);
    final listing = page.listings.single;
    expect(listing.serverId, hostAServerId);
    expect(listing.source, ServerListingSource.internet);
    expect(listing.name, "Sam's Sandbox");
    expect(listing.players, 2);
    expect(listing.maxPlayers, 8);
    expect(listing.passwordRequired, isFalse);
    expect(listing.voice, isTrue);
    expect(listing.gamemode, 'Open Sandbox');
    expect(listing.world, 'Test City');
    expect(listing.gameBuild, '2545');
    expect(listing.runtimeVersion, '0.2.0');
    expect(listing.descriptorSha256, descriptorSha);
    expect(listing.readiness, ServerReadiness.unknown);
  });

  test('a server detail goes out as recorded, and its descriptor verifies '
      'for the id asked', () async {
    final service = await recorded({
      '/v1/servers/$hostAServerId': 'server-detail',
    });
    final lookup = await service.run(
      () => testClient().fetchServer(hostAServerId),
    );
    expectRecorded(service.seen.single, 'server-detail');
    final server = lookup.server!;
    expect(server.descriptor.serverId, hostAServerId);
    expect(server.descriptor.descriptorSha256, descriptorSha);
    expect(server.descriptor.envelope.bytes, hostADescriptor);
    expect(server.hostRelayOnly, isFalse);
    expect(server.listing.serverId, hostAServerId);
    expect(server.listing.players, 2);
    expect(server.listing.name, "Sam's Sandbox");
    expect(server.listing.descriptorSha256, descriptorSha);
  });

  test('an invite as the player typed it goes out as recorded, its code in '
      'the body only', () async {
    final service = await recorded({
      '/v1/status': 'status',
      '/v1/invites/resolve': 'invite-resolve',
    });
    final lookup = await service.run(
      () => testClient().lookUpInvite('f80bh-RS9YW'),
    );
    expect(service.seen.map((seen) => seen.path), [
      '/v1/status',
      '/v1/invites/resolve',
    ]);
    expectRecorded(service.seen.last, 'invite-resolve');
    for (final seen in service.seen) {
      for (final spelling in spellingsOf('F80BHRS9YW')) {
        expect(seen.target, isNot(contains(spelling)));
      }
    }
    expect(lookup.server!.descriptor.serverId, hostAServerId);
    expect(lookup.server!.descriptor.descriptorSha256, descriptorSha);
  });

  test('an invite miss goes out as recorded and reads as the uniform '
      'miss', () async {
    final service = await recorded({
      '/v1/status': 'status',
      '/v1/invites/resolve': 'invite-resolve-miss',
    });
    final lookup = await service.run(
      () => testClient().lookUpInvite('F80BHRS9YW'),
    );
    expectRecorded(service.seen.last, 'invite-resolve-miss');
    expect(lookup.isFound, isFalse);
    expect(lookup.problem, MultiplayerServiceProblem.notFound);
    expect(
      lookup.message,
      'That invite code does not work: it may be mistyped, expired or '
      'withdrawn, or its session may have ended.',
    );
  });

  group('the recorded join', () {
    test('an invite link resolves to its server, whose descriptor then '
        'verifies without asking again', () async {
      final service = await recorded({
        '/v1/status': 'status',
        '/v1/invites/resolve': 'invite-resolve',
      });
      final client = testClient();
      var state = JoinSession.start(
        JoinLinkTarget(SessionLink.invite('F80BH-RS9YW')),
        privacy: SessionPrivacy.relayOnly,
      );
      final resolved = await service.run(() => client.resolve(state.target));
      expect((resolved! as JoinTargetResolved).serverId, hostAServerId);
      state = JoinSession.reduce(state, resolved);
      expect(state.stage, JoinStage.fetchingDescriptor);
      final verified = await service.run(
        () => client.fetchDescriptor(state.descriptorRequest!),
      );
      state = JoinSession.reduce(state, verified!);
      expect(state.stage, JoinStage.checking);
      expect(state.descriptor!.descriptorSha256, descriptorSha);
      // The canonical code went in the body; resolving fetched the
      // descriptor once, for both stages.
      expect(service.seen.map((seen) => seen.path), [
        '/v1/status',
        '/v1/invites/resolve',
      ]);
      expect(service.seen.last.text, '{"code":"F80BHRS9YW"}');
    });

    test('a server link and an Internet listing resolve through the '
        'recorded server detail', () async {
      for (final target in [
        JoinLinkTarget(SessionLink.server(hostAServerId)),
        JoinListingTarget(
          serverId: hostAServerId,
          source: ServerListingSource.internet,
        ),
      ]) {
        final service = await recorded({
          '/v1/status': 'status',
          '/v1/servers/$hostAServerId': 'server-detail',
        });
        final client = testClient();
        var state = JoinSession.start(
          target,
          privacy: SessionPrivacy.relayOnly,
        );
        state = JoinSession.reduce(
          state,
          (await service.run(() => client.resolve(target)))!,
        );
        expect(state.stage, JoinStage.fetchingDescriptor);
        state = JoinSession.reduce(
          state,
          (await service.run(
            () => client.fetchDescriptor(state.descriptorRequest!),
          ))!,
        );
        expect(state.stage, JoinStage.checking, reason: '$target');
        expect(state.serverId, hostAServerId);
        expect(service.seen.map((seen) => seen.path), [
          '/v1/status',
          '/v1/servers/$hostAServerId',
        ]);
        expectRecorded(service.seen.last, 'server-detail');
      }
    });
  });
}
