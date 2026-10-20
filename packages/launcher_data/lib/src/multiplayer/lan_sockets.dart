/// The UDP seam under LAN discovery.
///
/// Datagrams carry their sender's address, so this is network-layer
/// plumbing: nothing here is exported from the package, and nothing above
/// the LAN client ever sees a [Datagram].
library;

import 'dart:async';
import 'dart:collection';
import 'dart:io';
import 'dart:typed_data';

/// One bound UDP socket, as LAN discovery uses it.
abstract interface class LanDatagramSocket {
  /// The local port the socket is bound to.
  int get port;

  /// False once the socket no longer works: closed here, or closed by the
  /// system after a send it refused. Nothing sent through it arrives then.
  bool get isOpen;

  /// Delivers every datagram that arrives to [onDatagram], once.
  void listen(void Function(Datagram datagram) onDatagram);

  /// Sends [datagram] whole: now, or as soon as the socket can take it, in
  /// the order it was given. False when it cannot be sent at all, which
  /// loses it as any network might.
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

/// Starts a one-shot timer; tests run it by hand.
typedef LanRetryTimer = Timer Function(Duration delay, void Function() run);

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
  return wrapLanDatagramSocket(socket);
}

/// A [LanDatagramSocket] over [socket], which it now owns.
LanDatagramSocket wrapLanDatagramSocket(
  RawDatagramSocket socket, {
  LanRetryTimer? retryTimer,
}) => _RawLanDatagramSocket(socket, retryTimer ?? Timer.new);

/// A real socket, with the two things `RawDatagramSocket` leaves to its
/// caller done here:
///
/// - **A send the socket cannot take yet waits, in order.** Dart on Windows
///   keeps one datagram send in flight per socket, and a send made before
///   the previous one has completed returns 0 and sends nothing. Such a
///   datagram is kept, with any sent after it, and goes out once the socket
///   can take it: on the next write event, or, since a write event can come
///   before the send in flight has completed, on a short timer after that.
///   At most [maximumWaiting] wait; past that a send is refused.
/// - **A socket the system closed says so.** A send the system refuses, such
///   as one to a network it cannot reach, is reported after `send` returns,
///   and `RawDatagramSocket` then closes itself. [isOpen] turns false, so the
///   LAN client can bind a new socket instead of sending into a closed one.
final class _RawLanDatagramSocket implements LanDatagramSocket {
  _RawLanDatagramSocket(this._socket, this._retryTimer) {
    _socket
      ..readEventsEnabled = false
      ..writeEventsEnabled = false;
    _subscription = _socket.listen(
      _onEvent,
      onError: (Object _) => _stop(),
      onDone: _stop,
      cancelOnError: false,
    );
  }

  /// Reads per read event, so one flood cannot hold the event loop; the
  /// socket signals again while datagrams remain.
  static const _maximumReadsPerEvent = 256;

  /// Datagrams kept while the socket cannot take them: as many queries as
  /// the LAN client keeps outstanding.
  static const maximumWaiting = 64;

  /// How long a datagram the socket refused once more after a write event
  /// waits before the next try.
  static const retryDelay = Duration(milliseconds: 1);

  final RawDatagramSocket _socket;
  final LanRetryTimer _retryTimer;
  late final StreamSubscription<RawSocketEvent> _subscription;
  final _waiting = Queue<_WaitingDatagram>();
  void Function(Datagram datagram)? _onDatagram;
  Timer? _retry;
  var _open = true;

  @override
  int get port => _socket.port;

  @override
  bool get isOpen => _open;

  @override
  void listen(void Function(Datagram datagram) onDatagram) {
    if (_onDatagram != null || !_open) {
      throw StateError('A LAN socket is listened to once.');
    }
    _onDatagram = onDatagram;
    _socket.readEventsEnabled = true;
  }

  @override
  bool send(List<int> datagram, InternetAddress address, int port) {
    if (!_open) return false;
    _flush(afterWriteEvent: false);
    if (_waiting.isEmpty) {
      switch (_sendNow(datagram, address, port)) {
        case _Send.sent:
          return true;
        case _Send.refused:
          return false;
        case _Send.later:
          break;
      }
    }
    if (!_open || _waiting.length >= maximumWaiting) return false;
    _waiting.add(_WaitingDatagram(Uint8List.fromList(datagram), address, port));
    _awaitWritable(afterWriteEvent: false);
    return true;
  }

  @override
  void close() {
    if (!_open) return;
    _stop();
    unawaited(_subscription.cancel());
    _socket.close();
  }

  void _onEvent(RawSocketEvent event) {
    if (event == RawSocketEvent.read) {
      _read();
    } else if (event == RawSocketEvent.write) {
      _flush(afterWriteEvent: true);
    } else if (event == RawSocketEvent.closed) {
      _stop();
    }
  }

  void _read() {
    final onDatagram = _onDatagram;
    if (onDatagram == null) return;
    for (var count = 0; count < _maximumReadsPerEvent; count++) {
      final Datagram? datagram;
      try {
        datagram = _socket.receive();
      } on SocketException {
        // A transient error, such as an ICMP report about an earlier send,
        // loses that one datagram.
        continue;
      }
      if (datagram == null) break;
      try {
        onDatagram(datagram);
      } on Object {
        // Handlers never throw; this keeps a socket alive if one does.
      }
    }
  }

  /// Sends what waits, in order, until the socket cannot take the next one.
  void _flush({required bool afterWriteEvent}) {
    var stalled = afterWriteEvent;
    while (_open && _waiting.isNotEmpty) {
      final next = _waiting.first;
      final sent = _sendNow(next.data, next.address, next.port);
      if (sent == _Send.later) {
        _awaitWritable(afterWriteEvent: stalled);
        return;
      }
      // Sent, or refused for good: either way it no longer waits.
      _waiting.removeFirst();
      stalled = false;
    }
  }

  /// Tries again on the next write event. A write event can come before the
  /// send in flight has completed, so once one has brought no progress the
  /// next try waits [retryDelay] instead of spinning on write events.
  void _awaitWritable({required bool afterWriteEvent}) {
    if (!afterWriteEvent) {
      _socket.writeEventsEnabled = true;
      return;
    }
    _retry ??= _retryTimer(retryDelay, () {
      _retry = null;
      _flush(afterWriteEvent: false);
    });
  }

  _Send _sendNow(List<int> datagram, InternetAddress address, int port) {
    try {
      final sent = _socket.send(datagram, address, port);
      if (sent == datagram.length) return _Send.sent;
      return sent == 0 ? _Send.later : _Send.refused;
    } on Object {
      return _Send.refused;
    }
  }

  void _stop() {
    _open = false;
    _waiting.clear();
    _retry?.cancel();
    _retry = null;
  }
}

enum _Send { sent, later, refused }

final class _WaitingDatagram {
  const _WaitingDatagram(this.data, this.address, this.port);

  final Uint8List data;
  final InternetAddress address;
  final int port;
}
