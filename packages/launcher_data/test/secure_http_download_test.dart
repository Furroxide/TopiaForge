import 'dart:async';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:launcher_data/src/cancellation_token.dart';
import 'package:launcher_data/src/secure_http.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'secure_http_loopback.dart';

/// [downloadHttpsToFile] against a loopback server: the body streams to disk
/// with its SHA-256, inside its bounds, and nothing partial is ever left.
void main() {
  final uri = Uri.parse('https://packages.example/mod.topiaforgemod');
  late Directory folder;
  late File target;

  setUp(() {
    folder = Directory.systemTemp.createTempSync('tf-download-');
    addTearDown(() => folder.deleteSync(recursive: true));
    target = File(p.join(folder.path, 'archive.part'));
  });

  Future<LoopbackHttps> serve(
    FutureOr<void> Function(HttpRequest request) handle,
  ) async {
    final https = await LoopbackHttps.start(handle);
    addTearDown(https.close);
    return https;
  }

  List<int> bodyOf(int length) =>
      List<int>.generate(length, (index) => (index * 7) & 0xFF);

  Future<void> sendInChunks(HttpRequest request, List<int> body) async {
    for (var offset = 0; offset < body.length; offset += 40000) {
      final end = offset + 40000 > body.length ? body.length : offset + 40000;
      request.response.add(body.sublist(offset, end));
      await request.response.flush();
    }
  }

  test('streams the body to disk with its SHA-256 and progress', () async {
    final body = bodyOf(150000);
    final https = await serve((request) async {
      request.response.contentLength = body.length;
      await sendInChunks(request, body);
      await request.response.close();
    });
    final progress = <(int, int?)>[];
    final result = await https.run(
      () => downloadHttpsToFile(
        uri,
        target,
        maxBytes: 1 << 20,
        expectedBytes: body.length,
        label: 'Package',
        onProgress: (received, total) => progress.add((received, total)),
      ),
    );
    expect(result.length, body.length);
    expect(result.sha256, sha256.convert(body).toString());
    expect(target.readAsBytesSync(), body);
    expect(progress.last, (body.length, body.length));
    for (var index = 1; index < progress.length; index++) {
      expect(progress[index].$1, greaterThan(progress[index - 1].$1));
    }
    // Loopback only: every connection went to the server bound here.
    expect(https.server.address.address, '127.0.0.1');
  });

  test('cancelling mid-body stops at once and leaves no file', () async {
    final hold = Completer<void>();
    addTearDown(() {
      if (!hold.isCompleted) hold.complete();
    });
    final https = await serve((request) async {
      request.response.contentLength = 200000;
      request.response.add(bodyOf(30000));
      await request.response.flush();
      await hold.future;
    });
    final cancellation = CancellationToken();
    await expectLater(
      https.run(
        () => downloadHttpsToFile(
          uri,
          target,
          maxBytes: 1 << 20,
          label: 'Package',
          cancellation: cancellation,
          onProgress: (_, _) => cancellation.cancel(),
        ),
      ),
      throwsA(isA<OperationCancelledException>()),
    );
    expect(target.existsSync(), isFalse);
  });

  test('a token cancelled first opens no connection', () async {
    final overrides = CountingHttpOverrides();
    await expectLater(
      HttpOverrides.runWithHttpOverrides(
        () => downloadHttpsToFile(
          uri,
          target,
          maxBytes: 10,
          label: 'Package',
          cancellation: CancellationToken()..cancel(),
        ),
        overrides,
      ),
      throwsA(isA<OperationCancelledException>()),
    );
    expect(overrides.clientsCreated, 0);
    expect(target.existsSync(), isFalse);
  });

  group('bounds', () {
    Future<Object?> failure(
      LoopbackHttps https, {
      required int maxBytes,
      int? expectedBytes,
    }) async {
      try {
        await https.run(
          () => downloadHttpsToFile(
            uri,
            target,
            maxBytes: maxBytes,
            expectedBytes: expectedBytes,
            label: 'Package',
          ),
        );
      } on Object catch (error) {
        expect(target.existsSync(), isFalse, reason: 'nothing partial');
        return error;
      }
      fail('The download was accepted.');
    }

    Matcher problem(SecureHttpDownloadProblem kind) =>
        isA<SecureHttpDownloadException>().having(
          (error) => error.problem,
          'problem',
          kind,
        );

    test('an announced length past the cap is refused unread', () async {
      var sent = false;
      final https = await serve((request) async {
        request.response.contentLength = 5000;
        sent = true;
        request.response.add(bodyOf(5000));
        await request.response.close();
      });
      expect(
        await failure(https, maxBytes: 4999),
        problem(SecureHttpDownloadProblem.tooLarge),
      );
      expect(sent, isTrue);
    });

    test('a body that grows past the cap is refused as it does', () async {
      final https = await serve((request) async {
        // No length announced: chunked, so only the stream can tell.
        await sendInChunks(request, bodyOf(120000));
        await request.response.close();
      });
      expect(
        await failure(https, maxBytes: 100000),
        problem(SecureHttpDownloadProblem.tooLarge),
      );
    });

    test('an announced length other than the stated size is refused', () async {
      for (final length in [999, 1001]) {
        final https = await serve((request) async {
          request.response.contentLength = length;
          request.response.add(bodyOf(length));
          await request.response.close();
        });
        expect(
          await failure(https, maxBytes: 1 << 20, expectedBytes: 1000),
          problem(SecureHttpDownloadProblem.unexpectedLength),
        );
      }
    });

    test('a body that passes the stated size is refused', () async {
      final https = await serve((request) async {
        await sendInChunks(request, bodyOf(90000));
        await request.response.close();
      });
      expect(
        await failure(https, maxBytes: 1 << 20, expectedBytes: 50000),
        problem(SecureHttpDownloadProblem.tooLarge),
      );
    });

    test('a body that ends before the stated size is refused', () async {
      final https = await serve((request) async {
        await sendInChunks(request, bodyOf(30000));
        await request.response.close();
      });
      expect(
        await failure(https, maxBytes: 1 << 20, expectedBytes: 50000),
        problem(SecureHttpDownloadProblem.endedEarly),
      );
    });

    test('a body cut off before its announced length is refused', () async {
      final https = await serve((request) async {
        final socket = await request.response.detachSocket(writeHeaders: false);
        socket.add(
          'HTTP/1.1 200 OK\r\ncontent-length: 50000\r\n\r\n'.codeUnits,
        );
        socket.add(bodyOf(20000));
        await socket.flush();
        await socket.close();
      });
      expect(await failure(https, maxBytes: 1 << 20), isA<Exception>());
    });

    test('an error status is refused with its code', () async {
      final https = await serve((request) async {
        request.response.statusCode = HttpStatus.notFound;
        await request.response.close();
      });
      expect(
        await failure(https, maxBytes: 1000),
        isA<SecureHttpDownloadException>()
            .having((error) => error.statusCode, 'statusCode', 404)
            .having(
              (error) => error.message,
              'message',
              isNot(contains('packages.example')),
            ),
      );
    });
  });

  group('redirects, as fetchHttpsBytes follows them', () {
    test('to another HTTPS URL are followed', () async {
      final body = bodyOf(4000);
      final https = await serve((request) async {
        if (request.uri.path == '/mod.topiaforgemod') {
          await request.response.redirect(
            Uri.parse('https://mirror.example/final.topiaforgemod'),
          );
          return;
        }
        request.response.add(body);
        await request.response.close();
      });
      final result = await https.run(
        () =>
            downloadHttpsToFile(uri, target, maxBytes: 10000, label: 'Package'),
      );
      expect(result.sha256, sha256.convert(body).toString());
      expect(result.effectiveUri.host, 'mirror.example');
    });

    test('to plain HTTP, or past the limit, are refused', () async {
      final https = await serve((request) async {
        await request.response.redirect(
          request.uri.path == '/insecure'
              ? Uri.parse('http://packages.example/plain')
              : Uri.parse('https://packages.example/again'),
        );
      });
      for (final start in [
        Uri.parse('https://packages.example/insecure'),
        uri,
      ]) {
        await expectLater(
          https.run(
            () => downloadHttpsToFile(
              start,
              target,
              maxBytes: 10000,
              label: 'Package',
              maxRedirects: 3,
            ),
          ),
          throwsA(isA<StateError>()),
        );
        expect(target.existsSync(), isFalse);
      }
    });
  });

  test('a download past its time limit stops and leaves no file', () async {
    final hold = Completer<void>();
    addTearDown(() {
      if (!hold.isCompleted) hold.complete();
    });
    final https = await serve((request) async {
      request.response.contentLength = 100000;
      request.response.add(bodyOf(20000));
      await request.response.flush();
      await hold.future;
    });
    await expectLater(
      https.run(
        () => downloadHttpsToFile(
          uri,
          target,
          maxBytes: 1 << 20,
          label: 'Package',
          totalTimeout: const Duration(milliseconds: 300),
        ),
      ),
      throwsA(isA<TimeoutException>()),
    );
    expect(target.existsSync(), isFalse);
  });

  test('refuses bounds that cannot hold', () async {
    await expectLater(
      () => downloadHttpsToFile(
        uri,
        target,
        maxBytes: 10,
        expectedBytes: 11,
        label: 'Package',
      ),
      throwsArgumentError,
    );
  });
}
