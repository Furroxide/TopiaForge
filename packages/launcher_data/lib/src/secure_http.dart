import 'dart:async';
import 'dart:io';
import 'dart:typed_data';

import 'public_url.dart';

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
Future<SecureHttpFetchResult> fetchHttpsBytes(
  Uri initialUri, {
  required int maxBytes,
  required String label,
  int maxRedirects = 5,
  Duration connectionTimeout = const Duration(seconds: 15),
  Duration responseTimeout = const Duration(seconds: 30),
  Duration totalTimeout = const Duration(minutes: 2),
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
    );
    return await operation.timeout(
      totalTimeout,
      onTimeout: () => throw TimeoutException(
        '$label exceeded its ${totalTimeout.inSeconds}-second time limit.',
        totalTimeout,
      ),
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
}) async {
  var current = initialUri;
  for (var redirectCount = 0; ; redirectCount++) {
    final request = await client.getUrl(current).timeout(responseTimeout);
    request
      ..followRedirects = false
      ..maxRedirects = 0;
    final response = await request.close().timeout(responseTimeout);

    if (response.isRedirect) {
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
      continue;
    }

    if (response.statusCode < 200 || response.statusCode >= 300) {
      throw HttpException(
        '$label failed with HTTP ${response.statusCode}.',
        uri: current.replace(query: '', fragment: ''),
      );
    }
    if (response.contentLength > maxBytes) {
      throw StateError('$label is larger than its $maxBytes-byte limit.');
    }

    final bytes = BytesBuilder(copy: false);
    var length = 0;
    await for (final chunk in response.timeout(responseTimeout)) {
      if (chunk.length > maxBytes - length) {
        throw StateError('$label is larger than its $maxBytes-byte limit.');
      }
      bytes.add(chunk);
      length += chunk.length;
    }
    return SecureHttpFetchResult(
      bytes: bytes.takeBytes(),
      effectiveUri: current,
    );
  }
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
