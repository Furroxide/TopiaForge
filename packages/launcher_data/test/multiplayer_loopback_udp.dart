/// Real UDP sockets on the loopback interface, for the LAN client's
/// real-socket tests. Nothing those tests wait for can hang:
///
/// - **No send is lost to a busy socket.** Dart on Windows keeps one datagram
///   send in flight per socket. A send made before the previous one has
///   completed returns 0 and sends nothing. A reply sent right behind another
///   one therefore vanished on loaded runners, and the test waited for it
///   until it timed out. [LoopbackUdpHost.send] checks every result and tries
///   again a moment later, for as long as a loaded runner may need.
/// - **A dropped datagram is sent again, not waited for.**
///   [LoopbackUdpHost.repeatUntil] repeats a send on an interval until what
///   the test waits for has happened, within a bound that names it.
/// - **Order is observed, not assumed.** [ObservedLanBinder] records every
///   datagram the client's own sockets hand it, after the client has handled
///   it, so a test can wait for one to be handled before sending the next.
library;

import 'dart:async';
import 'dart:io';

import 'package:launcher_data/src/multiplayer/lan_sockets.dart';

/// A game host's UDP socket, bound to the loopback interface.
final class LoopbackUdpHost {
  LoopbackUdpHost._(this._socket) {
    _socket
      ..writeEventsEnabled = false
      ..listen(_onEvent);
  }

  static Future<LoopbackUdpHost> bind() async => LoopbackUdpHost._(
    await RawDatagramSocket.bind(InternetAddress.loopbackIPv4, 0),
  );

  /// How long a send may wait for the socket to take its datagram.
  static const _sendWithin = Duration(seconds: 5);

  /// How long a refused send waits before it tries again. A write event is
  /// no sign that the send in flight has completed: after a datagram send
  /// Dart still reports the socket writable, so the event comes at once. On
  /// a loaded runner that send can take milliseconds, and retries paced by
  /// write events alone ran out in less than one.
  static const _retryAfter = Duration(milliseconds: 1);

  final RawDatagramSocket _socket;
  final _firstReceived = Completer<Datagram>();

  int get port => _socket.port;

  /// The first datagram this socket receives.
  Future<Datagram> get firstReceived => _firstReceived.future;

  /// Sends [datagram] whole to [address] and [port], or throws once
  /// [_sendWithin] has passed without the socket taking it.
  Future<void> send(
    List<int> datagram,
    InternetAddress address,
    int port,
  ) async {
    final clock = Stopwatch()..start();
    while (_socket.send(datagram, address, port) != datagram.length) {
      if (clock.elapsed >= _sendWithin) {
        throw StateError(
          'The loopback socket did not send a datagram within $_sendWithin.',
        );
      }
      await Future<void>.delayed(_retryAfter);
    }
  }

  /// Runs [sendOnce] now, and again every [every], until [done] completes,
  /// and returns what [done] completed with. Throws a [TimeoutException]
  /// naming [what] once [within] has passed without it.
  Future<T> repeatUntil<T>(
    Future<T> done,
    Future<void> Function() sendOnce, {
    required String what,
    Duration every = const Duration(milliseconds: 500),
    Duration within = const Duration(seconds: 8),
  }) async {
    final finished = done.then<bool>((_) => true, onError: (Object _) => true);
    final clock = Stopwatch()..start();
    while (clock.elapsed < within) {
      await sendOnce();
      final interval = Completer<bool>();
      final timer = Timer(every, () => interval.complete(false));
      final over = await Future.any([finished, interval.future]);
      timer.cancel();
      if (over) return done;
    }
    throw TimeoutException('$what did not happen within $within.', within);
  }

  void close() => _socket.close();

  void _onEvent(RawSocketEvent event) {
    if (event != RawSocketEvent.read) return;
    for (
      var datagram = _socket.receive();
      datagram != null;
      datagram = _socket.receive()
    ) {
      if (!_firstReceived.isCompleted) _firstReceived.complete(datagram);
    }
  }
}

/// Binds the LAN client's sockets for real, on the address it asks for, and
/// records what each one hands the client.
final class ObservedLanBinder {
  final _sockets = <ObservedLanSocket>[];

  /// The query socket: the client binds its listener first and this second.
  ObservedLanSocket get querier => _sockets[1];

  Future<LanDatagramSocket> call(
    InternetAddress address,
    int port, {
    required bool reuseAddress,
  }) async {
    final socket = ObservedLanSocket._(
      await bindLanDatagramSocket(address, port, reuseAddress: reuseAddress),
    );
    _sockets.add(socket);
    return socket;
  }
}

/// A real LAN socket that reports the size of each datagram it handed the
/// client, once the client has handled it.
final class ObservedLanSocket implements LanDatagramSocket {
  ObservedLanSocket._(this._socket);

  final LanDatagramSocket _socket;
  final _handled = StreamController<int>.broadcast(sync: true);

  /// The size of every datagram handled so far, in the order they arrived.
  final handledSizes = <int>[];

  /// Each handled datagram's size, as the client finishes with it.
  Stream<int> get handled => _handled.stream;

  @override
  int get port => _socket.port;

  @override
  bool get isOpen => _socket.isOpen;

  @override
  void listen(void Function(Datagram datagram) onDatagram) =>
      _socket.listen((datagram) {
        onDatagram(datagram);
        handledSizes.add(datagram.data.length);
        if (!_handled.isClosed) _handled.add(datagram.data.length);
      });

  @override
  bool send(List<int> datagram, InternetAddress address, int port) =>
      _socket.send(datagram, address, port);

  @override
  void close() {
    _socket.close();
    unawaited(_handled.close());
  }
}
