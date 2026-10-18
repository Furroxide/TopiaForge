import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:math';
import 'dart:typed_data';

import 'package:launcher_data/src/multiplayer/lan_sockets.dart';

/// Host A's and host B's server ids in
/// `tests/fixtures/multiplayer/descriptor/cases.json`.
const hostAServerId =
    'c5f39060271ebd2f22d9bf155a26bf459274e31b795145fd9d862dc206d98bb3';
const hostBServerId =
    'cf82f7bc6b072f456554dd30c31b12fe5af423bbe03ff908124a1a08f0f16443';

/// Addresses tests use: private ones on the local network and documentation
/// ranges (RFC 5737, RFC 3849) beyond it. None is ever contacted for real.
final lanHost = InternetAddress('192.168.1.20');
final otherLanHost = InternetAddress('192.168.1.21');
final publicHost = InternetAddress('203.0.113.7');
final publicHostV6 = InternetAddress('2001:db8::7');

/// Every textual form of the addresses above, for privacy assertions.
const addressTexts = [
  '192.168.1.20',
  '192.168.1.21',
  '203.0.113.7',
  '2001:db8::7',
  '127.0.0.1',
  'game.local',
];

/// Reads `tests/fixtures/multiplayer/<relative>`, which the game's C# tests
/// and the control plane replay too.
Map<String, Object?> readMultiplayerFixture(String relative) {
  var directory = Directory.current.absolute;
  while (!File('${directory.path}/TopiaForge.slnx').existsSync()) {
    final parent = directory.parent;
    if (parent.path == directory.path) {
      throw StateError('Could not locate TopiaForge.slnx.');
    }
    directory = parent;
  }
  final path = [
    directory.path,
    'tests',
    'fixtures',
    'multiplayer',
    ...relative.split('/'),
  ].join(Platform.pathSeparator);
  return asObject(jsonDecode(File(path).readAsStringSync()));
}

Map<String, Object?> asObject(Object? value) =>
    (value! as Map).map((key, item) => MapEntry(key as String, item));

List<Map<String, Object?>> asObjects(Object? value) =>
    (value! as List<Object?>).map(asObject).toList();

Uint8List hexBytes(String text) => Uint8List.fromList([
  for (var index = 0; index < text.length; index += 2)
    int.parse(text.substring(index, index + 2), radix: 16),
]);

Uint8List textBytes(String text) => Uint8List.fromList(utf8.encode(text));

/// The signed bytes of the valid descriptor fixture [id].
Uint8List signedDescriptor(String id) {
  final fixture = readMultiplayerFixture('descriptor/cases.json');
  final item = asObjects(fixture['valid']).singleWhere((c) => c['id'] == id);
  return textBytes(item['signedBytes']! as String);
}

/// Host A's public session, and host B's invite-only one.
Uint8List get hostADescriptor => signedDescriptor('public-sandbox');
Uint8List get hostBDescriptor => signedDescriptor('invite-without-target');

/// The canonical session info of the LAN fixture [id].
Uint8List sessionInfoFixture(String id) {
  final info = asObject(
    readMultiplayerFixture('lan/cases.json')['sessionInfo'],
  );
  final item = asObjects(info['valid']).singleWhere((c) => c['id'] == id);
  return textBytes(item['canonical']! as String);
}

/// Binds UDP and TCP on one loopback port number, as a game host does.
///
/// Candidates are random rather than chosen by the system: Windows reserves
/// whole port ranges for one protocol, and hands out ports in sequence, so
/// retrying a system-chosen port can stay inside a reserved range.
Future<(RawDatagramSocket, ServerSocket)> bindPortPair() async {
  final random = Random(20261015);
  for (var attempt = 0; attempt < 64; attempt++) {
    final port = 20000 + random.nextInt(40000);
    RawDatagramSocket? udp;
    try {
      udp = await RawDatagramSocket.bind(
        InternetAddress.loopbackIPv4,
        port,
        reuseAddress: false,
      );
      return (udp, await ServerSocket.bind(InternetAddress.loopbackIPv4, port));
    } on SocketException {
      udp?.close();
    }
  }
  throw StateError('No UDP and TCP port pair was free on loopback.');
}

/// A datagram a fake socket sent.
final class SentDatagram {
  const SentDatagram(this.data, this.address, this.port);

  final Uint8List data;
  final InternetAddress address;
  final int port;
}

/// A UDP socket that sends nowhere: it records what is sent and delivers
/// what a test injects, from any address it names.
final class FakeLanSocket implements LanDatagramSocket {
  FakeLanSocket(this.port);

  @override
  final int port;
  final sent = <SentDatagram>[];
  void Function(Datagram datagram)? _onDatagram;
  var closed = false;
  var failSends = false;

  /// Set when the system closed the socket, as it does after a send it
  /// refused.
  var closedBySystem = false;

  @override
  bool get isOpen => !closed && !closedBySystem;

  @override
  void listen(void Function(Datagram datagram) onDatagram) =>
      _onDatagram = onDatagram;

  @override
  bool send(List<int> datagram, InternetAddress address, int port) {
    if (!isOpen || failSends) return false;
    sent.add(SentDatagram(Uint8List.fromList(datagram), address, port));
    return true;
  }

  @override
  void close() => closed = true;

  /// Delivers [data] as if it arrived from [from] and [fromPort].
  void deliver(List<int> data, InternetAddress from, int fromPort) =>
      _onDatagram?.call(Datagram(Uint8List.fromList(data), from, fromPort));
}

/// Binds [FakeLanSocket]s: the LAN client binds its listener first and its
/// query socket second.
final class FakeLanBinder {
  final sockets = <FakeLanSocket>[];
  final binds = <(InternetAddress, int, bool)>[];

  /// Thrown by the next bind instead of binding.
  Object? failWith;

  /// Thrown by the query socket's bind alone: the one that does not share
  /// its address.
  Object? failQueryWith;

  FakeLanSocket get listener => sockets[0];
  FakeLanSocket get querier => sockets[1];

  Future<LanDatagramSocket> call(
    InternetAddress address,
    int port, {
    required bool reuseAddress,
  }) async {
    binds.add((address, port, reuseAddress));
    final failure = failWith ?? (reuseAddress ? null : failQueryWith);
    if (failure != null) throw failure;
    final socket = FakeLanSocket(port == 0 ? 50000 + sockets.length : port);
    sockets.add(socket);
    return socket;
  }
}

/// Monotonic time that moves only when a test moves it.
final class FakeClock {
  var micros = 0;

  int call() => micros;

  void advance(Duration duration) => micros += duration.inMicroseconds;
}

/// A periodic timer a test fires by hand.
final class ManualTicker {
  void Function()? _tick;
  var started = 0;

  Timer call(Duration interval, void Function() tick) {
    _tick = tick;
    started++;
    return _ManualTimer();
  }

  void fire() => _tick?.call();
}

/// One-shot timers a test fires by hand.
final class ManualTimers {
  final timers = <ManualTimer>[];

  Timer call(Duration delay, void Function() fire) {
    final timer = ManualTimer(delay, fire);
    timers.add(timer);
    return timer;
  }

  /// Fires the newest timer still waiting.
  void fireLatest() => timers.lastWhere((timer) => timer.isActive).fire();
}

final class ManualTimer implements Timer {
  ManualTimer(this.delay, this._fire);

  final Duration delay;
  final void Function() _fire;
  var _active = true;

  void fire() {
    if (!_active) return;
    _active = false;
    _fire();
  }

  @override
  void cancel() => _active = false;

  @override
  bool get isActive => _active;

  @override
  int get tick => 0;
}

final class _ManualTimer implements Timer {
  var _active = true;

  @override
  void cancel() => _active = false;

  @override
  bool get isActive => _active;

  @override
  int get tick => 0;
}

/// A descriptor endpoint on the loopback interface. Each connection's
/// request line is recorded and handed to [respond], which writes whatever
/// the test needs and may close the socket or leave it open.
final class LoopbackDescriptorHost {
  LoopbackDescriptorHost._(this._server, this._respond) {
    _server.listen(_accept);
  }

  static Future<LoopbackDescriptorHost> start(
    FutureOr<void> Function(Socket socket, String request) respond,
  ) async => LoopbackDescriptorHost._(
    await ServerSocket.bind(InternetAddress.loopbackIPv4, 0),
    respond,
  );

  /// Serves on a server socket the test already bound.
  static LoopbackDescriptorHost on(
    ServerSocket server,
    FutureOr<void> Function(Socket socket, String request) respond,
  ) => LoopbackDescriptorHost._(server, respond);

  /// Serves [body] as a descriptor: `TFD1 200 <length>` and the bytes.
  static Future<LoopbackDescriptorHost> serving(List<int> body) =>
      start((socket, _) => answer(socket, body));

  /// Writes a `TFD1 200` answer with [body], then closes.
  static Future<void> answer(Socket socket, List<int> body) async {
    socket
      ..add(ascii.encode('TFD1 200 ${body.length}\n'))
      ..add(body);
    await socket.flush();
    await socket.close();
  }

  /// Writes [bytes] as they are, then closes.
  static Future<void> raw(Socket socket, List<int> bytes) async {
    socket.add(bytes);
    await socket.flush();
    await socket.close();
  }

  final ServerSocket _server;
  FutureOr<void> Function(Socket socket, String request) _respond;
  final requests = <String>[];
  final _accepted = StreamController<Socket>.broadcast();
  final _disconnected = StreamController<void>.broadcast();
  final _open = <Socket>[];
  var connections = 0;

  int get port => _server.port;

  /// Each connection, once its request has arrived.
  Stream<Socket> get accepted => _accepted.stream;

  /// Each connection the client closed or dropped.
  Stream<void> get disconnected => _disconnected.stream;

  set respond(FutureOr<void> Function(Socket socket, String request) value) =>
      _respond = value;

  void _accept(Socket socket) {
    _open.add(socket);
    connections++;
    unawaited(socket.done.catchError((Object _) {}));
    final line = <int>[];
    late final StreamSubscription<Uint8List> subscription;
    var answered = false;
    subscription = socket.listen(
      (chunk) {
        if (answered) return;
        for (final byte in chunk) {
          line.add(byte);
          if (byte == 0x0A || line.length >= 128) {
            answered = true;
            requests.add(latin1.decode(line));
            if (!_accepted.isClosed) _accepted.add(socket);
            // A client may hang up before the answer is written.
            unawaited(
              Future.sync(
                () => _respond(socket, latin1.decode(line)),
              ).catchError((Object _) {}),
            );
            return;
          }
        }
      },
      onError: (Object _) {},
      onDone: () {
        if (!_disconnected.isClosed) _disconnected.add(null);
        unawaited(subscription.cancel());
      },
    );
  }

  Future<void> close() async {
    for (final socket in _open) {
      socket.destroy();
    }
    await _server.close();
    await _accepted.close();
    await _disconnected.close();
  }
}
