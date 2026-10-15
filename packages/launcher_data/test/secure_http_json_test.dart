import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/src/cancellation_token.dart';
import 'package:launcher_data/src/secure_http.dart';
import 'package:test/test.dart';

import 'secure_http_loopback.dart';

/// The bounded JSON request helper the control-plane client will use,
/// against a loopback server.
void main() {
  final base = Uri.parse('https://service.example/v1/servers');
  const label = 'The multiplayer service';

  Future<LoopbackHttps> serve(
    FutureOr<void> Function(HttpRequest request) handle,
  ) async {
    final https = await LoopbackHttps.start(handle);
    addTearDown(https.close);
    return https;
  }

  Future<void> json(HttpRequest request, Object? body, {int status = 200}) {
    request.response
      ..statusCode = status
      ..headers.contentType = ContentType.json
      ..write(jsonEncode(body));
    return request.response.close();
  }

  Matcher fails(SecureHttpFailure failure) => throwsA(
    isA<SecureHttpRequestException>().having(
      (error) => error.failure,
      'failure',
      failure,
    ),
  );

  test('sends a GET with its query and headers, and reads JSON', () async {
    final seen = <String, String?>{};
    final https = await serve((request) {
      seen['method'] = request.method;
      seen['uri'] = request.uri.toString();
      seen['accept'] = request.headers.value('accept');
      seen['authorization'] = request.headers.value('authorization');
      return json(request, {'servers': <Object?>[], 'next': null});
    });
    final response = await https.run(
      () => requestHttpsJson(
        base,
        label: label,
        maxResponseBytes: 4096,
        query: {'build': '2545', 'protocol': '1'},
        headers: {'authorization': 'Bearer abc'},
      ),
    );
    expect(response.statusCode, 200);
    expect(response.isSuccess, isTrue);
    expect(response.body, {'servers': <Object?>[], 'next': null});
    expect(seen, {
      'method': 'GET',
      'uri': '/v1/servers?build=2545&protocol=1',
      'accept': 'application/json',
      'authorization': 'Bearer abc',
    });
  });

  test('POSTs a JSON body and reports progress', () async {
    String? contentType;
    final https = await serve((request) async {
      final text = await utf8.decodeStream(request);
      contentType = request.headers.contentType?.toString();
      return json(request, {'echo': jsonDecode(text)}, status: 201);
    });
    final progress = <int>[];
    final response = await https.run(
      () => requestHttpsJson(
        base,
        label: label,
        method: 'POST',
        body: {'name': "Charl's Sandbox", 'players': 3},
        maxResponseBytes: 4096,
        onProgress: (received, _) => progress.add(received),
      ),
    );
    expect(response.statusCode, 201);
    expect(contentType, 'application/json; charset=utf-8');
    expect(response.body, {
      'echo': {'name': "Charl's Sandbox", 'players': 3},
    });
    expect(progress.last, utf8.encode(jsonEncode(response.body)).length);
  });

  test('returns refusals with their status, body and retry delay', () async {
    final https = await serve((request) async {
      if (request.uri.path.endsWith('/limited')) {
        request.response.headers.set('retry-after', '30');
        return json(request, {'error': 'rate-limited'}, status: 429);
      }
      request.response
        ..statusCode = 502
        ..headers.contentType = ContentType.html
        ..write('<html>bad gateway</html>');
      await request.response.close();
    });
    await https.run(() async {
      final limited = await requestHttpsJson(
        base.replace(path: '/v1/limited'),
        label: label,
        maxResponseBytes: 4096,
      );
      expect(limited.statusCode, 429);
      expect(limited.isSuccess, isFalse);
      expect(limited.retryAfter, const Duration(seconds: 30));
      expect(limited.body, {'error': 'rate-limited'});
      final gateway = await requestHttpsJson(
        base,
        label: label,
        maxResponseBytes: 4096,
      );
      expect(gateway.statusCode, 502);
      expect(gateway.body, isNull);
      expect(gateway.retryAfter, isNull);
    });
  });

  test('a repeated Retry-After gives no delay, rather than failing', () async {
    final https = await serve((request) {
      // Two header lines, as a proxy can add one of its own.
      request.response.headers
        ..noFolding('retry-after')
        ..add('retry-after', '30')
        ..add('retry-after', '600');
      return json(request, {'error': 'rate-limited'}, status: 429);
    });
    final limited = await https.run(
      () => requestHttpsJson(base, label: label, maxResponseBytes: 4096),
    );
    expect(limited.statusCode, 429);
    expect(limited.body, {'error': 'rate-limited'});
    expect(limited.retryAfter, isNull);
  });

  test('never follows a redirect', () async {
    var followed = 0;
    final https = await serve((request) async {
      if (request.uri.path == '/elsewhere') {
        followed++;
        return json(request, {});
      }
      await request.response.redirect(
        Uri.parse('https://service.example/elsewhere'),
      );
    });
    await expectLater(
      https.run(
        () => requestHttpsJson(base, label: label, maxResponseBytes: 4096),
      ),
      fails(SecureHttpFailure.redirect),
    );
    expect(followed, 0);
  });

  test('refuses a response past its bound, announced or not', () async {
    final https = await serve((request) async {
      if (request.uri.path.endsWith('/announced')) {
        request.response.contentLength = 5000;
      }
      request.response
        ..headers.contentType = ContentType.json
        ..write(jsonEncode('x' * 4998));
      await request.response.close();
    });
    await https.run(() async {
      for (final path in ['/v1/announced', '/v1/chunked']) {
        await expectLater(
          requestHttpsJson(
            base.replace(path: path),
            label: label,
            maxResponseBytes: 1000,
          ),
          fails(SecureHttpFailure.tooLarge),
          reason: path,
        );
      }
    });
  });

  test('refuses a successful answer that is not JSON', () async {
    final https = await serve((request) async {
      final html = request.uri.path.endsWith('/html');
      request.response
        ..headers.contentType = html ? ContentType.html : ContentType.json
        ..write(html ? '<html></html>' : '{"servers": [');
      await request.response.close();
    });
    await https.run(() async {
      for (final path in ['/v1/html', '/v1/truncated']) {
        await expectLater(
          requestHttpsJson(
            base.replace(path: path),
            label: label,
            maxResponseBytes: 4096,
          ),
          fails(SecureHttpFailure.invalidResponse),
          reason: path,
        );
      }
    });
  });

  test('refuses a bad request before any client exists', () async {
    final overrides = CountingHttpOverrides();
    final cases = <String, Future<Object?> Function()>{
      'plain HTTP': () => requestHttpsJson(
        Uri.parse('http://service.example/v1'),
        label: label,
        maxResponseBytes: 10,
      ),
      'credentials': () => requestHttpsJson(
        Uri.parse('https://user:secret@service.example/v1'),
        label: label,
        maxResponseBytes: 10,
      ),
      'a query in the URL': () => requestHttpsJson(
        Uri.parse('https://service.example/v1?token=secret'),
        label: label,
        maxResponseBytes: 10,
      ),
      'a query key that is not plain': () => requestHttpsJson(
        base,
        label: label,
        maxResponseBytes: 10,
        query: {'bad key': '1'},
      ),
      'a query value with a newline': () => requestHttpsJson(
        base,
        label: label,
        maxResponseBytes: 10,
        query: {'build': '2545\n'},
      ),
      'too many query parameters': () => requestHttpsJson(
        base,
        label: label,
        maxResponseBytes: 10,
        query: {for (var index = 0; index < 17; index++) 'k$index': '1'},
      ),
      'a reserved header': () => requestHttpsJson(
        base,
        label: label,
        maxResponseBytes: 10,
        headers: {'host': 'elsewhere.example'},
      ),
      'a header name in capitals': () => requestHttpsJson(
        base,
        label: label,
        maxResponseBytes: 10,
        headers: {'Authorization': 'x'},
      ),
      'a header value that splits lines': () => requestHttpsJson(
        base,
        label: label,
        maxResponseBytes: 10,
        headers: {'authorization': 'x\r\nhost: elsewhere.example'},
      ),
      'an unsupported method': () => requestHttpsJson(
        base,
        label: label,
        maxResponseBytes: 10,
        method: 'PATCH',
      ),
      'a body on a GET': () => requestHttpsJson(
        base,
        label: label,
        maxResponseBytes: 10,
        body: {'a': 1},
      ),
      'a body that is not JSON': () => requestHttpsJson(
        base,
        label: label,
        maxResponseBytes: 10,
        method: 'POST',
        body: Object(),
      ),
      'a body past its bound': () => requestHttpsJson(
        base,
        label: label,
        maxResponseBytes: 10,
        method: 'POST',
        body: 'x' * 100,
        maxRequestBytes: 64,
      ),
    };
    for (final MapEntry(key: about, value: request) in cases.entries) {
      await expectLater(
        HttpOverrides.runWithHttpOverrides(request, overrides),
        fails(SecureHttpFailure.invalidRequest),
        reason: about,
      );
    }
    expect(overrides.clientsCreated, 0);
  });

  test('a service that cannot be reached fails without naming it', () async {
    final overrides = CountingHttpOverrides();
    final error = await HttpOverrides.runWithHttpOverrides(
      () => requestHttpsJson(base, label: label, maxResponseBytes: 10),
      overrides,
    ).then<Object?>((_) => null, onError: (Object error) => error);
    expect(
      error,
      isA<SecureHttpRequestException>().having(
        (error) => error.failure,
        'failure',
        SecureHttpFailure.network,
      ),
    );
    expect(error.toString(), isNot(contains('service.example')));
  });

  test('a service that never answers times out', () async {
    final hold = Completer<void>();
    addTearDown(() {
      if (!hold.isCompleted) hold.complete();
    });
    final https = await serve((_) => hold.future);
    await expectLater(
      https.run(
        () => requestHttpsJson(
          base,
          label: label,
          maxResponseBytes: 10,
          responseTimeout: const Duration(milliseconds: 200),
        ),
      ),
      fails(SecureHttpFailure.timeout),
    );
  });

  test('cancellation before a request creates no client, and during one '
      'stops it', () async {
    final overrides = CountingHttpOverrides();
    await expectLater(
      HttpOverrides.runWithHttpOverrides(
        () => requestHttpsJson(
          base,
          label: label,
          maxResponseBytes: 10,
          cancellation: CancellationToken()..cancel(),
        ),
        overrides,
      ),
      throwsA(isA<OperationCancelledException>()),
    );
    expect(overrides.clientsCreated, 0);

    final arrived = Completer<void>();
    final hold = Completer<void>();
    addTearDown(() {
      if (!hold.isCompleted) hold.complete();
    });
    final https = await serve((_) {
      arrived.complete();
      return hold.future;
    });
    final cancellation = CancellationToken();
    final pending = https.run(
      () => requestHttpsJson(
        base,
        label: label,
        maxResponseBytes: 10,
        cancellation: cancellation,
      ),
    );
    await arrived.future;
    cancellation.cancel();
    await expectLater(pending, throwsA(isA<OperationCancelledException>()));
  });
}
