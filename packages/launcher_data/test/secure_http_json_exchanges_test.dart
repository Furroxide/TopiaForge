import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/src/secure_http.dart';
import 'package:test/test.dart';

import 'multiplayer_network_support.dart';
import 'secure_http_loopback.dart';

/// Replays the control plane's own recorded exchanges
/// (`tests/fixtures/multiplayer/control-plane/exchanges.json`) through the
/// JSON helper: each request goes out as the service recorded it, its body
/// byte for byte, and each recorded answer reads as the service sent it.
void main() {
  final fixture = readMultiplayerFixture('control-plane/exchanges.json');
  final origin = Uri.parse(fixture['origin']! as String);
  const label = 'The multiplayer service';

  /// [value] with every object's members in reverse order, so a body matches
  /// the recording only when the helper puts its members in order itself.
  Object? reversed(Object? value) => switch (value) {
    Map() => {
      for (final key in value.keys.toList().reversed) key: reversed(value[key]),
    },
    List() => [for (final item in value) reversed(item)],
    _ => value,
  };

  for (final exchange in asObjects(fixture['exchanges'])) {
    final id = exchange['id']! as String;
    final request = asObject(exchange['request']);
    final response = asObject(exchange['response']);
    final requestHeaders = asObject(request['headers']).cast<String, String>();
    final responseHeaders = asObject(
      response['headers'],
    ).cast<String, String>();
    final requestBody = request['body'] as String?;
    final responseBody = response['body'] as String?;
    final path = request['path']! as String;
    final question = path.indexOf('?');

    // The service's example of a body that is not canonical, which this
    // helper never writes; see the test after this loop.
    if (id == 'error-malformed') continue;

    test(
      '$id goes out as recorded and reads as the service answered',
      () async {
        String? method;
        String? target;
        String? authorization;
        String? contentType;
        String? contentLength;
        String? transferEncoding;
        final sent = <int>[];
        final https = await LoopbackHttps.start((incoming) async {
          method = incoming.method;
          target = incoming.uri.toString();
          authorization = incoming.headers.value('authorization');
          contentType = incoming.headers.value('content-type');
          contentLength = incoming.headers.value('content-length');
          transferEncoding = incoming.headers.value('transfer-encoding');
          await incoming.forEach(sent.addAll);
          final answer = incoming.response
            ..statusCode = response['status']! as int;
          responseHeaders.forEach(answer.headers.set);
          if (responseBody != null) answer.add(utf8.encode(responseBody));
          await answer.close();
        });
        addTearDown(https.close);

        final result = await https.run(
          () => requestHttpsJson(
            origin.replace(
              path: question < 0 ? path : path.substring(0, question),
            ),
            label: label,
            method: request['method']! as String,
            query: question < 0
                ? const {}
                : Uri.splitQueryString(path.substring(question + 1)),
            headers: {'authorization': ?requestHeaders['authorization']},
            body: requestBody == null
                ? null
                : reversed(jsonDecode(requestBody)),
            maxResponseBytes: 64 * 1024,
          ),
        );

        expect(method, request['method']);
        expect(target, path);
        expect(authorization, requestHeaders['authorization']);
        if (requestBody == null) {
          expect(sent, isEmpty);
          // The service refuses any body framing where a method takes no body.
          expect(transferEncoding, isNull);
          expect(contentLength, anyOf(isNull, '0'));
        } else {
          expect(utf8.decode(sent), requestBody);
          // The media types the service's body reader accepts.
          expect(
            contentType,
            matches(
              RegExp(
                r'^application/json(\s*;\s*charset=utf-8)?$',
                caseSensitive: false,
              ),
            ),
          );
        }
        expect(result.statusCode, response['status']);
        expect(
          result.body,
          responseBody == null ? isNull : jsonDecode(responseBody),
        );
        final retryAfter = responseHeaders['retry-after'];
        expect(
          result.retryAfter,
          retryAfter == null
              ? isNull
              : Duration(seconds: int.parse(retryAfter)),
        );
      },
    );
  }

  test('a body canonical JSON cannot carry is refused before any client '
      'exists', () async {
    final overrides = CountingHttpOverrides();
    var deep = <String, Object?>{'leaf': 1};
    for (var depth = 0; depth < 16; depth++) {
      deep = {'next': deep};
    }
    for (final (about, body) in [
      ('a fraction', {'players': 2.5}),
      ('an integral double', {'players': 2.0}),
      ('an integer past 2^53 - 1', {'expiresAt': 9007199254740992}),
      ('a member name with a hyphen', {'relay-only': true}),
      ('an empty member name', {'': true}),
      ('an unpaired surrogate', {'name': String.fromCharCode(0xD800)}),
      ('nesting past 16 levels', deep),
      (
        'a set',
        {
          'codes': {'A'},
        },
      ),
    ]) {
      await expectLater(
        HttpOverrides.runWithHttpOverrides(
          () => requestHttpsJson(
            origin.replace(path: '/v1/join-tickets'),
            label: label,
            method: 'POST',
            body: body,
            maxResponseBytes: 1024,
          ),
          overrides,
        ),
        throwsA(
          isA<SecureHttpRequestException>().having(
            (error) => error.failure,
            'failure',
            SecureHttpFailure.invalidRequest,
          ),
        ),
        reason: about,
      );
    }
    expect(overrides.clientsCreated, 0);
  });
}
