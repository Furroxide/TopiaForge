import 'dart:async';
import 'dart:io';
import 'dart:typed_data';

import 'package:launcher_data/src/multiplayer/lan_sockets.dart';
import 'package:test/test.dart';

import 'multiplayer_network_support.dart';

/// The LAN socket over a socket that cannot always take a datagram. Dart on
/// Windows keeps one datagram send in flight per socket and returns 0 for a
/// send made before the previous one has completed; a socket whose send the
/// system refused closes itself. Over a scripted socket nothing waits on the
/// OS; over real loopback sockets the same holds against the real behaviour.
void main() {
  final lanHost = InternetAddress('192.168.1.20');

  group('over a socket the test scripts', () {
    late _ScriptedRawSocket raw;
    late ManualTimers timers;
    late LanDatagramSocket socket;

    setUp(() {
      raw = _ScriptedRawSocket();
      timers = ManualTimers();
      socket = wrapLanDatagramSocket(raw, retryTimer: timers.call);
    });

    test('keeps a datagram the socket cannot take yet, and sends it once '
        'it can', () {
      raw.takesDatagrams = false;
      expect(socket.send([1, 2, 3], lanHost, 27250), isTrue);
      expect(raw.sent, isEmpty);
      expect(raw.writeEventsEnabled, isTrue);

      raw.takesDatagrams = true;
      raw.becomeWritable();
      expect(raw.sent, [
        [1, 2, 3],
      ]);
      expect(raw.destinations.single, (lanHost, 27250));
    });

    test('sends what waits in order, ahead of what is sent after it', () {
      raw.takesDatagrams = false;
      socket
        ..send([1], lanHost, 27250)
        ..send([2], lanHost, 27250);
      raw.takesDatagrams = true;
      expect(socket.send([3], lanHost, 27250), isTrue);
      expect(raw.sent, [
        [1],
        [2],
        [3],
      ]);
    });

    test('a write event that brings no progress waits a moment, rather '
        'than spinning on write events', () {
      raw.takesDatagrams = false;
      socket.send([1], lanHost, 27250);
      raw.becomeWritable();
      expect(raw.sent, isEmpty);
      expect(raw.writeEventsEnabled, isFalse);
      final retry = timers.timers.single;
      expect(retry.delay, const Duration(milliseconds: 1));

      // While the retry waits, a send and another write event without
      // progress start no second timer.
      socket.send([2], lanHost, 27250);
      raw.becomeWritable();
      expect(timers.timers, hasLength(1));

      raw.takesDatagrams = true;
      timers.fireLatest();
      expect(raw.sent, [
        [1],
        [2],
      ]);
    });

    test('a waiting datagram keeps the bytes it was given when the caller '
        'reuses its buffer', () {
      raw.takesDatagrams = false;
      final buffer = Uint8List.fromList([1, 2, 3]);
      expect(socket.send(buffer, lanHost, 27250), isTrue);
      buffer.fillRange(0, buffer.length, 9);
      raw.takesDatagrams = true;
      raw.becomeWritable();
      expect(raw.sent, [
        [1, 2, 3],
      ]);
    });

    test('reads nothing until it is listened to, so a datagram that comes '
        'first waits for the listener instead of spinning on read events', () {
      // Dart raises a read event again at once while a datagram waits
      // unread, so a socket that took read events with no listener would
      // hold the event loop until the listener came.
      raw.arrive([7]);
      expect(raw.readEventsLeftUnread, 0);
      final heard = <List<int>>[];
      socket.listen((datagram) => heard.add(datagram.data));
      expect(heard, [
        [7],
      ]);
      expect(raw.readEventsLeftUnread, 0);
    });

    test('keeps at most 64 datagrams waiting, and refuses the next', () {
      raw.takesDatagrams = false;
      for (var index = 0; index < 64; index++) {
        expect(socket.send([index], lanHost, 27250), isTrue);
      }
      expect(socket.send([64], lanHost, 27250), isFalse);
      raw.takesDatagrams = true;
      raw.becomeWritable();
      expect(raw.sent, hasLength(64));
    });

    test('a socket the system closed says so, and sends nothing more', () {
      raw.takesDatagrams = false;
      socket.send([1], lanHost, 27250);
      raw.closeAfterRefusedSend();
      expect(socket.isOpen, isFalse);

      raw.takesDatagrams = true;
      expect(socket.send([2], lanHost, 27250), isFalse);
      expect(raw.sent, isEmpty);
    });

    test('a datagram the socket refuses outright is not kept', () {
      raw.partialSends = true;
      expect(socket.send([1, 2], lanHost, 27250), isFalse);
      raw.partialSends = false;
      raw.becomeWritable();
      expect(raw.sent, isEmpty);
    });

    test('closing drops what waits and stops the retry timer', () {
      raw.takesDatagrams = false;
      socket.send([1], lanHost, 27250);
      raw.becomeWritable();
      socket.close();
      expect(socket.isOpen, isFalse);
      expect(raw.closed, isTrue);
      expect(timers.timers.single.isActive, isFalse);
    });
  });

  group('over real loopback sockets', () {
    test('every datagram sent back to back arrives', () async {
      // As many as may wait (64), each small enough that the receiver's
      // buffer holds them all, so only the sender can lose one.
      const count = 64;
      final datagrams = [
        for (var index = 0; index < count; index++) Uint8List(200)..[0] = index,
      ];
      final receiver = await RawDatagramSocket.bind(
        InternetAddress.loopbackIPv4,
        0,
      );
      addTearDown(receiver.close);
      final all = Completer<void>();
      final received = <int>[];
      receiver.listen((event) {
        if (event != RawSocketEvent.read) return;
        for (var d = receiver.receive(); d != null; d = receiver.receive()) {
          received.add(d.data.first);
          if (received.length == count && !all.isCompleted) all.complete();
        }
      });
      final socket = await bindLanDatagramSocket(
        InternetAddress.loopbackIPv4,
        0,
        reuseAddress: false,
      );
      addTearDown(socket.close);
      socket.listen((_) {});
      final accepted = [
        for (final datagram in datagrams)
          socket.send(datagram, InternetAddress.loopbackIPv4, receiver.port),
      ];
      expect(accepted, everyElement(isTrue));
      await all.future.timeout(
        const Duration(seconds: 10),
        onTimeout: () => fail('${received.length} of $count arrived.'),
      );
      expect(received, List.generate(count, (index) => index));
    });

    test(
      'a send the system refuses closes the socket, and it says so',
      () async {
        final socket = await bindLanDatagramSocket(
          InternetAddress.loopbackIPv4,
          0,
          reuseAddress: false,
        );
        addTearDown(socket.close);
        socket.listen((_) {});
        // A socket bound to loopback cannot reach TEST-NET-1. Windows reports
        // that after the send, Linux and macOS during it; Dart closes the
        // socket either way.
        socket.send(List.filled(64, 0), InternetAddress('192.0.2.1'), 9);
        for (var wait = 0; wait < 500 && socket.isOpen; wait++) {
          await Future<void>.delayed(const Duration(milliseconds: 10));
        }
        expect(socket.isOpen, isFalse);
        expect(
          socket.send(List.filled(64, 0), InternetAddress.loopbackIPv4, 9),
          isFalse,
        );
      },
    );
  });
}

/// A raw UDP socket the test drives. It takes a datagram only while
/// [takesDatagrams] is set, as a real one takes it only once the send before
/// it has completed, and raises the events a real one raises.
final class _ScriptedRawSocket extends Stream<RawSocketEvent>
    implements RawDatagramSocket {
  final _events = StreamController<RawSocketEvent>(sync: true);
  final sent = <List<int>>[];
  final destinations = <(InternetAddress, int)>[];
  var takesDatagrams = true;

  /// Takes only part of each datagram, which a datagram socket never should.
  var partialSends = false;
  var closed = false;
  var _writeEventsEnabled = true;
  var _readEventsEnabled = true;
  final _arrived = <Datagram>[];

  /// Read events after which a datagram still waited unread. Dart raises
  /// the event again at once then, so on a real socket each would repeat
  /// without end.
  var readEventsLeftUnread = 0;

  @override
  bool get readEventsEnabled => _readEventsEnabled;

  @override
  set readEventsEnabled(bool value) {
    _readEventsEnabled = value;
    _raiseReads();
  }

  @override
  bool get writeEventsEnabled => _writeEventsEnabled;

  @override
  set writeEventsEnabled(bool value) => _writeEventsEnabled = value;

  @override
  int get port => 50000;

  @override
  InternetAddress get address => InternetAddress.loopbackIPv4;

  @override
  int send(List<int> buffer, InternetAddress address, int port) {
    if (closed || !takesDatagrams) return 0;
    if (partialSends) return buffer.length - 1;
    sent.add(List.of(buffer));
    destinations.add((address, port));
    return buffer.length;
  }

  @override
  Datagram? receive() => _arrived.isEmpty ? null : _arrived.removeAt(0);

  /// A datagram arrives from the network.
  void arrive(List<int> data) {
    _arrived.add(
      Datagram(Uint8List.fromList(data), InternetAddress('192.168.1.20'), 1),
    );
    _raiseReads();
  }

  /// Raises read events while someone listens, reads are enabled and a
  /// datagram waits, as Dart does: again after each one that left a datagram
  /// unread. A few rounds are enough to see a socket that does not read.
  void _raiseReads() {
    for (var round = 0; round < 4; round++) {
      if (!_events.hasListener || !_readEventsEnabled || _arrived.isEmpty) {
        return;
      }
      final waiting = _arrived.length;
      _events.add(RawSocketEvent.read);
      if (_arrived.length == waiting) readEventsLeftUnread++;
    }
  }

  @override
  void close() => closed = true;

  @override
  StreamSubscription<RawSocketEvent> listen(
    void Function(RawSocketEvent event)? onData, {
    Function? onError,
    void Function()? onDone,
    bool? cancelOnError,
  }) => _events.stream.listen(
    onData,
    onError: onError,
    onDone: onDone,
    cancelOnError: cancelOnError,
  );

  /// Raises a write event, as a real socket does once it is asked for one,
  /// and turns the request off, as a real one does when it raises it.
  void becomeWritable() {
    if (!_writeEventsEnabled) return;
    _writeEventsEnabled = false;
    _events.add(RawSocketEvent.write);
  }

  /// What Dart does after the system refused a send: it reports the error
  /// and closes the socket.
  void closeAfterRefusedSend() {
    _events.addError(
      const SocketException('The network location cannot be reached.'),
    );
    closed = true;
    _events.add(RawSocketEvent.closed);
    unawaited(_events.close());
  }

  @override
  Object? noSuchMethod(Invocation invocation) =>
      throw UnimplementedError('Not scripted: ${invocation.memberName}');
}
