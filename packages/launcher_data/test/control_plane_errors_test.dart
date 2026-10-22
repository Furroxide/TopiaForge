import 'dart:async';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_data/src/multiplayer/control_plane_client.dart';
import 'package:launcher_data/src/multiplayer/control_plane_requests.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'control_plane_support.dart';
import 'multiplayer_network_support.dart';

/// Every error the service can send for the calls the launcher makes, each
/// against a fake service on 127.0.0.1: the join failure it becomes, how
/// often it is sent, and what is held back after it.
void main() {
  final exchanges = RecordedExchanges.load();
  final invite = JoinLinkTarget(SessionLink.invite('RJ9JM-V1V09'));
  final serverLink = JoinLinkTarget(SessionLink.server(hostAServerId));
  final filter = ServerBrowseFilter(gameBuild: '2545', protocolMajor: 1);

  /// A service whose status is fine and whose other answers come from
  /// [answer], counted by path.
  Future<FakeControlPlane> serving(
    CannedAnswer Function(SeenRequest request) answer,
  ) => FakeControlPlane.start(
    (request) => request.path == '/v1/status'
        ? exchanges.answer('status')
        : answer(request),
  );

  int sentTo(FakeControlPlane service, String path) =>
      service.seen.where((seen) => seen.path == path).length;

  group('each error', () {
    // status, code, the join failure, how many times one call sends it.
    final cases = <(int, String?, JoinFailureCode, JoinFailureCode, int)>[
      (400, 'invalid-request', .serviceUnavailable, .serviceUnavailable, 1),
      (401, 'unauthorized', .serviceUnavailable, .serviceUnavailable, 1),
      (403, 'server-blocked', .serviceUnavailable, .serviceUnavailable, 1),
      (404, 'not-found', .inviteExpired, .serverNotListed, 1),
      (405, 'method-not-allowed', .serviceUnavailable, .serviceUnavailable, 1),
      (409, 'room-full', .serviceUnavailable, .serviceUnavailable, 1),
      (413, 'payload-too-large', .serviceUnavailable, .serviceUnavailable, 1),
      (
        415,
        'unsupported-media-type',
        .serviceUnavailable,
        .serviceUnavailable,
        1,
      ),
      (429, 'rate-limited', .rateLimited, .rateLimited, 1),
      (500, 'internal', .serviceUnavailable, .serviceUnavailable, 3),
      (502, 'upstream-failed', .serviceUnavailable, .serviceUnavailable, 3),
      (503, 'feature-disabled', .serviceUnavailable, .serviceUnavailable, 1),
      (503, 'relay-unavailable', .serviceUnavailable, .serviceUnavailable, 1),
      (503, 'service-unavailable', .serviceUnavailable, .serviceUnavailable, 1),
      // A code whose status disagrees with the catalogue is no miss.
      (404, 'rate-limited', .serviceUnavailable, .serviceUnavailable, 1),
      // Pages from a platform in front of the service.
      (404, null, .serviceUnavailable, .serviceUnavailable, 1),
      (429, null, .rateLimited, .rateLimited, 1),
      (503, null, .serviceUnavailable, .serviceUnavailable, 3),
      (504, null, .serviceUnavailable, .serviceUnavailable, 3),
    ];

    for (final (status, code, forInvite, forServer, sends) in cases) {
      test('$status ${code ?? 'from a platform page'} ends a join as '
          '${forInvite.name} or ${forServer.name}, sent $sends '
          'time${sends == 1 ? '' : 's'}', () async {
        CannedAnswer answer(SeenRequest _) => code == null
            ? CannedAnswer(
                status,
                body: '<html>try later</html>',
                headers: {'content-type': 'text/html'},
              )
            : CannedAnswer.error(status, code);
        for (final (target, expected, path) in [
          (invite, forInvite, '/v1/invites/resolve'),
          (serverLink, forServer, '/v1/servers/$hostAServerId'),
        ]) {
          final service = await serving(answer);
          final event =
              await service.run(() => testClient().resolve(target))
                  as JoinFailed;
          expect(event.code, expected, reason: '$target');
          expect(sentTo(service, path), sends, reason: '$target');
          // The service's own message is never read or repeated.
          expect(event.detail, isNot(contains('Fixed text')));
          expect(event.detail, isNot(contains('try later')));
          expect(event.detail, isNotEmpty);
          // The failure is one the resolving stage takes.
          final state = JoinSession.start(
            target,
            privacy: SessionPrivacy.relayOnly,
          );
          expect(
            JoinSession.reduce(state, event).outcome!.failure!.code,
            expected,
          );
        }
      });
    }
  });

  test('the uniform miss says neither expired nor withdrawn, and the same '
      'code is not sent again for a while', () async {
    final service = await serving((_) => CannedAnswer.error(404, 'not-found'));
    final time = FakeTime();
    final client = testClient(time: time);
    Future<JoinFailed> join(String code) async =>
        await service.run(
              () => client.resolve(JoinLinkTarget(SessionLink.invite(code))),
            )
            as JoinFailed;
    final first = await join('RJ9JM-V1V09');
    expect(first.code, JoinFailureCode.inviteExpired);
    expect(
      first.detail,
      'That invite code does not work: it may be mistyped, expired or '
      'withdrawn, or its session may have ended.',
    );
    // The same code, however it is spelled, is answered without asking.
    for (final spelling in ['rj9jmv1v09', 'RJ9JM V1V09', 'RJ9JMV1VO9']) {
      final again = await join(spelling);
      expect(again.code, first.code);
      expect(again.detail, first.detail);
    }
    expect(sentTo(service, '/v1/invites/resolve'), 1);
    // Another code is asked about.
    await join('F80BH-RS9YW');
    expect(sentTo(service, '/v1/invites/resolve'), 2);
    time.advance(HttpsControlPlaneClient.missMemory);
    await join('RJ9JM-V1V09');
    expect(sentTo(service, '/v1/invites/resolve'), 3);
  });

  test('a 4xx is never sent again by its call; a 5xx is, after a backoff '
      'that doubles', () async {
    for (final (code, status, waits) in [
      ('invalid-request', 400, <Duration>[]),
      ('internal', 500, const [Duration(seconds: 1), Duration(seconds: 2)]),
    ]) {
      final service = await serving((_) => CannedAnswer.error(status, code));
      final time = FakeTime();
      final page = await service.run(
        () => testClient(time: time).browse(filter),
      );
      expect(page.isAvailable, isFalse);
      expect(time.waits, waits, reason: code);
      expect(sentTo(service, '/v1/servers'), waits.length + 1);
    }
  });

  test('a 5xx that passes is a success', () async {
    var calls = 0;
    final service = await serving(
      (_) => ++calls < 3
          ? CannedAnswer.error(502, 'upstream-failed')
          : exchanges.answer('servers-list'),
    );
    final time = FakeTime();
    final page = await service.run(() => testClient(time: time).browse(filter));
    expect(page.listings.single.serverId, hostAServerId);
    expect(time.waits, const [Duration(seconds: 1), Duration(seconds: 2)]);
  });

  test('Retry-After holds its scope: nothing is sent until it '
      'passes', () async {
    final service = await serving(
      (request) => request.path == '/v1/servers'
          ? CannedAnswer.error(429, 'rate-limited', retryAfter: '30')
          : exchanges.answer('server-detail'),
    );
    final time = FakeTime();
    final client = testClient(time: time);
    final limited = await service.run(() => client.browse(filter));
    expect(limited.problem, MultiplayerServiceProblem.rateLimited);
    expect(limited.retryAfter, const Duration(seconds: 30));
    expect(limited.unavailableMessage, contains('Try again in 30 seconds'));
    expect(sentTo(service, '/v1/servers'), 1);

    time.advance(const Duration(seconds: 29));
    final held = await service.run(() => client.browse(filter));
    expect(held.problem, MultiplayerServiceProblem.rateLimited);
    expect(held.retryAfter, const Duration(seconds: 1));
    expect(held.unavailableMessage, contains('Try again in 1 second.'));
    // The service named the delay for the whole network, so the message
    // does not blame this computer.
    expect(held.unavailableMessage, contains('from this network'));
    expect(held.unavailableMessage, isNot(contains('This computer')));
    // The list's limit is shared by every launcher on the network, and the
    // service does not limit a server's detail, so a join is not held.
    final join = await service.run(() => client.resolve(serverLink));
    expect(join, isA<JoinTargetResolved>());
    expect(sentTo(service, '/v1/servers'), 1);
    expect(sentTo(service, '/v1/servers/$hostAServerId'), 1);

    time.advance(const Duration(seconds: 1));
    await service.run(() => client.browse(filter));
    expect(sentTo(service, '/v1/servers'), 2);
  });

  test('a delay named with a 503 is honoured, and stops its call when it is '
      'long', () async {
    final service = await serving(
      (_) => CannedAnswer(
        503,
        body: '<html>busy</html>',
        headers: {'content-type': 'text/html', 'retry-after': '60'},
      ),
    );
    final time = FakeTime();
    final client = testClient(time: time);
    final failed =
        await service.run(() => client.resolve(serverLink)) as JoinFailed;
    expect(failed.code, JoinFailureCode.serviceUnavailable);
    expect(failed.detail, contains('Try again in 60 seconds'));
    expect(sentTo(service, '/v1/servers/$hostAServerId'), 1);
    expect(time.waits, isEmpty);
    time.advance(const Duration(seconds: 59));
    await service.run(() => client.resolve(serverLink));
    expect(sentTo(service, '/v1/servers/$hostAServerId'), 1);
  });

  test('failures in a row back off for longer each time, and an answer ends '
      'the run', () async {
    var failing = true;
    final service = await serving(
      (_) => failing
          ? CannedAnswer.error(500, 'internal')
          : exchanges.answer('servers-list'),
    );
    final time = FakeTime();
    final client = testClient(time: time);
    final delays = <Duration?>[];
    for (var round = 0; round < 3; round++) {
      final page = await service.run(() => client.browse(filter));
      delays.add(page.retryAfter);
      time.advance(page.retryAfter!);
    }
    // Three tries, then one each round as the wait outgrows a call.
    expect(delays, const [
      Duration(seconds: 4),
      Duration(seconds: 8),
      Duration(seconds: 16),
    ]);
    expect(sentTo(service, '/v1/servers'), 5);
    failing = false;
    expect(
      (await service.run(() => client.browse(filter))).isAvailable,
      isTrue,
    );
    failing = true;
    final again = await service.run(() => client.browse(filter));
    expect(again.retryAfter, const Duration(seconds: 4));
  });

  test('a 429 without Retry-After holds its scope for a default '
      'delay', () async {
    final service = await serving(
      (_) => CannedAnswer.error(429, 'rate-limited'),
    );
    final page = await service.run(() => testClient().browse(filter));
    expect(page.retryAfter, ControlPlaneRequester.defaultRateLimitDelay);
  });

  test('a service that cannot be reached is offline only without a '
      'network', () async {
    final service = await serving((_) => exchanges.answer('status'));
    // Nothing listens any more, so every connection is refused.
    await service.close();
    for (final (online, code) in [
      (true, JoinFailureCode.serviceUnavailable),
      (false, JoinFailureCode.offline),
    ]) {
      final event = await service.run(
        () => testClient(online: online).resolve(serverLink),
      );
      expect((event! as JoinFailed).code, code);
    }
    final page = await service.run(
      () => testClient(online: false).browse(filter),
    );
    expect(page.problem, MultiplayerServiceProblem.offline);
    // Without a network LAN play does not work either, so neither sentence
    // says it does; with one, it still works.
    final status = await service.run(
      () => testClient(online: false).readStatus(),
    );
    for (final message in [page.unavailableMessage, status.message]) {
      expect(message, contains('not connected to a network'));
      expect(message, isNot(contains('LAN')));
    }
    final unreachable = await service.run(() => testClient().browse(filter));
    expect(unreachable.problem, MultiplayerServiceProblem.unreachable);
    expect(
      unreachable.unavailableMessage,
      contains('LAN and direct play still work'),
    );
  });

  test('a service that does not answer in time fails within the call\'s '
      'limits', () async {
    final never = Completer<void>();
    addTearDown(never.complete);
    final service = await serving(
      (_) => CannedAnswer(200, body: '{}', hold: never.future),
    );
    final client = testClient(
      timeouts: const ControlPlaneTimeouts(
        connect: Duration(seconds: 2),
        response: Duration(milliseconds: 300),
        attempt: Duration(milliseconds: 600),
        call: Duration(seconds: 2),
      ),
    );
    final watch = Stopwatch()..start();
    final failed =
        await service.run(() => client.resolve(serverLink)) as JoinFailed;
    expect(watch.elapsed, lessThan(const Duration(seconds: 5)));
    expect(failed.code, JoinFailureCode.serviceUnavailable);
    expect(failed.detail, contains('did not answer in time'));
  });
}
