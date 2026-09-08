/// UDP transport for the reachability probe, behind an interface so the probe logic is testable without a network.
library;

import 'dart:async';
import 'dart:io';
import 'dart:math';
import 'dart:typed_data';

import 'stun_message.dart';

/// Opens a transport bound for one address family.
///
/// The family is chosen by the caller rather than the transport because a probe run has to stay inside a single
/// family — see [StunTransport] — and only the caller knows which servers it is about to use.
typedef StunTransportFactory =
    Future<StunTransport> Function(InternetAddressType family);

/// One STUN binding transaction.
///
/// An implementation is bound to a single address family. RFC 5780 behaviour discovery compares the reflexive
/// endpoints seen across a server's addresses and ports, and endpoints in different families are not comparable, so
/// a run that mixed them would produce a mapping verdict that means nothing.
abstract class StunTransport {
  /// Sends a binding request to [server] and waits for a matching success response.
  ///
  /// Returns `null` on timeout, on a malformed reply, or when the reply does not match the transaction. Callers
  /// treat `null` as evidence, not as an error.
  ///
  /// [changeAddress] and [changePort] ask the server to answer from a different address and/or port (RFC 5780
  /// CHANGE-REQUEST), which is how NAT filtering behaviour is observed.
  Future<StunBindingResponse?> request(
    StunEndpoint server, {
    bool changeAddress = false,
    bool changePort = false,
  });

  /// Whether [candidate] is this machine's own endpoint — that is, no NAT translated the address.
  bool matchesLocalEndpoint(StunEndpoint candidate);

  Future<void> close();
}

/// A [StunTransport] over a single unconnected UDP socket.
///
/// Deliberately minimal. It binds one ephemeral port, sends a few small datagrams, and closes. It keeps no
/// connection, allocates no relay, and gathers no candidates.
class UdpStunTransport implements StunTransport {
  UdpStunTransport._(
    this._socket,
    this._addressLength,
    this._localAddresses,
    this._timeout,
  ) : _random = Random.secure() {
    _socket.writeEventsEnabled = false;
    // RawDatagramSocket is single-subscription, and cancelling that subscription
    // closes the socket. Its listener belongs to the entire probe run.
    _subscription = _socket.listen(
      _onSocketEvent,
      onError: (Object _) => unawaited(close()),
      onDone: _onSocketDone,
    );
  }

  /// Binds an ephemeral UDP port for [family] and snapshots this machine's own interface addresses.
  ///
  /// The socket is bound for one family because it is used unconnected for every transaction in a run, and a datagram
  /// socket cannot send outside the family it was bound for. `anyIPv6` is not dual-stack on every platform — Windows
  /// defaults `IPV6_V6ONLY` on and Dart exposes no way to clear it — so a family is chosen rather than assumed.
  ///
  /// The interface addresses are held only to answer [matchesLocalEndpoint] and are discarded with [close]. They are
  /// never persisted, logged, or passed to `launcher_domain`.
  static Future<UdpStunTransport> bind(
    InternetAddressType family, {
    Duration timeout = const Duration(milliseconds: 700),
  }) async {
    final wantsIPv6 = family == InternetAddressType.IPv6;
    final addressLength = wantsIPv6 ? 16 : 4;
    final socket = await RawDatagramSocket.bind(
      wantsIPv6 ? InternetAddress.anyIPv6 : InternetAddress.anyIPv4,
      0,
    );
    try {
      final addresses = <StunEndpoint>[];
      for (final interface in await NetworkInterface.list(
        includeLoopback: false,
        includeLinkLocal: false,
      )) {
        for (final address in interface.addresses) {
          if (address.rawAddress.length != addressLength) continue;
          addresses.add(
            StunEndpoint(Uint8List.fromList(address.rawAddress), socket.port),
          );
        }
      }
      return UdpStunTransport._(socket, addressLength, addresses, timeout);
    } catch (_) {
      socket.close();
      rethrow;
    }
  }

  final RawDatagramSocket _socket;
  final int _addressLength;
  final List<StunEndpoint> _localAddresses;
  final Duration _timeout;
  final Random _random;
  final StunCodec _codec = const StunCodec();
  late final StreamSubscription<RawSocketEvent> _subscription;
  _StunTransaction? _pending;
  Future<void>? _closing;
  bool _closed = false;

  @override
  bool matchesLocalEndpoint(StunEndpoint candidate) =>
      _localAddresses.contains(candidate);

  /// Refuses overlapping requests and requests after close with `null`, leaving
  /// the active transaction untouched. A probe run sends its requests in order.
  @override
  Future<StunBindingResponse?> request(
    StunEndpoint server, {
    bool changeAddress = false,
    bool changePort = false,
  }) async {
    if (_closed ||
        _pending != null ||
        server.address.length != _addressLength) {
      return null;
    }

    final transactionId = Uint8List.fromList(
      List<int>.generate(
        StunCodec.transactionIdLength,
        (_) => _random.nextInt(256),
      ),
    );
    final message = _codec.encodeBindingRequest(
      transactionId: transactionId,
      changeAddress: changeAddress,
      changePort: changePort,
    );
    final transaction = _StunTransaction(transactionId);
    _pending = transaction;
    transaction.deadline = Timer(_timeout, () => _finish(transaction, null));
    try {
      while (_socket.receive() != null) {}
      final destination = InternetAddress.fromRawAddress(server.address);
      if (_socket.send(message, destination, server.port) <= 0) {
        _finish(transaction, null);
      }
    } on SocketException {
      _finish(transaction, null);
    }
    return transaction.completer.future;
  }

  void _onSocketEvent(RawSocketEvent event) {
    if (event != RawSocketEvent.read || _closed) return;
    for (
      var datagram = _socket.receive();
      datagram != null;
      datagram = _socket.receive()
    ) {
      final transaction = _pending;
      if (transaction == null) continue;
      final decoded = _codec.decodeBindingResponse(
        Uint8List.fromList(datagram.data),
        transaction.id,
      );
      if (decoded != null) _finish(transaction, decoded);
    }
  }

  void _finish(_StunTransaction transaction, StunBindingResponse? response) {
    if (!identical(_pending, transaction)) return;
    _pending = null;
    transaction.deadline?.cancel();
    transaction.completer.complete(response);
  }

  void _onSocketDone() {
    _closed = true;
    _localAddresses.clear();
    final transaction = _pending;
    if (transaction != null) _finish(transaction, null);
  }

  @override
  Future<void> close() => _closing ??= _close();

  Future<void> _close() async {
    _onSocketDone();
    try {
      await _subscription.cancel();
    } finally {
      _socket.close();
    }
  }
}

class _StunTransaction {
  _StunTransaction(this.id);

  final Uint8List id;
  final Completer<StunBindingResponse?> completer = Completer();
  Timer? deadline;
}
