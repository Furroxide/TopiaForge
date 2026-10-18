part of 'secure_http.dart';

/// Why [downloadHttpsToFile] refused a body. Network failures, timeouts and
/// refused redirects keep their own exceptions.
enum SecureHttpDownloadProblem {
  /// The answer was not a success.
  status,

  /// The body is larger than its bound: the size its source states, or the
  /// largest the caller accepts.
  tooLarge,

  /// The server announced another length than the size the source states.
  unexpectedLength,

  /// The body ended before its announced length or the size its source
  /// states.
  endedEarly,
}

/// A body [downloadHttpsToFile] refused. [message] names no host.
final class SecureHttpDownloadException implements Exception {
  const SecureHttpDownloadException(
    this.problem,
    this.message, {
    this.statusCode,
  });

  final SecureHttpDownloadProblem problem;
  final String message;

  /// The status of an answer that was not a success.
  final int? statusCode;

  @override
  String toString() => 'SecureHttpDownloadException: $message';
}

/// What [downloadHttpsToFile] wrote.
final class SecureHttpDownloadResult {
  const SecureHttpDownloadResult({
    required this.sha256,
    required this.length,
    required this.effectiveUri,
  });

  /// The lowercase hexadecimal SHA-256 of the body, computed as it streamed.
  final String sha256;
  final int length;

  /// The URL that answered, after the redirects.
  final Uri effectiveUri;
}

/// Streams an HTTPS resource into [target], computing its SHA-256 as it
/// arrives, under the redirect and URL rules of [fetchHttpsBytes].
///
/// The body is bounded by [maxBytes] and, when its source states a size, by
/// [expectedBytes]. An announced length past [maxBytes], or another than
/// [expectedBytes], is refused before the body is read; a body that grows
/// past its bound is refused as soon as it does; and a body that ends before
/// its announced length or [expectedBytes] is refused once it ends.
///
/// [onProgress] runs after each chunk. Cancelling [cancellation] closes the
/// connection at once and the call throws [OperationCancelledException];
/// passing [totalTimeout] closes it and throws [TimeoutException]. However
/// the download ends early, [target] is deleted before the call returns, so
/// nothing partial is left behind.
Future<SecureHttpDownloadResult> downloadHttpsToFile(
  Uri initialUri,
  File target, {
  required int maxBytes,
  int? expectedBytes,
  required String label,
  int maxRedirects = 5,
  Duration connectionTimeout = const Duration(seconds: 15),
  Duration responseTimeout = const Duration(seconds: 30),
  Duration totalTimeout = const Duration(minutes: 30),
  SecureHttpProgress? onProgress,
  CancellationToken? cancellation,
}) async {
  if (maxBytes < 0) {
    throw ArgumentError.value(maxBytes, 'maxBytes', 'must not be negative');
  }
  if (expectedBytes != null &&
      (expectedBytes < 0 || expectedBytes > maxBytes)) {
    throw ArgumentError.value(
      expectedBytes,
      'expectedBytes',
      'must be between 0 and maxBytes',
    );
  }
  if (maxRedirects < 0) {
    throw ArgumentError.value(
      maxRedirects,
      'maxRedirects',
      'must not be negative',
    );
  }
  cancellation?.throwIfCancelled();
  final uri = requirePublicHttpsUri(initialUri, label);

  final client = HttpClient()
    ..connectionTimeout = connectionTimeout
    ..autoUncompress = true;
  var stopped = false;
  var timedOut = false;
  void stop() {
    stopped = true;
    client.close(force: true);
  }

  final registration = cancellation?.register(stop);
  final timer = Timer(totalTimeout, () {
    timedOut = true;
    stop();
  });
  try {
    return await _downloadFollowingHttpsRedirects(
      client,
      uri,
      target,
      maxBytes: maxBytes,
      expectedBytes: expectedBytes,
      label: label,
      maxRedirects: maxRedirects,
      responseTimeout: responseTimeout,
      onProgress: onProgress,
      stopped: () => stopped,
    );
  } on Object {
    await _deleteDownloadQuietly(target);
    if (cancellation?.isCancelled ?? false) {
      throw const OperationCancelledException();
    }
    if (timedOut) {
      throw TimeoutException(
        '$label exceeded its ${totalTimeout.inSeconds}-second time limit.',
        totalTimeout,
      );
    }
    rethrow;
  } finally {
    timer.cancel();
    registration?.dispose();
    client.close(force: true);
  }
}

Future<SecureHttpDownloadResult> _downloadFollowingHttpsRedirects(
  HttpClient client,
  Uri initialUri,
  File target, {
  required int maxBytes,
  required int? expectedBytes,
  required String label,
  required int maxRedirects,
  required Duration responseTimeout,
  required SecureHttpProgress? onProgress,
  required bool Function() stopped,
}) async {
  final (response, current) = await _openFollowingHttpsRedirects(
    client,
    initialUri,
    label: label,
    maxRedirects: maxRedirects,
    responseTimeout: responseTimeout,
  );
  if (response.statusCode < 200 || response.statusCode >= 300) {
    throw SecureHttpDownloadException(
      SecureHttpDownloadProblem.status,
      '$label failed with HTTP ${response.statusCode}.',
      statusCode: response.statusCode,
    );
  }
  final announced = response.contentLength;
  if (announced > maxBytes) {
    throw SecureHttpDownloadException(
      SecureHttpDownloadProblem.tooLarge,
      '$label is larger than its $maxBytes-byte limit.',
    );
  }
  if (expectedBytes != null && announced >= 0 && announced != expectedBytes) {
    throw SecureHttpDownloadException(
      SecureHttpDownloadProblem.unexpectedLength,
      '$label is $announced bytes, and its source says $expectedBytes.',
    );
  }
  final limit = expectedBytes ?? maxBytes;
  final total = expectedBytes ?? (announced >= 0 ? announced : null);
  final digest = _DigestCapture();
  final hashing = sha256.startChunkedConversion(digest);
  final output = await target.open(mode: FileMode.write);
  var received = 0;
  try {
    await for (final chunk in response.timeout(responseTimeout)) {
      if (chunk.length > limit - received) {
        throw SecureHttpDownloadException(
          SecureHttpDownloadProblem.tooLarge,
          expectedBytes == null
              ? '$label is larger than its $maxBytes-byte limit.'
              : '$label is larger than the $expectedBytes bytes its source '
                    'says.',
        );
      }
      hashing.add(chunk);
      await output.writeFrom(chunk);
      received += chunk.length;
      onProgress?.call(received, total);
    }
    await output.flush();
  } finally {
    await output.close();
  }
  hashing.close();
  // A connection closed to stop the download can end its body as if it
  // were complete.
  if (stopped()) throw const OperationCancelledException();
  if ((announced >= 0 && received < announced) ||
      (expectedBytes != null && received < expectedBytes)) {
    throw SecureHttpDownloadException(
      SecureHttpDownloadProblem.endedEarly,
      '$label ended after $received bytes.',
    );
  }
  return SecureHttpDownloadResult(
    sha256: digest.value.toString(),
    length: received,
    effectiveUri: current,
  );
}

final class _DigestCapture implements Sink<Digest> {
  Digest? _value;

  Digest get value => _value!;

  @override
  void add(Digest data) => _value = data;

  @override
  void close() {}
}

Future<void> _deleteDownloadQuietly(File file) async {
  try {
    if (await FileSystemEntity.type(file.path, followLinks: false) ==
        FileSystemEntityType.file) {
      await file.delete();
    }
  } on FileSystemException {
    // A partial download that cannot be deleted just now is swept later.
  }
}
