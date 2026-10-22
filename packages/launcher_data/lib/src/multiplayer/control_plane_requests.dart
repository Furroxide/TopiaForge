/// Sends the launcher's requests to the TopiaForge service, one HTTPS
/// exchange at a time through [requestHttpsJson], paced as the service asks,
/// each with deadlines and cancellation.
library;

import 'dart:async';
import 'dart:io';

import '../cancellation_token.dart';
import '../secure_http.dart';
import 'control_plane_answers.dart';
import 'control_plane_pacing.dart';
import 'control_plane_results.dart';
import 'join_descriptor_resolver.dart' show NetworkPresence;

/// Receives one line about each exchange with the service, for the
/// launcher's log. A line names the request and its outcome only: never an
/// invite code, a body, the service's address or any text the service sent.
typedef ControlPlaneLog = void Function(String line);

/// Starts a timer; the seam tests use to run the backoff without waiting.
typedef ControlPlaneTimer =
    Timer Function(Duration delay, void Function() fire);

/// The time limits of one request.
final class ControlPlaneTimeouts {
  const ControlPlaneTimeouts({
    this.connect = const Duration(seconds: 10),
    this.response = const Duration(seconds: 10),
    this.attempt = const Duration(seconds: 20),
    this.call = const Duration(seconds: 30),
  });

  /// To open the connection, and to wait for each part of the answer.
  final Duration connect;
  final Duration response;

  /// For one exchange, from the lookup to the answer's last byte.
  final Duration attempt;

  /// For a whole call, its retries and their waits included.
  final Duration call;
}

/// One request to the service.
final class ControlPlaneRequest {
  const ControlPlaneRequest({
    required this.scope,
    required this.operation,
    required this.path,
    required this.maxBytes,
    this.method = 'GET',
    this.query = const {},
    this.body,
  });

  final ControlPlaneScope scope;

  /// What the request is, in a few words for the log: never its body.
  final String operation;
  final String method;
  final String path;
  final Map<String, String> query;

  /// Written as canonical JSON. It may hold an invite code, so it is never
  /// logged or printed.
  final Object? body;
  final int maxBytes;

  @override
  String toString() => operation;
}

/// What a request came to: the bytes of a successful answer, or why there
/// are none.
final class ControlPlaneOutcome {
  const ControlPlaneOutcome.answered(List<int> this.bytes)
    : problem = null,
      retryAfter = null,
      code = null,
      timedOut = false,
      heldBack = false;

  const ControlPlaneOutcome.failed(
    MultiplayerServiceProblem this.problem, {
    this.retryAfter,
    this.code,
    this.timedOut = false,
    this.heldBack = false,
  }) : bytes = null;

  final List<int>? bytes;
  final MultiplayerServiceProblem? problem;

  /// How long to wait before asking again, when that is known.
  final Duration? retryAfter;

  /// The service's own error code, when it sent one its catalogue holds.
  final String? code;

  /// Whether no answer came in time.
  final bool timedOut;

  /// Whether nothing was sent, because the scope is held back.
  final bool heldBack;

  bool get succeeded => bytes != null;
}

/// Sends [ControlPlaneRequest]s to the service at [origin].
///
/// - **Pacing.** Nothing is sent while its scope is held ([ControlPlanePacer]):
///   the request fails at once with the time left, as rate-limited for a
///   delay the service named with a 429 or for this launcher's own count of
///   recent requests, and as unavailable after the service failed.
/// - **4xx answers are never retried.** A 429 holds its scope for its
///   `Retry-After`, or [defaultRateLimitDelay] without one.
/// - **5xx answers back off.** One that may pass is tried again within the
///   same call, at most [maximumAttempts] times in all, after the pacer's
///   backoff while that is at most [longestInCallWait] and within the call's
///   deadline. A `Retry-After` it names is honoured. `feature-disabled` and
///   `service-unavailable` are an operator's or the deployment's state, not a
///   passing failure, so they are not tried again at once.
/// - **Deadlines and cancellation.** Every exchange has [ControlPlaneTimeouts],
///   and every wait between them stops at once when [CancellationToken]
///   fires, with [OperationCancelledException].
final class ControlPlaneRequester {
  ControlPlaneRequester(
    this._origin, {
    ControlPlaneLog? log,
    ControlPlanePacer? pacer,
    ControlPlaneTimer? timer,
    NetworkPresence? networkPresence,
    this.timeouts = const ControlPlaneTimeouts(),
  }) : _log = log,
       pacer = pacer ?? ControlPlanePacer(),
       _timer = timer ?? Timer.new,
       _networkPresence = networkPresence ?? _anyNetworkUp;

  static const maximumAttempts = 3;
  static const longestInCallWait = Duration(seconds: 5);
  static const defaultRateLimitDelay = Duration(seconds: 5);

  /// What every failure of [requestHttpsJson] is labelled with; it names no
  /// address.
  static const _label = 'The TopiaForge service';

  final Uri _origin;
  final ControlPlaneLog? _log;
  final ControlPlanePacer pacer;
  final ControlPlaneTimer _timer;
  final NetworkPresence _networkPresence;
  final ControlPlaneTimeouts timeouts;

  /// Sends [request] and reads its answer. Never throws for what the network
  /// or the service does; throws [OperationCancelledException] when
  /// [cancellation] stops it.
  Future<ControlPlaneOutcome> send(
    ControlPlaneRequest request, {
    CancellationToken? cancellation,
  }) async {
    final deadline = pacer.now.add(timeouts.call);
    var exchanges = 0;
    var waits = 0;
    while (true) {
      cancellation?.throwIfCancelled();
      final hold = pacer.holdFor(request.scope);
      if (hold != null) {
        final (wait, why) = hold;
        // Only the short backoff after this call's own 5xx is waited out.
        final retrying =
            exchanges > 0 &&
            waits < maximumAttempts &&
            why == ControlPlaneHold.unavailable &&
            wait <= longestInCallWait &&
            pacer.now.add(wait).isBefore(deadline);
        if (!retrying) {
          _note('$request held back for ${_seconds(wait)} s (${why.name})');
          return ControlPlaneOutcome.failed(
            why == ControlPlaneHold.rateLimited
                ? MultiplayerServiceProblem.rateLimited
                : MultiplayerServiceProblem.unavailable,
            retryAfter: wait,
            heldBack: true,
          );
        }
        waits++;
        await _wait(wait, cancellation);
        continue;
      }
      final remaining = deadline.difference(pacer.now);
      if (remaining <= Duration.zero) {
        return const ControlPlaneOutcome.failed(
          MultiplayerServiceProblem.unreachable,
          timedOut: true,
        );
      }
      final outcome = await _exchange(
        request,
        ++exchanges,
        remaining < timeouts.attempt ? remaining : timeouts.attempt,
        cancellation,
      );
      if (outcome != null) return outcome;
    }
  }

  /// One exchange; null when the call should try again.
  Future<ControlPlaneOutcome?> _exchange(
    ControlPlaneRequest request,
    int attempt,
    Duration limit,
    CancellationToken? cancellation,
  ) async {
    final scope = request.scope;
    final slot = pacer.take(scope);
    final started = pacer.now;
    final SecureHttpJsonResponse response;
    try {
      response = await requestHttpsJson(
        _origin.replace(path: request.path),
        label: _label,
        method: request.method,
        query: request.query,
        body: request.body,
        maxResponseBytes: request.maxBytes,
        connectionTimeout: timeouts.connect,
        responseTimeout: timeouts.response,
        totalTimeout: limit,
        cancellation: cancellation,
      );
    } on OperationCancelledException {
      _note('$request was cancelled');
      rethrow;
    } on SecureHttpRequestException catch (error) {
      _note('$request failed (${error.failure.name}) after ${_since(started)}');
      return _failedExchange(error.failure, slot);
    }
    final status = response.statusCode;
    final code = response.isSuccess
        ? null
        : readErrorCode(status, response.bytes);
    _note(
      '$request answered $status${code == null ? '' : ' ($code)'} after '
      '${_since(started)}${attempt > 1 ? ', attempt $attempt' : ''}',
    );
    // An invite resolution keeps its slot only when it missed, as the
    // service counts misses.
    final missed = status == 404 && code == 'not-found';
    if (scope == ControlPlaneScope.invite && !missed) pacer.giveBack(slot);
    if (status < 500) pacer.answered(scope);
    if (response.isSuccess) {
      return status == 200
          ? ControlPlaneOutcome.answered(response.bytes)
          : const ControlPlaneOutcome.failed(
              MultiplayerServiceProblem.invalidAnswer,
            );
    }
    final named = response.retryAfter;
    if (status == 429) {
      final delay = named ?? defaultRateLimitDelay;
      pacer.holdOff(scope, delay, ControlPlaneHold.rateLimited);
      return ControlPlaneOutcome.failed(
        MultiplayerServiceProblem.rateLimited,
        retryAfter: delay,
        code: code,
      );
    }
    if (status < 500) {
      return ControlPlaneOutcome.failed(
        missed
            ? MultiplayerServiceProblem.notFound
            : MultiplayerServiceProblem.refused,
        code: code,
      );
    }
    if (named != null) {
      pacer.holdOff(scope, named, ControlPlaneHold.unavailable);
    }
    if (code == 'feature-disabled') {
      pacer.answered(scope);
      return ControlPlaneOutcome.failed(
        MultiplayerServiceProblem.turnedOff,
        retryAfter: named,
        code: code,
      );
    }
    pacer.failed(scope);
    final passing =
        code != 'service-unavailable' && code != 'relay-unavailable';
    if (passing && attempt < maximumAttempts) return null;
    return ControlPlaneOutcome.failed(
      MultiplayerServiceProblem.unavailable,
      retryAfter: pacer.holdFor(scope)?.$1,
      code: code,
    );
  }

  Future<ControlPlaneOutcome> _failedExchange(
    SecureHttpFailure failure,
    ControlPlaneSlot slot,
  ) async {
    final scope = slot.scope;
    switch (failure) {
      case SecureHttpFailure.timeout:
        // The request may have been read, and may even have missed, so an
        // invite's slot stays; the service is given time.
        pacer.failed(scope);
        return const ControlPlaneOutcome.failed(
          MultiplayerServiceProblem.unreachable,
          timedOut: true,
        );
      case SecureHttpFailure.network:
        if (scope == ControlPlaneScope.invite) pacer.giveBack(slot);
        return ControlPlaneOutcome.failed(
          await _anyNetwork()
              ? MultiplayerServiceProblem.unreachable
              : MultiplayerServiceProblem.offline,
        );
      case SecureHttpFailure.redirect:
        if (scope == ControlPlaneScope.invite) pacer.giveBack(slot);
        return const ControlPlaneOutcome.failed(
          MultiplayerServiceProblem.unavailable,
        );
      case SecureHttpFailure.tooLarge || SecureHttpFailure.invalidResponse:
        pacer.answered(scope);
        return const ControlPlaneOutcome.failed(
          MultiplayerServiceProblem.invalidAnswer,
        );
      case SecureHttpFailure.invalidRequest:
        // Every request is built from checked values, so this is a fault in
        // this launcher, never something to send again.
        if (scope == ControlPlaneScope.invite) pacer.giveBack(slot);
        return const ControlPlaneOutcome.failed(
          MultiplayerServiceProblem.refused,
        );
    }
  }

  /// Waits [delay], or stops at once with [OperationCancelledException]
  /// when [cancellation] fires.
  Future<void> _wait(Duration delay, CancellationToken? cancellation) {
    final done = Completer<void>();
    final timer = _timer(delay, () {
      if (!done.isCompleted) done.complete();
    });
    final registration = cancellation?.register(() {
      timer.cancel();
      if (!done.isCompleted) {
        done.completeError(const OperationCancelledException());
      }
    });
    return done.future.whenComplete(() => registration?.dispose());
  }

  /// Whether any network is up; true when that cannot be told, so offline
  /// is reported only when it is certain.
  Future<bool> _anyNetwork() async {
    try {
      return await _networkPresence();
    } on Object {
      return true;
    }
  }

  void _note(String line) => _log?.call('TopiaForge service: $line.');

  String _since(DateTime started) =>
      '${pacer.now.difference(started).inMilliseconds} ms';

  static int _seconds(Duration wait) => (wait.inMilliseconds + 999) ~/ 1000;
}

Future<bool> _anyNetworkUp() async {
  final networks = await NetworkInterface.list();
  return networks.any((network) => network.addresses.isNotEmpty);
}
