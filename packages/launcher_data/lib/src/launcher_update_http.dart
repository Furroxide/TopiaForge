import 'dart:async';
import 'dart:io';
import 'dart:typed_data';

import 'package:crypto/crypto.dart';

final class LauncherUpdateDownloadResult {
  const LauncherUpdateDownloadResult({
    required this.path,
    required this.size,
    required this.sha256,
  });

  final String path;
  final int size;
  final String sha256;
}

abstract interface class LauncherUpdateTransport {
  Future<Uint8List> fetch(
    Uri uri, {
    required int maxBytes,
    required String label,
  });

  Future<LauncherUpdateDownloadResult> download(
    Uri uri, {
    required File partialFile,
    required int expectedSize,
    required String expectedSha256,
    void Function(double progress)? onProgress,
  });

  void close();
}

final class SecureLauncherUpdateTransport implements LauncherUpdateTransport {
  SecureLauncherUpdateTransport({
    HttpClient Function()? clientFactory,
    this.maxRedirects = 5,
    this.responseTimeout = const Duration(seconds: 30),
    this.totalTimeout = const Duration(minutes: 10),
  }) : _clientFactory = clientFactory ?? HttpClient.new;

  final HttpClient Function() _clientFactory;
  final int maxRedirects;
  final Duration responseTimeout;
  final Duration totalTimeout;
  final Set<HttpClient> _clients = {};

  @override
  Future<Uint8List> fetch(
    Uri uri, {
    required int maxBytes,
    required String label,
  }) async {
    final client = _newClient();
    var timedOut = false;
    final totalTimer = Timer(totalTimeout, () {
      timedOut = true;
      client.close(force: true);
    });
    try {
      final response = await _open(client, uri, label: label);
      if (response.contentLength > maxBytes) {
        throw StateError('$label exceeds its $maxBytes-byte limit.');
      }
      final output = BytesBuilder(copy: false);
      var received = 0;
      await for (final chunk in response.timeout(responseTimeout)) {
        if (chunk.length > maxBytes - received) {
          throw StateError('$label exceeds its $maxBytes-byte limit.');
        }
        output.add(chunk);
        received += chunk.length;
      }
      return output.takeBytes();
    } on Object {
      if (timedOut) {
        throw TimeoutException(
          '$label exceeded its ${totalTimeout.inSeconds}-second time limit.',
        );
      }
      rethrow;
    } finally {
      totalTimer.cancel();
      _closeClient(client);
    }
  }

  @override
  Future<LauncherUpdateDownloadResult> download(
    Uri uri, {
    required File partialFile,
    required int expectedSize,
    required String expectedSha256,
    void Function(double progress)? onProgress,
  }) async {
    if (expectedSize <= 0 || expectedSize > 512 * 1024 * 1024) {
      throw StateError('Launcher update download size is invalid.');
    }
    if (!RegExp(r'^[0-9a-f]{64}$').hasMatch(expectedSha256)) {
      throw StateError('Launcher update download hash is invalid.');
    }
    final type = FileSystemEntity.typeSync(
      partialFile.path,
      followLinks: false,
    );
    if (type != FileSystemEntityType.notFound) {
      throw StateError('Launcher update partial path already exists.');
    }
    partialFile.parent.createSync(recursive: true);
    partialFile.createSync(exclusive: true);

    final client = _newClient();
    var timedOut = false;
    final totalTimer = Timer(totalTimeout, () {
      timedOut = true;
      client.close(force: true);
    });
    RandomAccessFile? output;
    try {
      final response = await _open(client, uri, label: 'Launcher update');
      if (response.contentLength >= 0 &&
          response.contentLength != expectedSize) {
        throw StateError('Launcher update Content-Length does not match.');
      }
      output = await partialFile.open(mode: FileMode.writeOnly);
      final digestSink = _DigestSink();
      final hashSink = sha256.startChunkedConversion(digestSink);
      var received = 0;
      await for (final chunk in response.timeout(responseTimeout)) {
        if (chunk.length > expectedSize - received) {
          throw StateError('Launcher update exceeded its signed size.');
        }
        await output.writeFrom(chunk);
        hashSink.add(chunk);
        received += chunk.length;
        onProgress?.call(received / expectedSize);
      }
      hashSink.close();
      await output.flush();
      await output.close();
      output = null;
      final actualHash = digestSink.value?.toString() ?? '';
      if (received != expectedSize || actualHash != expectedSha256) {
        throw StateError('Launcher update size or SHA-256 does not match.');
      }
      return LauncherUpdateDownloadResult(
        path: partialFile.path,
        size: received,
        sha256: actualHash,
      );
    } on Object {
      await output?.close();
      if (partialFile.existsSync()) {
        partialFile.deleteSync();
      }
      if (timedOut) {
        throw TimeoutException(
          'Launcher update exceeded its '
          '${totalTimeout.inSeconds}-second time limit.',
        );
      }
      rethrow;
    } finally {
      totalTimer.cancel();
      _closeClient(client);
    }
  }

  HttpClient _newClient() {
    final client = _clientFactory()
      ..connectionTimeout = const Duration(seconds: 15)
      ..autoUncompress = true;
    _clients.add(client);
    return client;
  }

  Future<HttpClientResponse> _open(
    HttpClient client,
    Uri initial, {
    required String label,
  }) {
    return _openFollowingRedirects(client, initial, label: label).timeout(
      totalTimeout,
      onTimeout: () => throw TimeoutException(
        '$label exceeded its ${totalTimeout.inSeconds}-second time limit.',
      ),
    );
  }

  Future<HttpClientResponse> _openFollowingRedirects(
    HttpClient client,
    Uri initial, {
    required String label,
  }) async {
    var current = _requireInitialUri(initial);
    for (var redirects = 0; ; redirects++) {
      final request = await client.getUrl(current).timeout(responseTimeout);
      request
        ..followRedirects = false
        ..maxRedirects = 0;
      request.headers
        ..set(HttpHeaders.userAgentHeader, 'TopiaForge-Updater/1')
        ..set(HttpHeaders.acceptHeader, 'application/vnd.github+json');
      final response = await request.close().timeout(responseTimeout);
      if (response.isRedirect) {
        if (redirects >= maxRedirects) {
          throw StateError('$label exceeded its redirect limit.');
        }
        final location = response.headers.value(HttpHeaders.locationHeader);
        if (location == null || location.trim().isEmpty) {
          throw StateError('$label returned a redirect without a location.');
        }
        await _drainBounded(response, label);
        current = _requireRedirectUri(current.resolve(location));
        continue;
      }
      if (response.statusCode < 200 || response.statusCode >= 300) {
        await _drainBounded(response, label);
        throw HttpException(
          '$label failed with HTTP ${response.statusCode}.',
          uri: current.replace(query: '', fragment: ''),
        );
      }
      return response;
    }
  }

  Uri _requireInitialUri(Uri uri) {
    _requireBaseHttps(uri);
    final isApi =
        uri.host == 'api.github.com' &&
        uri.path == '/repos/furroxide/TopiaForge/releases' &&
        uri.queryParameters.length == 1 &&
        uri.queryParameters['per_page'] == '20';
    final isReleaseAsset =
        uri.host == 'github.com' &&
        uri.path.startsWith('/furroxide/TopiaForge/releases/download/') &&
        !uri.hasQuery;
    if (!isApi && !isReleaseAsset) {
      throw StateError('Launcher updates use only fixed GitHub endpoints.');
    }
    return uri;
  }

  Uri _requireRedirectUri(Uri uri) {
    _requireBaseHttps(uri);
    final host = uri.host.toLowerCase();
    final allowed =
        host == 'github.com' ||
        host == 'objects.githubusercontent.com' ||
        host == 'release-assets.githubusercontent.com' ||
        host.endsWith('.githubusercontent.com');
    if (!allowed) {
      throw StateError('Launcher update redirected outside GitHub.');
    }
    return uri;
  }

  void _requireBaseHttps(Uri uri) {
    if (uri.toString().length > 8192 ||
        uri.scheme.toLowerCase() != 'https' ||
        uri.userInfo.isNotEmpty ||
        uri.host.isEmpty ||
        uri.hasFragment) {
      throw StateError('Launcher update URL is not trusted.');
    }
  }

  Future<void> _drainBounded(HttpClientResponse response, String label) async {
    var bytes = 0;
    await for (final chunk in response.timeout(responseTimeout)) {
      bytes += chunk.length;
      if (bytes > 64 * 1024) {
        throw StateError('$label returned an oversized response.');
      }
    }
  }

  void _closeClient(HttpClient client) {
    _clients.remove(client);
    client.close(force: true);
  }

  @override
  void close() {
    for (final client in _clients.toList()) {
      _closeClient(client);
    }
  }
}

final class _DigestSink implements Sink<Digest> {
  Digest? value;

  @override
  void add(Digest data) => value = data;

  @override
  void close() {}
}
