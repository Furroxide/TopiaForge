/// UDP transport for the reachability probe, behind an interface so the probe logic is testable without a network.
library;

import 'dart:async';
import 'dart:io';
import 'dart:math';
import 'dart:typed_data';

import 'stun_message.dart';

/// One STUN binding transaction.
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
  UdpStunTransport._(this._socket, this._localAddresses, this._timeout)
    : _random = Random.secure();

  /// Binds an ephemeral UDP port and snapshots this machine's own interface addresses.
  ///
  /// The interface addresses are held only to answer [matchesLocalEndpoint] and are discarded with [close]. They are
  /// never persisted, logged, or passed to `launcher_domain`.
  static Future<UdpStunTransport> bind({
    Duration timeout = const Duration(milliseconds: 700),
  }) async {
    final socket = await RawDatagramSocket.bind(InternetAddress.anyIPv4, 0);
    final addresses = <StunEndpoint>[];
    for (final interface in await NetworkInterface.list(
      includeLoopback: false,
      includeLinkLocal: false,
    )) {
      for (final address in interface.addresses) {
        addresses.add(
          StunEndpoint(Uint8List.fromList(address.rawAddress), socket.port),
        );
      }
    }
    return UdpStunTransport._(socket, addresses, timeout);
  }

  final RawDatagramSocket _socket;
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
