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
  ) : _random = Random.secure();

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
  }

  final RawDatagramSocket _socket;
  final int _addressLength;
  final List<StunEndpoint> _localAddresses;
  final Duration _timeout;
  final Random _random;
  final StunCodec _codec = const StunCodec();

  @override
  bool matchesLocalEndpoint(StunEndpoint candidate) =>
      _localAddresses.contains(candidate);

  @override
  Future<StunBindingResponse?> request(
    StunEndpoint server, {
    bool changeAddress = false,
    bool changePort = false,
  }) async {
    // The socket cannot reach another family, and `send` would throw rather than report it. Callers pick the
    // family before binding, so this only fires on a programming error; it stays a `null` because the whole
    // transport contract is that a transaction reports evidence, never an exception.
    if (server.address.length != _addressLength) return null;

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

    // Drain anything still queued from an earlier transaction so a late reply cannot be mistaken for this one.
    while (_socket.receive() != null) {}

    final destination = InternetAddress.fromRawAddress(server.address);
    if (_socket.send(message, destination, server.port) <= 0) return null;

    final completer = Completer<StunBindingResponse?>();
    late StreamSubscription<RawSocketEvent> subscription;
    Timer? deadline;

    void finish(StunBindingResponse? response) {
      if (completer.isCompleted) return;
      deadline?.cancel();
      unawaited(subscription.cancel());
      completer.complete(response);
    }

    subscription = _socket.listen(
      (event) {
        if (event != RawSocketEvent.read) return;
        for (
          var datagram = _socket.receive();
          datagram != null;
          datagram = _socket.receive()
        ) {
          final decoded = _codec.decodeBindingResponse(
            Uint8List.fromList(datagram.data),
            transactionId,
          );
          if (decoded != null) {
            finish(decoded);
            return;
          }
        }
      },
      onError: (_) => finish(null),
      cancelOnError: true,
    );

    deadline = Timer(_timeout, () => finish(null));
    return completer.future;
  }

  @override
  Future<void> close() async {
    _localAddresses.clear();
    _socket.close();
  }
}
