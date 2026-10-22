import 'dart:convert';

import 'package:crypto/crypto.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'control_plane_support.dart';
import 'multiplayer_network_support.dart';

/// Answers the client must refuse, each from a fake service on 127.0.0.1:
/// too large, cut short, not canonical, a descriptor that fails, a hash
/// that does not match, and pages that would lead back.
void main() {
  final exchanges = RecordedExchanges.load();
  final filter = ServerBrowseFilter(gameBuild: '2545', protocolMajor: 1);
  final serverLink = JoinLinkTarget(SessionLink.server(hostAServerId));

  Future<FakeControlPlane> serving(CannedAnswer Function(SeenRequest) answer) =>
      FakeControlPlane.start(
        (request) => request.path == '/v1/status'
            ? exchanges.answer('status')
            : answer(request),
      );

  /// A service whose status is [answer].
  Future<FakeControlPlane> statusOf(CannedAnswer answer) =>
      FakeControlPlane.start((_) => answer);

  /// A server answer for [serverId] carrying [descriptor] as served, with a
  /// listing and hash that agree with it unless [sha] says otherwise.
  CannedAnswer serverAnswer(
    String descriptor, {
    String serverId = hostAServerId,
    String? sha,
  }) {
    final answer = recordedServerAnswer(exchanges);
    final hash = sha ?? sha256.convert(utf8.encode(descriptor)).toString();
    answer['descriptor'] = descriptor;
    answer['descriptorSha256'] = hash;
    answer['listing'] = {
      ...asObject(answer['listing']),
      'descriptorSha256': hash,
      'serverId': serverId,
    };
    return CannedAnswer.json(200, answer);
  }

  /// What a join of [target] comes to once its descriptor stage has run on
  /// what resolving fetched.
  Future<JoinSessionState> joined(
    FakeControlPlane service,
    JoinTarget target,
  ) async {
    final client = testClient();
    var state = JoinSession.start(target, privacy: SessionPrivacy.relayOnly);
    state = JoinSession.reduce(
      state,
      (await service.run(() => client.resolve(target)))!,
    );
    if (state.isEnded) return state;
    return JoinSession.reduce(
      state,
      (await service.run(
        () => client.fetchDescriptor(state.descriptorRequest!),
      ))!,
    );
  }

  group('a descriptor', () {
    test('that fails verification is never used, whichever way it '
        'fails', () async {
      final cases = asObjects(
        readMultiplayerFixture('descriptor/cases.json')['invalid'],
      ).where((item) => item['bytes'] is String).toList();
      expect(cases.length, greaterThan(25));
      for (final item in cases) {
        final serverId = item['expectedServerId'] as String? ?? hostAServerId;
        final answer = serverAnswer(
          item['bytes']! as String,
          serverId: serverId,
        );
        final service = await serving((_) => answer);
        final lookup = await service.run(
          () => testClient().fetchServer(serverId),
        );
        expect(lookup.isFound, isFalse, reason: item['id'] as String);
        expect(lookup.problem, MultiplayerServiceProblem.descriptorInvalid);
        final state = await joined(
          service,
          JoinLinkTarget(SessionLink.server(serverId)),
        );
        final failure = state.outcome!.failure!;
        expect(failure.code, JoinFailureCode.descriptorInvalid);
        expect(failure.stage, JoinStage.fetchingDescriptor);
      }
    });

    test('whose hash does not match the exact bytes served is never '
        'used', () async {
      final valid = utf8.decode(hostADescriptor);
      final other = sha256.convert(utf8.encode('$valid ')).toString();
      for (final sha in [other, 'a' * 64]) {
        final service = await serving((_) => serverAnswer(valid, sha: sha));
        final lookup = await service.run(
          () => testClient().fetchServer(hostAServerId),
        );
        expect(lookup.problem, MultiplayerServiceProblem.descriptorInvalid);
        expect(lookup.message, contains('does not match its hash'));
        final state = await joined(service, serverLink);
        expect(state.outcome!.failure!.code, JoinFailureCode.descriptorInvalid);
      }
      // The same bytes with their own hash verify.
      final service = await serving((_) => serverAnswer(valid));
      expect(
        (await service.run(
          () => testClient().fetchServer(hostAServerId),
        )).isFound,
        isTrue,
      );
    });

    test('of another server than the one asked for is never used', () async {
      final service = await serving(
        (_) =>
            serverAnswer(utf8.decode(hostBDescriptor), serverId: hostAServerId),
      );
      final state = await joined(service, serverLink);
      expect(state.outcome!.failure!.code, JoinFailureCode.descriptorInvalid);
    });
  });

  group('an answer', () {
    test('larger than its bound is refused, announced or not', () async {
      final padded = statusAnswer();
      final document = asObject(jsonDecode(utf8.decode(padded.bytes!)));
      document['notice'] = 'x' * 5000;
      for (final answer in [
        CannedAnswer.json(200, document),
        CannedAnswer(200, bytes: padded.bytes, announcedLength: 5000),
      ]) {
        final service = await statusOf(answer);
        final check = await service.run(() => testClient().readStatus());
        expect(check.isAvailable, isFalse);
        expect(
          check.problem,
          anyOf(
            MultiplayerServiceProblem.invalidAnswer,
            MultiplayerServiceProblem.unreachable,
          ),
        );
      }
      final page = await (await serving(
        (_) => CannedAnswer.json(200, {
          'next': null,
          'servers': [],
          'padding': 'x' * (300 * 1024),
        }),
      )).run(() => testClient().browse(filter));
      expect(page.problem, MultiplayerServiceProblem.invalidAnswer);
    });

    test('cut short is refused', () async {
      final whole = exchanges.answer('server-detail').bytes!;
      for (final answer in [
        // The connection ends before the announced length.
        CannedAnswer(
          200,
          bytes: whole.sublist(0, whole.length ~/ 2),
          announcedLength: whole.length,
        ),
        // The framing is whole, but the document is not.
        CannedAnswer(200, bytes: whole.sublist(0, whole.length ~/ 2)),
      ]) {
        final service = await serving((_) => answer);
        final lookup = await service.run(
          () => testClient().fetchServer(hostAServerId),
        );
        expect(lookup.isFound, isFalse);
        final event = await service.run(() => testClient().resolve(serverLink));
        expect((event! as JoinFailed).code, JoinFailureCode.serviceUnavailable);
      }
    });

    test('whose content coding does not decode is refused, and no call '
        'throws', () async {
      CannedAnswer garbled(int status, {String? retryAfter}) => CannedAnswer(
        status,
        // Not gzip, whatever the header says.
        bytes: [for (var index = 0; index < 64; index++) index],
        headers: {
          'content-type': 'application/json; charset=utf-8',
          'content-encoding': 'gzip',
          'retry-after': ?retryAfter,
        },
      );
      final service = await FakeControlPlane.start(
        (request) => switch (request.path) {
          '/v1/status' => garbled(200),
          '/v1/servers' => garbled(429, retryAfter: '30'),
          _ => garbled(200),
        },
      );
      final client = testClient();
      await service.run(() async {
        final status = await client.readStatus();
        expect(status.problem, MultiplayerServiceProblem.invalidAnswer);
        final lookup = await client.fetchServer(hostAServerId);
        expect(lookup.problem, MultiplayerServiceProblem.invalidAnswer);
        expect(
          (await client.lookUpInvite('F80BH-RS9YW')).problem,
          MultiplayerServiceProblem.invalidAnswer,
        );
        final event = await client.resolve(serverLink);
        expect((event! as JoinFailed).code, JoinFailureCode.serviceUnavailable);
        // A refusal is read by its status, so its delay still holds.
        final page = await client.browse(filter);
        expect(page.problem, MultiplayerServiceProblem.rateLimited);
        expect(page.retryAfter, const Duration(seconds: 30));
      });
    });

    test('that is not canonical JSON is refused', () async {
      final status = utf8.decode(exchanges.answer('status').bytes!);
      for (final text in [
        status.replaceFirst('{"api":1,', '{"api": 1,'),
        status.replaceFirst('{"api":1,', '{"api":1,\n'),
        '$status\n',
        status
            .replaceFirst('"api":1,"features"', '"features"')
            .replaceFirst('"hosting":{', '"api":1,"hosting":{'),
        status.replaceFirst('"api":1', '"api":1.0'),
        status.replaceFirst('"api":1', '"api":1,"api":1'),
        // A needless escape for the hyphen.
        status.replaceFirst(
          'control-plane-status',
          'control${String.fromCharCode(0x5C)}u002dplane-status',
        ),
        status.replaceFirst('topiaforge.', r'topiaforge\/'),
      ]) {
        final service = await statusOf(CannedAnswer(200, body: text));
        final check = await service.run(() => testClient().readStatus());
        expect(
          check.problem,
          MultiplayerServiceProblem.invalidAnswer,
          reason: text,
        );
      }
    });

    test('ignores members it does not know, and refuses ones it uses that are '
        'wrong', () async {
      final document = asObject(
        jsonDecode(utf8.decode(exchanges.answer('status').bytes!)),
      );
      final known = {...document, 'motd': 'Welcome', 'zone': 7};
      final service = await statusOf(CannedAnswer.json(200, known));
      expect(
        (await service.run(() => testClient().readStatus())).isAvailable,
        isTrue,
      );
      for (final broken in [
        {...document, 'api': 2},
        {...document, 'schema': 'topiaforge.control-plane-status/2'},
        {...document, 'killSwitch': 'no'},
        {...document}..remove('relay'),
        {
          ...document,
          'hosting': {'heartbeatSeconds': 30, 'listingTtlSeconds': 30},
        },
      ]) {
        final service = await statusOf(CannedAnswer.json(200, broken));
        final check = await service.run(() => testClient().readStatus());
        expect(check.problem, MultiplayerServiceProblem.invalidAnswer);
      }
    });

    test('with an unexpected success status is refused', () async {
      final service = await serving(
        (_) => CannedAnswer(201, bytes: exchanges.answer('servers-list').bytes),
      );
      final page = await service.run(() => testClient().browse(filter));
      expect(page.problem, MultiplayerServiceProblem.invalidAnswer);
    });
  });

  group('pages', () {
    final listing = asObject(
      asObjects(
        jsonDecode(
          exchanges.response('servers-list')['body']! as String,
        )['servers'],
      ).single,
    );
    Map<String, Object?> listingOf(String serverId) => {
      ...listing,
      'serverId': serverId,
    };
    final first = '1${'0' * 63}';
    final second = '2${'0' * 63}';
    final third = '3${'0' * 63}';

    Future<InternetBrowseResult> page(
      Map<String, Object?> body, {
      String? after,
      ServerBrowseFilter? using,
    }) async {
      final service = await serving((_) => CannedAnswer.json(200, body));
      return service.run(
        () => testClient().browse(using ?? filter, after: after),
      );
    }

    test('follow one another by their next server id', () async {
      final service = await serving(
        (request) => Uri.parse(request.target).queryParameters['after'] == null
            ? CannedAnswer.json(200, {
                'next': second,
                'servers': [listingOf(first), listingOf(second)],
              })
            : CannedAnswer.json(200, {
                'next': null,
                'servers': [listingOf(third)],
              }),
      );
      final client = testClient();
      final one = await service.run(() => client.browse(filter));
      expect(one.next, second);
      expect(one.listings.map((item) => item.serverId), [first, second]);
      final two = await service.run(
        () => client.browse(filter, after: one.next),
      );
      expect(service.seen.last.target, contains('after=$second'));
      expect(two.next, isNull);
      expect(two.listings.single.serverId, third);
    });

    test('never lead back: a repeated or backwards next, or servers before '
        'the cursor, are refused', () async {
      for (final (about, body, after) in [
        (
          'a next that repeats the cursor',
          {
            'next': second,
            'servers': [listingOf(second)],
          },
          second,
        ),
        (
          'a next behind the cursor',
          {
            'next': first,
            'servers': [listingOf(first)],
          },
          second,
        ),
        (
          'a next that is not the last server',
          {
            'next': third,
            'servers': [listingOf(first), listingOf(second)],
          },
          null,
        ),
        (
          'a next that goes back on the page',
          {
            'next': first,
            'servers': [listingOf(first), listingOf(second)],
          },
          null,
        ),
        (
          'a next with no servers',
          {'next': third, 'servers': <Object?>[]},
          null,
        ),
        (
          'servers out of order',
          {
            'next': null,
            'servers': [listingOf(second), listingOf(first)],
          },
          null,
        ),
        (
          'a server listed twice',
          {
            'next': null,
            'servers': [listingOf(first), listingOf(first)],
          },
          null,
        ),
        (
          'a server at the cursor',
          {
            'next': null,
            'servers': [listingOf(second), listingOf(third)],
          },
          second,
        ),
        (
          'a next that is no server id',
          {
            'next': 'zzz',
            'servers': [listingOf(first)],
          },
          null,
        ),
      ]) {
        final result = await page(body, after: after);
        expect(result.isAvailable, isFalse, reason: about);
        expect(result.problem, MultiplayerServiceProblem.invalidAnswer);
        expect(result.listings, isEmpty);
      }
    });

    test('hold only public listings of the build and protocol asked '
        'for', () async {
      for (final (about, item) in [
        ('another build', {...listingOf(first), 'gameBuild': '2546'}),
        ('another protocol', {...listingOf(first), 'protocolVersion': '2.0.0'}),
        (
          'an invite-only server',
          {...listingOf(first), 'visibility': 'invite'},
        ),
        ('too many players', {...listingOf(first), 'players': 9}),
        ('a malformed id', {...listingOf(first), 'serverId': 'ABC'}),
        ('no name', {...listingOf(first)}..remove('name')),
        ('a name with a newline', {...listingOf(first), 'name': 'Sam\n'}),
        ('another schema', {...listingOf(first), 'schema': 'listing/2'}),
        ('a fraction of a player', {...listingOf(first), 'players': '2'}),
      ]) {
        final result = await page({
          'next': null,
          'servers': [item],
        });
        expect(result.isAvailable, isFalse, reason: about);
      }
      final more = await page(
        {
          'next': null,
          'servers': [listingOf(first), listingOf(second)],
        },
        using: ServerBrowseFilter(
          gameBuild: '2545',
          protocolMajor: 1,
          pageSize: 1,
        ),
      );
      expect(more.isAvailable, isFalse, reason: 'more servers than asked');
    });
  });
}
