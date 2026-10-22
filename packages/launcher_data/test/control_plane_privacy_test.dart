import 'dart:async';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_data/src/multiplayer/control_plane_client.dart';
import 'package:launcher_data/src/multiplayer/control_plane_pacing.dart';
import 'package:launcher_data/src/multiplayer/control_plane_requests.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'control_plane_support.dart';
import 'multiplayer_network_support.dart';

/// What the client keeps to itself: an invite code never reaches a log line,
/// a failure, a message or an exception, nothing names the service's address
/// or any other, it sends no header beyond the JSON helper's own, and it
/// writes nothing to disk.
void main() {
  final exchanges = RecordedExchanges.load();
  const code = 'RJ9JMV1V09';
  const typed = 'rj9jm-v1v09';
  final target = JoinLinkTarget(SessionLink.invite(code));

  /// Every way a service can answer an invite resolution.
  final answers = <String, CannedAnswer Function()>{
    'a hit': () => exchanges.answer('invite-resolve'),
    'a miss': () => CannedAnswer.error(404, 'not-found'),
    'a rate limit': () =>
        CannedAnswer.error(429, 'rate-limited', retryAfter: '20'),
    'a failure': () => CannedAnswer.error(500, 'internal'),
    'invites turned off': () => CannedAnswer.error(503, 'feature-disabled'),
    'a refusal': () => CannedAnswer.error(400, 'invalid-request'),
    'an unreadable answer': () => CannedAnswer(200, body: '{"servers": []}'),
    'a page from a platform': () => CannedAnswer(
      502,
      body: '<html>$code</html>',
      headers: {'content-type': 'text/html'},
    ),
  };

  /// Everything the client said while [body] ran: its log lines, and the
  /// texts [body] collects from what it returned or threw.
  Future<List<String>> said(
    Future<void> Function(HttpsControlPlaneClient client, List<String> texts)
    body, {
    bool online = true,
    ControlPlaneTimeouts timeouts = const ControlPlaneTimeouts(),
  }) async {
    final log = <String>[];
    final texts = <String>[];
    final client = testClient(log: log, online: online, timeouts: timeouts);
    try {
      await body(client, texts);
    } on Object catch (error) {
      texts.add('$error');
    }
    return [...log, ...texts];
  }

  /// Every text an invite join and lookup of [code] gives.
  Future<void> inviteTexts(
    HttpsControlPlaneClient client,
    List<String> texts, {
    CancellationToken? cancellation,
  }) async {
    final resolved = await client.resolve(target, cancellation: cancellation);
    texts.add('$resolved');
    if (resolved is JoinFailed) texts.add(resolved.detail);
    if (resolved is JoinTargetResolved) {
      final state = JoinSession.reduce(
        JoinSession.start(target, privacy: SessionPrivacy.relayOnly),
        resolved,
      );
      final fetched = await client.fetchDescriptor(state.descriptorRequest!);
      texts.add('$fetched $state ${state.descriptorRequest}');
      if (fetched is JoinFailed) texts.add(fetched.detail);
    }
    final lookup = await client.lookUpInvite(typed, cancellation: cancellation);
    texts
      ..add('${lookup.message} ${lookup.problem} ${lookup.server}')
      ..add('$target');
  }

  void expectNoCode(List<String> texts, String about) {
    expect(texts, isNotEmpty);
    for (final text in texts) {
      for (final spelling in spellingsOf(code)) {
        expect(text, isNot(contains(spelling)), reason: '$about: $text');
      }
    }
  }

  void expectNoAddress(List<String> texts, String about) {
    for (final text in texts) {
      expect(text, isNot(contains(testOrigin.host)), reason: about);
      expect(text, isNot(contains('example.invalid')), reason: about);
      expect(addressPattern.hasMatch(text), isFalse, reason: '$about: $text');
      for (final address in addressTexts) {
        expect(text, isNot(contains(address)), reason: about);
      }
    }
  }

  group('an invite code never reaches a log, a failure or a message', () {
    for (final MapEntry(key: about, value: answer) in answers.entries) {
      test('after $about', () async {
        final service = await FakeControlPlane.start(
          (request) => request.path == '/v1/status'
              ? exchanges.answer('status')
              : answer(),
        );
        final texts = await said(
          (client, texts) => service.run(() => inviteTexts(client, texts)),
        );
        expect(
          texts.where((text) => text.startsWith('TopiaForge service:')),
          isNotEmpty,
        );
        expectNoCode(texts, about);
        expectNoAddress(texts, about);
        // The code went to the service in the body only.
        for (final seen in service.seen) {
          expect(seen.target, isNot(contains(code)));
          expect(
            seen.target.toLowerCase(),
            isNot(contains(code.toLowerCase())),
          );
        }
      });
    }

    test('when the service cannot be reached, answers too late, or the join '
        'is cancelled', () async {
      final quiet = Completer<void>();
      addTearDown(() => quiet.isCompleted ? null : quiet.complete());
      final slow = await FakeControlPlane.start(
        (request) => request.path == '/v1/status'
            ? exchanges.answer('status')
            : CannedAnswer(200, body: '{}', hold: quiet.future),
      );
      final gone = await FakeControlPlane.start(
        (_) => exchanges.answer('status'),
      );
      await gone.close();
      for (final (about, texts) in [
        (
          'no service',
          await said(
            (client, texts) => gone.run(() => inviteTexts(client, texts)),
            online: false,
          ),
        ),
        (
          'a late answer',
          await said(
            (client, texts) => slow.run(() => inviteTexts(client, texts)),
            timeouts: const ControlPlaneTimeouts(
              response: Duration(milliseconds: 200),
              attempt: Duration(milliseconds: 400),
              call: Duration(seconds: 1),
            ),
          ),
        ),
        (
          'a cancel',
          await said((client, texts) async {
            final cancellation = CancellationToken();
            final sent = slow.seen.length;
            final joined = slow.run(
              () => inviteTexts(client, texts, cancellation: cancellation),
            );
            await eventually(() => slow.seen.length > sent + 1);
            cancellation.cancel();
            await joined;
          }),
        ),
      ]) {
        expectNoCode(texts, about);
        expectNoAddress(texts, about);
      }
    });

    test('nor does a request print its body', () {
      const request = ControlPlaneRequest(
        scope: ControlPlaneScope.invite,
        operation: 'invite resolution',
        method: 'POST',
        path: '/v1/invites/resolve',
        body: {'code': code},
        maxBytes: 1024,
      );
      expectNoCode(['$request'], 'a request');
    });
  });

  test('every request carries only the JSON helper\'s own headers', () async {
    final service = await FakeControlPlane.start(
      (request) => switch (request.path) {
        '/v1/status' => exchanges.answer('status'),
        '/v1/servers' => exchanges.answer('servers-list'),
        '/v1/invites/resolve' => exchanges.answer('invite-resolve'),
        _ => exchanges.answer('server-detail'),
      },
    );
    final client = testClient();
    await service.run(() async {
      await client.readStatus();
      await client.browse(
        ServerBrowseFilter(gameBuild: '2545', protocolMajor: 1),
      );
      await client.fetchServer(hostAServerId);
      await client.lookUpInvite(typed);
    });
    expect(service.seen.map((seen) => seen.method).toSet(), {'GET', 'POST'});
    service.seen.forEach(expectOnlyHelperHeaders);
  });

  test('nothing is written to disk', () async {
    final service = await FakeControlPlane.start(
      (request) => switch (request.path) {
        '/v1/status' => exchanges.answer('status'),
        '/v1/invites/resolve' => CannedAnswer.error(404, 'not-found'),
        _ => exchanges.answer('server-detail'),
      },
    );
    final client = testClient();
    var writes = 0;
    Never refuse(String path) {
      writes++;
      throw const FileSystemException('Nothing may be written.');
    }

    await IOOverrides.runZoned(
      () => service.run(() async {
        await client.resolve(target);
        await client.resolve(JoinLinkTarget(SessionLink.server(hostAServerId)));
        await client.fetchServer(hostAServerId);
      }),
      createFile: refuse,
      createDirectory: refuse,
      createLink: refuse,
    );
    expect(writes, 0);
    expect(service.seen, hasLength(4));
  });
}
