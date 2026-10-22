import 'dart:async';
import 'dart:convert';

import 'package:launcher_data/src/multiplayer/control_plane_client.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_network_support.dart';
import 'secure_http_loopback.dart';

/// The control plane's recorded exchanges
/// (`tests/fixtures/multiplayer/control-plane/exchanges.json`), by id.
final class RecordedExchanges {
  RecordedExchanges._(this.origin, this._byId);

  factory RecordedExchanges.load() {
    final fixture = readMultiplayerFixture('control-plane/exchanges.json');
    return RecordedExchanges._(Uri.parse(fixture['origin']! as String), {
      for (final exchange in asObjects(fixture['exchanges']))
        exchange['id']! as String: exchange,
    });
  }

  final Uri origin;
  final Map<String, Map<String, Object?>> _byId;

  Map<String, Object?> request(String id) => asObject(_byId[id]!['request']);

  Map<String, Object?> response(String id) => asObject(_byId[id]!['response']);

  /// The recorded answer to [id], exactly as the service sent it.
  CannedAnswer answer(String id) {
    final response = this.response(id);
    return CannedAnswer(
      response['status']! as int,
      body: response['body'] as String?,
      headers: asObject(response['headers']).cast<String, String>(),
    );
  }
}

/// An answer the fake service gives: a status, headers and exact body bytes.
final class CannedAnswer {
  CannedAnswer(
    this.status, {
    String? body,
    List<int>? bytes,
    Map<String, String>? headers,
    this.announcedLength,
    this.hold,
  }) : bytes = bytes ?? (body == null ? null : utf8.encode(body)),
       headers =
           headers ??
           {
             'cache-control': 'no-store',
             'content-type': 'application/json; charset=utf-8',
             'x-content-type-options': 'nosniff',
           };

  /// A canonical JSON answer: [value] is written with sorted keys and no
  /// white space, as the service writes.
  factory CannedAnswer.json(int status, Object? value) =>
      CannedAnswer(status, body: canonicalText(value));

  /// The service's error answer for [code].
  factory CannedAnswer.error(int status, String code, {String? retryAfter}) {
    final answer = CannedAnswer.json(status, {
      'error': {'code': code, 'message': 'Fixed text.'},
    });
    if (retryAfter != null) answer.headers['retry-after'] = retryAfter;
    return answer;
  }

  final int status;
  final List<int>? bytes;
  final Map<String, String> headers;

  /// A `Content-Length` larger than the bytes sent, so the body is cut
  /// short.
  final int? announcedLength;

  /// Holds the answer until this completes.
  final Future<void>? hold;
}

/// One request the fake service saw.
final class SeenRequest {
  SeenRequest(this.method, this.target, this.headers, this.body);

  final String method;

  /// The path and query, as sent.
  final String target;
  final Map<String, List<String>> headers;
  final List<int> body;

  String get path => Uri.parse(target).path;
  String get text => utf8.decode(body);
}

/// A fake TopiaForge service on 127.0.0.1. Every HTTPS client the code under
/// test creates reaches it ([LoopbackHttps]), whatever origin it names.
/// [answers] picks each answer by method and path; it may return the same
/// one for every request, or count them.
final class FakeControlPlane {
  FakeControlPlane._(this._https, this.seen);

  static Future<FakeControlPlane> start(
    CannedAnswer Function(SeenRequest request) answers,
  ) async {
    final seen = <SeenRequest>[];
    final https = await LoopbackHttps.start((incoming) async {
      final headers = <String, List<String>>{};
      incoming.headers.forEach((name, values) => headers[name] = values);
      final body = <int>[];
      await incoming.forEach(body.addAll);
      final request = SeenRequest(
        incoming.method,
        incoming.uri.toString(),
        headers,
        body,
      );
      seen.add(request);
      final answer = answers(request);
      final hold = answer.hold;
      if (hold != null) await hold;
      final response = incoming.response..statusCode = answer.status;
      response.headers.clear();
      answer.headers.forEach(response.headers.set);
      final bytes = answer.bytes;
      final announced = answer.announcedLength;
      if (announced != null) {
        // Announce more than is sent, send what there is, then hang up.
        response.headers.contentLength = announced;
        final socket = await response.detachSocket();
        if (bytes != null) socket.add(bytes);
        await socket.flush();
        socket.destroy();
        return;
      }
      if (bytes != null) {
        response.headers.contentLength = bytes.length;
        response.add(bytes);
      }
      await response.close();
    });
    addTearDown(https.close);
    return FakeControlPlane._(https, seen);
  }

  final LoopbackHttps _https;
  final List<SeenRequest> seen;

  int get clientsCreated => _https.clientsCreated;

  /// Stops listening, so every later connection is refused.
  Future<void> close() => _https.close();

  /// Runs [body] with every HTTPS client routed here.
  Future<T> run<T>(Future<T> Function() body) => _https.run(body);
}

/// A clock that moves only when told, and timers that fire on the next
/// microtask after moving it by their delay, so backoff runs at once.
final class FakeTime {
  FakeTime([DateTime? start]) : now = start ?? DateTime.utc(2032, 6, 1, 12);

  DateTime now;
  final waits = <Duration>[];

  DateTime call() => now;

  void advance(Duration by) => now = now.add(by);

  Timer timer(Duration delay, void Function() fire) {
    waits.add(delay);
    final timer = _FakeTimer();
    scheduleMicrotask(() {
      if (!timer.isActive) return;
      timer.cancel();
      advance(delay);
      fire();
    });
    return timer;
  }
}

final class _FakeTimer implements Timer {
  var _active = true;

  @override
  void cancel() => _active = false;

  @override
  bool get isActive => _active;

  @override
  int get tick => 0;
}

/// The origin every test client names; the fake service answers for it.
final testOrigin = Uri.parse('https://control-plane.example.invalid');

/// A client of the fake service, with [time] for its clock and backoff and
/// its log lines in [log].
HttpsControlPlaneClient testClient({
  FakeTime? time,
  List<String>? log,
  bool online = true,
  ControlPlaneTimeouts timeouts = const ControlPlaneTimeouts(),
}) {
  final clock = time ?? FakeTime();
  return HttpsControlPlaneClient(
    testOrigin,
    log: log?.add,
    clock: clock.call,
    timer: clock.timer,
    networkPresence: () async => online,
    timeouts: timeouts,
  );
}

/// [value] as canonical JSON text: sorted keys, no white space.
String canonicalText(Object? value) => CanonicalJson.encodeToString(value);

/// The status answer with [features] changed.
CannedAnswer statusAnswer({
  bool hosting = true,
  bool invites = true,
  bool relay = true,
  bool signaling = true,
  bool killSwitch = false,
}) => CannedAnswer.json(200, {
  'api': 1,
  'features': {
    'hosting': hosting,
    'invites': invites,
    'relay': relay,
    'signaling': signaling,
  },
  'hosting': {'heartbeatSeconds': 30, 'listingTtlSeconds': 90},
  'killSwitch': killSwitch,
  'relay': {'credentialTtlSeconds': 600},
  'schema': 'topiaforge.control-plane-status/1',
});

/// The recorded server answer (`server-detail` or `invite-resolve`) as JSON,
/// to change before it is served again.
Map<String, Object?> recordedServerAnswer(
  RecordedExchanges exchanges, [
  String id = 'server-detail',
]) => asObject(jsonDecode(exchanges.response(id)['body']! as String));

/// Every spelling of an invite code a log or message could leak.
List<String> spellingsOf(String code) {
  final canonical = code.replaceAll('-', '').toUpperCase();
  return {
    code,
    canonical,
    canonical.toLowerCase(),
    '${canonical.substring(0, 5)}-${canonical.substring(5)}',
    '${canonical.substring(0, 5)}-${canonical.substring(5)}'.toLowerCase(),
    canonical.substring(0, 5),
    canonical.substring(5),
  }.toList();
}

/// Waits until [condition] holds, checking on each turn of the event loop.
Future<void> eventually(bool Function() condition) async {
  for (var turn = 0; turn < 10000 && !condition(); turn++) {
    await Future<void>.delayed(Duration.zero);
  }
  expect(condition(), isTrue);
}

/// The headers dart:io sends itself, besides the request's own.
const transportHeaders = {'host', 'user-agent', 'accept-encoding'};

/// Whether [request] came with exactly the headers the JSON helper sends.
void expectOnlyHelperHeaders(SeenRequest request) {
  final expected = {
    ...transportHeaders,
    'accept',
    'connection',
    if (request.body.isNotEmpty) ...{'content-type', 'content-length'},
  };
  final names = request.headers.keys.toSet()..remove('content-length');
  expected.remove('content-length');
  expect(names, expected, reason: request.target);
  expect(request.headers['accept'], ['application/json']);
  expect(
    request.headers['user-agent']!.single,
    matches(r'^Dart/[0-9.]+ \(dart:io\)$'),
  );
  expect(request.headers['accept-encoding'], ['gzip']);
  expect(request.headers.containsKey('authorization'), isFalse);
  expect(request.headers.containsKey('cookie'), isFalse);
}

/// An address-shaped text never belongs in a log line or a message.
final addressPattern = RegExp(
  r'\b(?:\d{1,3}\.){3}\d{1,3}\b|\[?[0-9a-fA-F]*:[0-9a-fA-F:]+\]?:\d+',
);
