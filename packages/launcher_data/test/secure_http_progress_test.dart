import 'dart:async';
import 'dart:io';

import 'package:launcher_data/src/cancellation_token.dart';
import 'package:launcher_data/src/secure_http.dart';
import 'package:test/test.dart';

import 'secure_http_loopback.dart';

/// Progress and cancellation for [fetchHttpsBytes], against a loopback
/// server; callers that pass neither keep exactly the old behaviour.
void main() {
  final uri = Uri.parse('https://packages.example/mod.zip');

  Future<LoopbackHttps> serve(
    FutureOr<void> Function(HttpRequest request) handle,
  ) async {
    final https = await LoopbackHttps.start(handle);
    addTearDown(https.close);
    return https;
  }

  test('reports progress for each chunk, with the announced total', () async {
    final body = List<int>.generate(200000, (index) => index & 0xFF);
    final https = await serve((request) async {
      request.response.contentLength = body.length;
      for (var offset = 0; offset < body.length; offset += 50000) {
        request.response.add(body.sublist(offset, offset + 50000));
        await request.response.flush();
      }
      await request.response.close();
    });
    final progress = <(int, int?)>[];
    final result = await https.run(
      () => fetchHttpsBytes(
        uri,
        maxBytes: body.length,
        label: 'Package',
        onProgress: (received, total) => progress.add((received, total)),
      ),
    );
    expect(result.bytes, body);
    expect(progress.last, (body.length, body.length));
    for (var index = 1; index < progress.length; index++) {
      expect(progress[index].$1, greaterThan(progress[index - 1].$1));
    }
    expect(progress.every((item) => item.$2 == body.length), isTrue);
  });

  test('progress without an announced length has no total', () async {
    final https = await serve((request) async {
      request.response.add(List.filled(3000, 7));
      await request.response.close();
    });
    final progress = <(int, int?)>[];
    await https.run(
      () => fetchHttpsBytes(
        uri,
        maxBytes: 3000,
        label: 'Package',
        onProgress: (received, total) => progress.add((received, total)),
      ),
    );
    expect(progress.last, (3000, null));
  });

  test('a token cancelled before the call creates no client', () async {
    final overrides = CountingHttpOverrides();
    await expectLater(
      HttpOverrides.runWithHttpOverrides(
        () => fetchHttpsBytes(
          uri,
          maxBytes: 10,
          label: 'Package',
          cancellation: CancellationToken()..cancel(),
        ),
        overrides,
      ),
      throwsA(isA<OperationCancelledException>()),
    );
    expect(overrides.clientsCreated, 0);
  });

  test('cancelling mid-transfer stops it at once', () async {
    final hold = Completer<void>();
    addTearDown(() {
      if (!hold.isCompleted) hold.complete();
    });
    final https = await serve((request) async {
      // More than the server's output buffer, so the chunk leaves at once.
      request.response.contentLength = 100000;
      request.response.add(List.filled(20000, 1));
      await request.response.flush();
      await hold.future;
    });
    final cancellation = CancellationToken();
    await expectLater(
      https.run(
        () => fetchHttpsBytes(
          uri,
          maxBytes: 100000,
          label: 'Package',
          cancellation: cancellation,
          onProgress: (_, _) => cancellation.cancel(),
        ),
      ),
      throwsA(isA<OperationCancelledException>()),
    );
  });

  test('a finished transfer ignores a later cancel', () async {
    final https = await serve((request) async {
      request.response.write('done');
      await request.response.close();
    });
    final cancellation = CancellationToken();
    final result = await https.run(
      () => fetchHttpsBytes(
        uri,
        maxBytes: 10,
        label: 'Package',
        cancellation: cancellation,
      ),
    );
    cancellation.cancel();
    expect(String.fromCharCodes(result.bytes), 'done');
  });

  test('without the new arguments it behaves as before', () async {
    var finalRequests = 0;
    final https = await serve((request) async {
      switch (request.uri.path) {
        case '/missing':
          request.response.statusCode = HttpStatus.notFound;
        case '/big':
          request.response.contentLength = 2000;
          request.response.add(List.filled(2000, 0));
        case '/redirect':
          await request.response.redirect(
            Uri.parse('https://packages.example/final'),
          );
          return;
        case '/final':
          finalRequests++;
          request.response.write('ok');
      }
      await request.response.close();
    });
    await https.run(() async {
      await expectLater(
        fetchHttpsBytes(
          uri.replace(path: '/missing'),
          maxBytes: 1000,
          label: 'Package',
        ),
        throwsA(isA<HttpException>()),
      );
      await expectLater(
        fetchHttpsBytes(
          uri.replace(path: '/big'),
          maxBytes: 1000,
          label: 'Package',
        ),
        throwsStateError,
      );
      final redirected = await fetchHttpsBytes(
        uri.replace(path: '/redirect'),
        maxBytes: 1000,
        label: 'Package',
      );
      expect(redirected.effectiveUri.path, '/final');
      expect(String.fromCharCodes(redirected.bytes), 'ok');
    });
    expect(finalRequests, 1);
  });
}
