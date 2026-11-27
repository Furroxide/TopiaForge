import 'dart:convert';
import 'dart:async';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  final json = utf8.encode(
    '{"name":"Courtyard","scenes":{"scene":{"entities":{"entry":{"components":{"spawn-location":{}}}}}}}',
  );
  test('packed identity survives bounded decompression and survey', () {
    final bytes = gzip.encode(json);
    final survey = MapProjectSurvey.read(bytes);
    expect(survey.sha256, sha256.convert(bytes).toString());
    expect(survey.packedSize, bytes.length);
    expect(survey.document.select('scene').spawns.keys, ['entry']);
    final snapshot = survey.snapshot(
      MapReference.parse('example'),
      survey.document.select('scene'),
    );
    expect(snapshot['map'], 'example');
    expect(snapshot['sha256'], survey.sha256);
    expect(snapshot.containsKey('url'), isFalse);
  });
  test('compressed bombs, damaged streams and packed overflow refuse', () {
    for (final bytes in [
      <int>[],
      [31, 139, 0],
      List.filled(MapWorldDocument.maximumPackedBytes + 1, 0),
      gzip.encode(List.filled(MapWorldDocument.maximumJsonBytes + 1, 0)),
    ]) {
      expect(() => MapProjectSurvey.read(bytes), throwsFormatException);
    }
  });
  test(
    'authoring transport pins HTTPS, disables redirects and closes on every outcome',
    () async {
      for (final status in [200, 302, 404, 410, 500]) {
        final client = _Client(_Response(status, json.length, json));
        final pending = MapProjectReader(
          createClient: () => client,
        ).read(MapReference.parse('example'));
        if (status == 200) {
          expect((await pending).document.name, 'Courtyard');
        } else {
          await expectLater(pending, throwsStateError);
        }
        expect(
          client.uri,
          Uri.parse('https://ugc.tomatocake.dev/example/project.roboworld'),
        );
        expect(client.autoUncompress, false);
        expect(client.request.followRedirects, false);
        expect(client.closed, true);
      }
    },
  );
  test(
    'authoring transport refuses lying lengths and hides network diagnostics',
    () async {
      final client = _Client(_Response(200, json.length + 1, json));
      await expectLater(
        MapProjectReader(
          createClient: () => client,
        ).read(MapReference.parse('example')),
        throwsStateError,
      );
      final failed = _Client(_Response(200, json.length, json))..fail = true;
      await expectLater(
        MapProjectReader(
          createClient: () => failed,
        ).read(MapReference.parse('example')),
        throwsA(
          isA<StateError>().having(
            (e) => e.message,
            'fixed failure',
            isNot(contains('private')),
          ),
        ),
      );
      expect(failed.closed, true);
    },
  );
}

class _Client implements HttpClient {
  _Client(_Response response) : request = _Request(response);
  final _Request request;
  Uri? uri;
  bool closed = false, fail = false;
  @override
  bool autoUncompress = true;
  @override
  Future<HttpClientRequest> getUrl(Uri value) async {
    uri = value;
    if (fail) {
      throw const SocketException('private backend diagnostic');
    }
    return request;
  }

  @override
  void close({bool force = false}) {
    closed = force;
  }

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

class _Request implements HttpClientRequest {
  _Request(this.response);
  final _Response response;
  @override
  bool followRedirects = true;
  @override
  Future<HttpClientResponse> close() async => response;
  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}

class _Response extends Stream<List<int>> implements HttpClientResponse {
  _Response(this.statusCode, this.contentLength, this.bytes);
  @override
  final int statusCode;
  @override
  final int contentLength;
  final List<int> bytes;
  @override
  StreamSubscription<List<int>> listen(
    void Function(List<int>)? onData, {
    Function? onError,
    void Function()? onDone,
    bool? cancelOnError,
  }) => Stream.value(bytes).listen(
    onData,
    onError: onError,
    onDone: onDone,
    cancelOnError: cancelOnError,
  );
  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}
