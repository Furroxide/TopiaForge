part of 'secure_http.dart';

/// Why [requestHttpsJson] produced no response.
enum SecureHttpFailure {
  /// The request itself was refused before anything was contacted: its URL,
  /// query, headers, method or body broke a rule.
  invalidRequest,

  /// The service did not answer within a time limit.
  timeout,

  /// The service could not be reached, or the connection failed.
  network,

  /// The service answered with a redirect, which a JSON API never follows.
  redirect,

  /// The response was larger than its limit.
  tooLarge,

  /// The response was not JSON where JSON was required.
  invalidResponse,
}

/// A failed [requestHttpsJson]. [message] names the request's label and never
/// a URL, host name or network address, so it is safe to show and log.
final class SecureHttpRequestException implements Exception {
  const SecureHttpRequestException(this.failure, this.message);

  final SecureHttpFailure failure;
  final String message;

  @override
  String toString() => 'SecureHttpRequestException(${failure.name}): $message';
}

/// A bounded response from [requestHttpsJson], whatever its status.
final class SecureHttpJsonResponse {
  const SecureHttpJsonResponse({
    required this.statusCode,
    required this.body,
    this.retryAfter,
  });

  final int statusCode;

  /// The decoded JSON body; null for an empty body, and for a non-JSON body
  /// of an unsuccessful response, whose status alone is the answer.
  final Object? body;

  /// The `Retry-After` delay in whole seconds, when the service sent one
  /// such header, of at most a day.
  final Duration? retryAfter;

  bool get isSuccess => statusCode >= 200 && statusCode < 300;
}

const _jsonMethods = {'GET', 'POST', 'PUT', 'DELETE'};
const _maximumQueryParameters = 16;
const _maximumHeaders = 16;
const _maximumHeaderValueLength = 4096;
const _maximumRetryAfterSeconds = 24 * 60 * 60;

/// Headers the helper sets itself or that would change how the connection
/// behaves; callers may not supply them.
const _reservedHeaders = {
  'accept',
  'connection',
  'content-length',
  'content-type',
  'cookie',
  'expect',
  'host',
  'keep-alive',
  'proxy-authorization',
  'te',
  'trailer',
  'transfer-encoding',
  'upgrade',
};

final _queryKeyPattern = RegExp(r'^[A-Za-z0-9._~-]{1,64}$');
final _headerNamePattern = RegExp(r"^[a-z0-9!#$%&'*+.^_`|~-]{1,64}$");

/// Sends one bounded JSON request to an HTTPS service and reads its answer.
///
/// - Everything is checked before a socket opens: [uri] is an absolute HTTPS
///   URL without credentials, query or fragment; [query] holds at most 16
///   parameters with plain keys and printable ASCII values; [headers] holds at
///   most 16 lowercase names outside the reserved set, with printable ASCII
///   values; [body], for POST and PUT only, is written as canonical JSON of
///   at most [maxRequestBytes] bytes, the only form the service reads: its
///   members in ordinal order, integers only, and member names of 1 to 64
///   ASCII letters and digits.
/// - Redirects are never followed. The response body is read up to
///   [maxResponseBytes], reporting each chunk to [onProgress].
/// - Any status is returned with its decoded body, so the caller decides what
///   a refusal means. A successful response must be JSON or empty.
/// - [cancellation] works as for [fetchHttpsBytes].
///
/// Throws [SecureHttpRequestException] when no response could be read, or
/// [OperationCancelledException]. No message names the service's address.
Future<SecureHttpJsonResponse> requestHttpsJson(
  Uri uri, {
  required String label,
  required int maxResponseBytes,
  String method = 'GET',
  Map<String, String> query = const {},
  Object? body,
  Map<String, String> headers = const {},
  int maxRequestBytes = 64 * 1024,
  Duration connectionTimeout = const Duration(seconds: 10),
  Duration responseTimeout = const Duration(seconds: 15),
  Duration totalTimeout = const Duration(seconds: 30),
  SecureHttpProgress? onProgress,
  CancellationToken? cancellation,
}) async {
  if (maxResponseBytes < 0) {
    throw ArgumentError.value(
      maxResponseBytes,
      'maxResponseBytes',
      'must not be negative',
    );
  }
  if (maxRequestBytes < 0) {
    throw ArgumentError.value(
      maxRequestBytes,
      'maxRequestBytes',
      'must not be negative',
    );
  }
  final target = _jsonTarget(uri, query, label);
  final payload = _jsonPayload(method, body, maxRequestBytes, label);
  final extraHeaders = _jsonHeaders(headers, label);
  cancellation?.throwIfCancelled();

  final client = HttpClient()
    ..connectionTimeout = connectionTimeout
    ..autoUncompress = true;
  try {
    final work = _sendJson(
      client,
      target,
      method: method,
      payload: payload,
      headers: extraHeaders,
      label: label,
      maxResponseBytes: maxResponseBytes,
      responseTimeout: responseTimeout,
      onProgress: onProgress,
    ).timeout(totalTimeout);
    return await _cancellable(work, cancellation, () {
      client.close(force: true);
    });
  } on TimeoutException {
    throw SecureHttpRequestException(
      SecureHttpFailure.timeout,
      '$label did not answer in time.',
    );
  } on IOException {
    if (cancellation?.isCancelled ?? false) {
      throw const OperationCancelledException();
    }
    throw SecureHttpRequestException(
      SecureHttpFailure.network,
      '$label could not be reached.',
    );
  } finally {
    client.close(force: true);
  }
}

Future<SecureHttpJsonResponse> _sendJson(
  HttpClient client,
  Uri target, {
  required String method,
  required List<int>? payload,
  required Map<String, String> headers,
  required String label,
  required int maxResponseBytes,
  required Duration responseTimeout,
  SecureHttpProgress? onProgress,
}) async {
  final request = await client.openUrl(method, target).timeout(responseTimeout);
  request
    ..followRedirects = false
    ..maxRedirects = 0
    ..persistentConnection = false;
  request.headers.set(HttpHeaders.acceptHeader, 'application/json');
  headers.forEach(request.headers.set);
  if (payload != null) {
    request.headers.contentType = ContentType(
      'application',
      'json',
      charset: 'utf-8',
    );
    request.contentLength = payload.length;
    request.add(payload);
  }
  final response = await request.close().timeout(responseTimeout);
  final status = response.statusCode;
  if (response.isRedirect || (status >= 300 && status < 400)) {
    await _drainRedirectBodyBounded(
      response,
      label: label,
      timeout: responseTimeout,
    ).catchError((Object _) {});
    throw SecureHttpRequestException(
      SecureHttpFailure.redirect,
      '$label answered with a redirect.',
    );
  }
  if (status < 200 || status > 599) {
    throw SecureHttpRequestException(
      SecureHttpFailure.invalidResponse,
      '$label answered with an unexpected status.',
    );
  }
  final tooLarge = SecureHttpRequestException(
    SecureHttpFailure.tooLarge,
    '$label sent more than $maxResponseBytes bytes.',
  );
  if (response.contentLength > maxResponseBytes) throw tooLarge;
  final bytes = await _readBodyBounded(
    response,
    maxBytes: maxResponseBytes,
    timeout: responseTimeout,
    onProgress: onProgress,
    tooLarge: () => tooLarge,
  );
  return SecureHttpJsonResponse(
    statusCode: status,
    body: _decodeJsonBody(response, bytes, label),
    // `value` throws on a repeated header, so the list is read instead.
    retryAfter: _retryAfter(response.headers['retry-after']),
  );
}

Object? _decodeJsonBody(
  HttpClientResponse response,
  Uint8List bytes,
  String label,
) {
  if (bytes.isEmpty) return null;
  final success = response.statusCode >= 200 && response.statusCode < 300;
  final mimeType = response.headers.contentType?.mimeType.toLowerCase() ?? '';
  final json = mimeType == 'application/json' || mimeType.endsWith('+json');
  final invalid = SecureHttpRequestException(
    SecureHttpFailure.invalidResponse,
    '$label did not answer with JSON.',
  );
  if (!json) {
    if (success) throw invalid;
    return null;
  }
  try {
    return jsonDecode(utf8.decode(bytes));
  } on FormatException {
    if (success) throw invalid;
    return null;
  }
}

/// The delay a single `Retry-After` header gives in whole seconds, when it is
/// at most a day. A header sent more than once is ignored: it does not say
/// which delay holds.
Duration? _retryAfter(List<String>? values) {
  if (values == null || values.length != 1) return null;
  final value = values.single.trim();
  if (!RegExp(r'^[0-9]{1,6}$').hasMatch(value)) return null;
  final seconds = int.parse(value);
  return seconds > _maximumRetryAfterSeconds
      ? null
      : Duration(seconds: seconds);
}

Uri _jsonTarget(Uri uri, Map<String, String> query, String label) {
  final Uri base;
  try {
    base = requirePublicHttpsUri(uri, label);
  } on StateError catch (error) {
    throw SecureHttpRequestException(
      SecureHttpFailure.invalidRequest,
      error.message,
    );
  }
  if (query.length > _maximumQueryParameters) {
    throw SecureHttpRequestException(
      SecureHttpFailure.invalidRequest,
      '$label sends at most $_maximumQueryParameters query parameters.',
    );
  }
  for (final MapEntry(:key, :value) in query.entries) {
    if (!_queryKeyPattern.hasMatch(key) ||
        value.length > 512 ||
        !_isPrintableAscii(value)) {
      throw SecureHttpRequestException(
        SecureHttpFailure.invalidRequest,
        '$label has a query parameter that is not plain text.',
      );
    }
  }
  final target = query.isEmpty ? base : base.replace(queryParameters: query);
  if (target.toString().length > 4096) {
    throw SecureHttpRequestException(
      SecureHttpFailure.invalidRequest,
      '$label has a URL longer than 4096 characters.',
    );
  }
  return target;
}

List<int>? _jsonPayload(
  String method,
  Object? body,
  int maxRequestBytes,
  String label,
) {
  if (!_jsonMethods.contains(method)) {
    throw SecureHttpRequestException(
      SecureHttpFailure.invalidRequest,
      '$label uses GET, POST, PUT or DELETE.',
    );
  }
  if (body == null) return null;
  if (method == 'GET' || method == 'DELETE') {
    throw SecureHttpRequestException(
      SecureHttpFailure.invalidRequest,
      '$label sends a body only with POST or PUT.',
    );
  }
  final List<int> bytes;
  try {
    // The service reads a body only when it is already canonical, and
    // `jsonEncode` keeps insertion order and writes doubles.
    bytes = CanonicalJson.encode(body);
  } on CanonicalJsonException {
    throw SecureHttpRequestException(
      SecureHttpFailure.invalidRequest,
      '$label has a body that is not canonical JSON.',
    );
  }
  if (bytes.length > maxRequestBytes) {
    throw SecureHttpRequestException(
      SecureHttpFailure.invalidRequest,
      '$label has a body larger than $maxRequestBytes bytes.',
    );
  }
  return bytes;
}

Map<String, String> _jsonHeaders(Map<String, String> headers, String label) {
  if (headers.length > _maximumHeaders) {
    throw SecureHttpRequestException(
      SecureHttpFailure.invalidRequest,
      '$label sends at most $_maximumHeaders headers.',
    );
  }
  for (final MapEntry(:key, :value) in headers.entries) {
    if (!_headerNamePattern.hasMatch(key) ||
        _reservedHeaders.contains(key) ||
        value.length > _maximumHeaderValueLength ||
        !_isPrintableAscii(value)) {
      throw SecureHttpRequestException(
        SecureHttpFailure.invalidRequest,
        '$label has a header it may not send.',
      );
    }
  }
  return Map.unmodifiable(headers);
}

bool _isPrintableAscii(String text) {
  for (var index = 0; index < text.length; index++) {
    final unit = text.codeUnitAt(index);
    if (unit < 0x20 || unit > 0x7E) return false;
  }
  return true;
}
