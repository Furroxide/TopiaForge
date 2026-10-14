/// Fetches a host's signed descriptor from its bounded TCP endpoint
/// (`docs/internal/MultiplayerLiveSessions.md`, section 15).
library;

import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:launcher_domain/launcher_domain.dart';

import '../cancellation_token.dart';
import 'descriptor_response.dart';
import 'descriptor_signature_verifier.dart';

/// Why a descriptor fetch produced no descriptor.
enum DescriptorFetchFailure {
  /// Relay-only forbids contacting the address; nothing was opened.
  directNotAllowed,

  /// Nothing answered, or the connection failed.
  unreachable,

  /// This computer has no route to any network.
  offline,

  /// The host did not connect or answer in time.
  timedOut,

  /// `TFD1 404`: the host serves no descriptor yet.
  notServing,

  /// `TFD1 429`: the host is limiting this player's requests.
  rateLimited,

  /// `TFD1 503`: the host's endpoint is full.
  busy,

  /// Another status, such as `TFD1 400`.
  refused,

  /// The answer broke the protocol or its bounds.
  malformedResponse,

  /// The bytes are not a valid descriptor.
  invalidDescriptor,

  /// The descriptor's signature does not verify.
  badSignature,

  /// A valid descriptor of another server than the one expected.
  otherServer,

  /// The fetch was cancelled.
  cancelled,
}

/// The outcome of [DescriptorClient.fetchFrom]. Neither kind names an
/// address.
sealed class DescriptorFetch {
  const DescriptorFetch();
}

/// A descriptor that verified, against the expected server when one was
/// given.
final class DescriptorFetched extends DescriptorFetch {
  const DescriptorFetched(this.descriptor);

  final VerifiedServerDescriptor descriptor;
}

final class DescriptorFetchFailed extends DescriptorFetch {
  const DescriptorFetchFailed(
    this.failure,
    this.message, {
    this.servedServerId,
  });

  final DescriptorFetchFailure failure;

  /// A sentence for the player; it never names an address.
  final String message;

  /// For [DescriptorFetchFailure.otherServer]: the server the descriptor
  /// names. Its signature was not checked, so it proves nothing yet.
  final String? servedServerId;

  @override
  String toString() => 'descriptor fetch failed (${failure.name}): $message';
}

/// Opens a TCP connection; the seam tests use to watch what is contacted.
typedef DescriptorConnector =
    Future<ConnectionTask<Socket>> Function(InternetAddress address, int port);

/// Starts a one-shot timer; tests drive time through it.
typedef DescriptorTimer = Timer Function(Duration delay, void Function() fire);

/// Asks one host for its signed descriptor with `TFD1 descriptor`, and
/// verifies it with Ed25519 before returning it.
///
/// - **Contact.** Under relay-only an address beyond the local network is
///   refused before any socket opens; that is the last line of the
///   [JoinContactRules], which callers also apply before they get here.
/// - **Bounds.** The connection must open within [connectTimeout] and the
///   whole answer arrive within [responseTimeout]. The status line must come
///   within 64 bytes and the body be at most 256 KiB and exactly as long as
///   announced.
/// - **Cancellation** closes the connection at once.
/// - **Failures** never throw and never name an address: a socket's own
///   messages can, so none is passed on.
final class DescriptorClient {
  DescriptorClient({
    ServerDescriptorVerifier verifier = const ServerDescriptorVerifier(
      Ed25519DescriptorSignatureVerifier(),
    ),
    this.connectTimeout = const Duration(seconds: 5),
    this.responseTimeout = const Duration(seconds: 5),
    DescriptorConnector? connect,
    DescriptorTimer? timer,
  }) : _verifier = verifier,
       _connect = connect ?? Socket.startConnect,
       _timer = timer ?? Timer.new;

  /// The one request this client sends.
  static final request = ascii.encode('TFD1 descriptor\n');

  final Duration connectTimeout;
  final Duration responseTimeout;
  final ServerDescriptorVerifier _verifier;
  final DescriptorConnector _connect;
  final DescriptorTimer _timer;

  /// Fetches the descriptor served at [address] and [port]. [allowDirect] is
  /// the session's choice: without it, only an address on the local network
  /// is contacted. [expectedServerId], when given, is the only server
  /// accepted.
  Future<DescriptorFetch> fetchFrom(
    InternetAddress address,
    int port, {
    required bool allowDirect,
    String? expectedServerId,
    CancellationToken? cancellation,
  }) async {
    if (!allowDirect && !isOnLocalNetwork(address)) {
      return const DescriptorFetchFailed(
        DescriptorFetchFailure.directNotAllowed,
        'Reaching this host directly would show it your address.',
      );
    }
    if (port < 1 || port > 65535) {
      return const DescriptorFetchFailed(
        DescriptorFetchFailure.unreachable,
        'The host could not be reached.',
      );
    }
    if (cancellation?.isCancelled ?? false) return _cancelled;
    final response = await _exchange(address, port, cancellation);
    if (response is! _Answered) return response as DescriptorFetchFailed;
    return _verify(response.response, expectedServerId, cancellation);
  }

  /// Whether [address] is on the local network, as the game classifies
  /// paths. Anything that is not an IP address is not.
  static bool isOnLocalNetwork(InternetAddress address) {
    if (address.type != InternetAddressType.IPv4 &&
        address.type != InternetAddressType.IPv6) {
      return false;
    }
    try {
      return NetworkHost.pathKindOfAddress(address.rawAddress) ==
          NetworkPathKind.lan;
    } on ArgumentError {
      return false;
    }
  }

  Future<Object> _exchange(
    InternetAddress address,
    int port,
    CancellationToken? cancellation,
  ) {
    final done = Completer<Object>();
    final reader = Tfd1ResponseReader();
    ConnectionTask<Socket>? task;
    Socket? socket;
    StreamSubscription<List<int>>? subscription;
    Timer? deadline;
    CancellationRegistration? registration;

    void finish(Object outcome) {
      if (done.isCompleted) return;
      deadline?.cancel();
      registration?.dispose();
      unawaited(subscription?.cancel());
      socket?.destroy();
      task?.cancel();
      done.complete(outcome);
    }

    void answered(Tfd1Response response) => finish(_Answered(response));

    registration = cancellation?.register(() => finish(_cancelled));
    if (done.isCompleted) return done.future;
    deadline = _timer(connectTimeout, () => finish(_timedOut));

    _startConnect(address, port).then((started) {
      if (done.isCompleted) {
        // Too late: consume the attempt's outcome before abandoning it.
        unawaited(
          started.socket.then((late) => late.destroy(), onError: (Object _) {}),
        );
        started.cancel();
        return;
      }
      task = started;
      started.socket.then((connected) {
        if (done.isCompleted) {
          connected.destroy();
          return;
        }
        socket = connected;
        deadline?.cancel();
        deadline = _timer(responseTimeout, () => finish(_timedOut));
        unawaited(connected.done.then((_) {}, onError: (Object _) {}));
        subscription = connected.listen(
          (chunk) {
            if (reader.add(chunk)) answered(reader.result!);
          },
          onError: (Object error) => finish(_failureOf(error)),
          onDone: () => answered(reader.close()),
          cancelOnError: true,
        );
        try {
          connected.setOption(SocketOption.tcpNoDelay, true);
          connected.add(request);
        } on Object catch (error) {
          finish(_failureOf(error));
        }
      }, onError: (Object error) => finish(_failureOf(error)));
    }, onError: (Object error) => finish(_failureOf(error)));
    return done.future;
  }

  /// Starts the connection, turning a synchronous refusal into a failed
  /// future so every outcome arrives the same way.
  Future<ConnectionTask<Socket>> _startConnect(
    InternetAddress address,
    int port,
  ) async => _connect(address, port);

  Future<DescriptorFetch> _verify(
    Tfd1Response response,
    String? expectedServerId,
    CancellationToken? cancellation,
  ) async {
    switch (response) {
      case Tfd1Malformed(:final message):
        return DescriptorFetchFailed(
          DescriptorFetchFailure.malformedResponse,
          message,
        );
      case Tfd1Status(:final status):
        return _statusFailure(status);
      case Tfd1Body(:final bytes):
        final verification = await _verifier.verify(
          bytes,
          expectedServerId: expectedServerId,
        );
        if (cancellation?.isCancelled ?? false) return _cancelled;
        return switch (verification) {
          ServerDescriptorAccepted(:final descriptor) => DescriptorFetched(
            descriptor,
          ),
          ServerDescriptorRefused(
            refusal: ServerDescriptorRefusal.otherServer,
            :final servedServerId,
          ) =>
            DescriptorFetchFailed(
              DescriptorFetchFailure.otherServer,
              'The host serves another server than the one expected.',
              servedServerId: servedServerId,
            ),
          ServerDescriptorRefused(
            refusal: ServerDescriptorRefusal.badSignature,
            :final message,
          ) =>
            DescriptorFetchFailed(DescriptorFetchFailure.badSignature, message),
          ServerDescriptorRefused(:final message) => DescriptorFetchFailed(
            DescriptorFetchFailure.invalidDescriptor,
            message,
          ),
        };
    }
  }

  static DescriptorFetchFailed _statusFailure(String status) =>
      switch (status) {
        '404' => const DescriptorFetchFailed(
          DescriptorFetchFailure.notServing,
          'The host is not serving a session.',
        ),
        '429' => const DescriptorFetchFailed(
          DescriptorFetchFailure.rateLimited,
          'The host is limiting requests. Try again in a moment.',
        ),
        '503' => const DescriptorFetchFailed(
          DescriptorFetchFailure.busy,
          'The host is busy. Try again in a moment.',
        ),
        _ => const DescriptorFetchFailed(
          DescriptorFetchFailure.refused,
          'The host refused the request.',
        ),
      };

  /// Error codes for "no network at all": ENETDOWN and ENETUNREACH on
  /// Windows, Linux and macOS.
  static const _offlineCodes = {10050, 10051, 100, 101, 50, 51};

  /// A socket failure as a reason. The error's own text may name the
  /// address, so only its code is read.
  static DescriptorFetchFailed _failureOf(Object error) {
    final code = error is SocketException ? error.osError?.errorCode : null;
    if (code != null && _offlineCodes.contains(code)) {
      return const DescriptorFetchFailed(
        DescriptorFetchFailure.offline,
        'This computer is not connected to a network.',
      );
    }
    return const DescriptorFetchFailed(
      DescriptorFetchFailure.unreachable,
      'The host could not be reached.',
    );
  }

  static const _cancelled = DescriptorFetchFailed(
    DescriptorFetchFailure.cancelled,
    'The request was cancelled.',
  );

  static const _timedOut = DescriptorFetchFailed(
    DescriptorFetchFailure.timedOut,
    'The host did not answer in time.',
  );
}

final class _Answered {
  const _Answered(this.response);

  final Tfd1Response response;
}
