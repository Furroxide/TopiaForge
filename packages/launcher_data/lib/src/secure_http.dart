import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:crypto/crypto.dart';
import 'package:launcher_domain/launcher_domain.dart'
    show CanonicalJson, CanonicalJsonException;

import 'cancellation_token.dart';
import 'public_url.dart';

part 'secure_http_json.dart';
part 'secure_http_download.dart';

/// Reports how many bytes of a response body have arrived, and the total
/// when the server announced one.
typedef SecureHttpProgress = void Function(int receivedBytes, int? totalBytes);

/// Result of a bounded HTTPS fetch. [effectiveUri] is the final trusted URI
/// after following validated redirects.
final class SecureHttpFetchResult {
  const SecureHttpFetchResult({
    required this.bytes,
    required this.effectiveUri,
  });

  final Uint8List bytes;
  final Uri effectiveUri;
}

/// Fetches an HTTPS resource with bounded redirects, bytes, and wall time.
///
/// Redirects are followed manually so an HTTPS endpoint can never downgrade
/// to plaintext HTTP. URL credentials are rejected to keep secrets out of
/// request lines, logs, proxy state, and redirect targets.
///
/// [onProgress] runs after each chunk of the final response's body.
/// [cancellation] aborts the transfer: a token cancelled before the call
/// opens no connection, and cancelling one in flight closes it at once. Either
/// way the call throws [OperationCancelledException].
Future<SecureHttpFetchResult> fetchHttpsBytes(
  Uri initialUri, {
  required int maxBytes,
  required String label,
  int maxRedirects = 5,
  Duration connectionTimeout = const Duration(seconds: 15),
  Duration responseTimeout = const Duration(seconds: 30),
  Duration totalTimeout = const Duration(minutes: 2),
  SecureHttpProgress? onProgress,
  CancellationToken? cancellation,
}) async {
  if (maxBytes < 0) {
    throw ArgumentError.value(maxBytes, 'maxBytes', 'must not be negative');
  }
  if (maxRedirects < 0) {
    throw ArgumentError.value(
      maxRedirects,
      'maxRedirects',
      'must not be negative',
    );
  }
  cancellation?.throwIfCancelled();

  final client = HttpClient()
    ..connectionTimeout = connectionTimeout
    ..autoUncompress = true;
  try {
    final operation = _fetchFollowingHttpsRedirects(
      client,
      requirePublicHttpsUri(initialUri, label),
      maxBytes: maxBytes,
      label: label,
      maxRedirects: maxRedirects,
      responseTimeout: responseTimeout,
      onProgress: onProgress,
    );
    return await _cancellable(
      operation.timeout(
        totalTimeout,
        onTimeout: () => throw TimeoutException(
          '$label exceeded its ${totalTimeout.inSeconds}-second time limit.',
          totalTimeout,
        ),
      ),
      cancellation,
      () => client.close(force: true),
    );
  } finally {
    client.close(force: true);
  }
}

Future<SecureHttpFetchResult> _fetchFollowingHttpsRedirects(
  HttpClient client,
  Uri initialUri, {
  required int maxBytes,
  required String label,
  required int maxRedirects,
  required Duration responseTimeout,
  SecureHttpProgress? onProgress,
}) async {
  final (response, current) = await _openFollowingHttpsRedirects(
    client,
    initialUri,
    label: label,
    maxRedirects: maxRedirects,
    responseTimeout: responseTimeout,
  );
  if (response.statusCode < 200 || response.statusCode >= 300) {
    throw HttpException(
      '$label failed with HTTP ${response.statusCode}.',
      uri: current.replace(query: '', fragment: ''),
    );
  }
  if (response.contentLength > maxBytes) {
    throw StateError('$label is larger than its $maxBytes-byte limit.');
  }

  final bytes = await _readBodyBounded(
    response,
    maxBytes: maxBytes,
    timeout: responseTimeout,
    onProgress: onProgress,
    tooLarge: () =>
        StateError('$label is larger than its $maxBytes-byte limit.'),
  );
  return SecureHttpFetchResult(bytes: bytes, effectiveUri: current);
}

/// Sends a GET for [initialUri] and follows its redirects by hand, each to
/// another HTTPS URL that [requirePublicHttpsUri] accepts and at most
/// [maxRedirects] of them, so a fetch can never downgrade to plain HTTP.
/// Returns the first answer that is not a redirect, whatever its status, and
/// the URL that gave it.
Future<(HttpClientResponse, Uri)> _openFollowingHttpsRedirects(
  HttpClient client,
  Uri initialUri, {
  required String label,
  required int maxRedirects,
  required Duration responseTimeout,
}) async {
  var current = initialUri;
  for (var redirectCount = 0; ; redirectCount++) {
    final request = await client.getUrl(current).timeout(responseTimeout);
    request
      ..followRedirects = false
      ..maxRedirects = 0;
    final response = await request.close().timeout(responseTimeout);
    if (!response.isRedirect) return (response, current);

    final location = response.headers.value(HttpHeaders.locationHeader);
    if (location == null || location.trim().isEmpty) {
      throw StateError('$label returned a redirect without a location.');
    }
    if (redirectCount >= maxRedirects) {
      throw StateError('$label exceeded its $maxRedirects-redirect limit.');
    }
    current = requirePublicHttpsUri(current.resolve(location), label);
    await _drainRedirectBodyBounded(
      response,
      label: label,
      timeout: responseTimeout,
    );
  }
}

/// Reads [response] whole, failing with what [tooLarge] makes as soon as the
/// body would pass [maxBytes], and reporting each chunk to [onProgress].
Future<Uint8List> _readBodyBounded(
  HttpClientResponse response, {
  required int maxBytes,
  required Duration timeout,
  required Object Function() tooLarge,
  SecureHttpProgress? onProgress,
}) async {
  final total = response.contentLength >= 0 ? response.contentLength : null;
  final bytes = BytesBuilder(copy: false);
  var length = 0;
  await for (final chunk in response.timeout(timeout)) {
    if (chunk.length > maxBytes - length) throw tooLarge();
    bytes.add(chunk);
    length += chunk.length;
    onProgress?.call(length, total);
  }
  return bytes.takeBytes();
}

Future<void> _drainRedirectBodyBounded(
  HttpClientResponse response, {
  required String label,
  required Duration timeout,
}) async {
  const maxRedirectBodyBytes = 64 * 1024;
  var received = 0;
  await for (final chunk in response.timeout(timeout)) {
    received += chunk.length;
    if (received > maxRedirectBodyBytes) {
      throw StateError('$label returned an oversized redirect response.');
    }
  }
}

/// Completes with [work], or fails with [OperationCancelledException] as soon
/// as [cancellation] is cancelled, and then calls [abort] to stop the work.
/// The abandoned work's own outcome is consumed, so it never surfaces as an
/// unhandled error.
Future<T> _cancellable<T>(
  Future<T> work,
  CancellationToken? cancellation,
  void Function() abort,
) {
  if (cancellation == null) return work;
  final completer = Completer<T>();
  final registration = cancellation.register(() {
    if (!completer.isCompleted) {
      completer.completeError(const OperationCancelledException());
    }
    try {
      abort();
    } on Object {
      // The work is abandoned either way.
    }
  });
  work.then(
    (value) {
      registration.dispose();
      if (!completer.isCompleted) completer.complete(value);
    },
    onError: (Object error, StackTrace stackTrace) {
      registration.dispose();
      if (!completer.isCompleted) completer.completeError(error, stackTrace);
    },
  );
  return completer.future;
}
