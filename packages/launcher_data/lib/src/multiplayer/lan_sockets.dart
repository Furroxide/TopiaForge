/// The UDP seam under LAN discovery.
///
/// Datagrams carry their sender's address, so this is network-layer
/// plumbing: nothing here is exported from the package, and nothing above
/// the LAN client ever sees a [Datagram].
library;

import 'dart:async';
import 'dart:io';

/// One bound UDP socket, as LAN discovery uses it.
abstract interface class LanDatagramSocket {
  /// The local port the socket is bound to.
  int get port;

  /// Delivers every datagram that arrives to [onDatagram], once.
  void listen(void Function(Datagram datagram) onDatagram);

  /// Sends [datagram] whole; false when it could not be sent now, which loses
  /// it as any network might.
  bool send(List<int> datagram, InternetAddress address, int port);

  /// Closes the socket; harmless when already closed.
  void close();
}

/// Binds a [LanDatagramSocket] to [address] and [port]. [reuseAddress] lets
/// the launcher and the game listen on the discovery port together.
typedef LanSocketBinder =
    Future<LanDatagramSocket> Function(
      InternetAddress address,
      int port, {
      required bool reuseAddress,
    });

/// Binds a real UDP socket.
Future<LanDatagramSocket> bindLanDatagramSocket(
  InternetAddress address,
  int port, {
  required bool reuseAddress,
}) async {
  final socket = await RawDatagramSocket.bind(
    address,
    port,
    reuseAddress: reuseAddress,
  );
  return _RawLanDatagramSocket(socket);
}

final class _RawLanDatagramSocket implements LanDatagramSocket {
  _RawLanDatagramSocket(this._socket) {
    _socket.writeEventsEnabled = false;
  }

  /// Reads per read event, so one flood cannot hold the event loop; the
  /// socket signals again while datagrams remain.
  static const _maximumReadsPerEvent = 256;

  final RawDatagramSocket _socket;
  StreamSubscription<RawSocketEvent>? _subscription;
  var _closed = false;

  @override
  int get port => _socket.port;

  @override
  void listen(void Function(Datagram datagram) onDatagram) {
    if (_subscription != null || _closed) {
      throw StateError('A LAN socket is listened to once.');
    }
    _subscription = _socket.listen(
      (event) {
        if (event != RawSocketEvent.read) return;
        for (var count = 0; count < _maximumReadsPerEvent; count++) {
          final Datagram? datagram;
          try {
            datagram = _socket.receive();
          } on SocketException {
            // A transient error, such as an ICMP report about an earlier
            // send, loses that one datagram.
            continue;
          }
          if (datagram == null) break;
          try {
            onDatagram(datagram);
          } on Object {
            // Handlers never throw; this keeps a socket alive if one does.
          }
        }
      },
      onError: (Object _) {},
      cancelOnError: false,
    );
  }

  @override
  bool send(List<int> datagram, InternetAddress address, int port) {
    if (_closed) return false;
    try {
      return _socket.send(datagram, address, port) == datagram.length;
    } on SocketException {
      return false;
    } on OSError {
      return false;
    }
  }

  @override
  void close() {
    if (_closed) return;
    _closed = true;
    unawaited(_subscription?.cancel());
    _socket.close();
  }
}
