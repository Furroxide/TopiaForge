import 'dart:async';
import 'dart:collection';
import 'dart:io';
import 'dart:typed_data';

import 'package:launcher_data/src/reachability/stun_message.dart';
import 'package:launcher_data/src/reachability/stun_transport.dart';
import 'package:test/test.dart';

void main() {
  test('one socket supports successive successful transactions', () async {
    final fixture = await _LoopbackFixture.create();
    addTearDown(fixture.close);

    final first = fixture.transport.request(fixture.endpoint);
    fixture.reply(await fixture.nextRequest(), mappedPort: 41001);
    expect((await first)?.mapped.port, 41001);

    final second = fixture.transport.request(
      fixture.endpoint,
      changePort: true,
    );
    fixture.reply(await fixture.nextRequest(), mappedPort: 41002);
    expect((await second)?.mapped.port, 41002);
    expect(fixture.requestCount, 2);
  });

  test('a timed out transaction does not prevent the next request', () async {
    final fixture = await _LoopbackFixture.create(
      timeout: const Duration(seconds: 1),
    );
    addTearDown(fixture.close);

    final first = fixture.transport.request(fixture.endpoint);
    await fixture.nextRequest();
    expect(await first, isNull);

    final second = fixture.transport.request(fixture.endpoint);
    fixture.reply(await fixture.nextRequest(), mappedPort: 41002);
    expect((await second)?.mapped.port, 41002);
    expect(fixture.requestCount, 2);
  });

  test(
    'close completes pending work and refuses subsequent requests',
    () async {
      final fixture = await _LoopbackFixture.create(
        timeout: const Duration(seconds: 3),
      );
      addTearDown(fixture.close);

      final pending = fixture.transport.request(fixture.endpoint);
      await fixture.nextRequest();
      await fixture.transport.close();
      expect(await pending.timeout(const Duration(seconds: 1)), isNull);
      expect(await fixture.transport.request(fixture.endpoint), isNull);
      await fixture.transport.close();
      expect(fixture.requestCount, 1);
    },
  );

  test('a late response cannot complete a newer transaction', () async {
    final fixture = await _LoopbackFixture.create(
      timeout: const Duration(seconds: 1),
    );
    addTearDown(fixture.close);

    final first = fixture.transport.request(fixture.endpoint);
    final firstRequest = await fixture.nextRequest();
    expect(await first, isNull);

    final second = fixture.transport.request(fixture.endpoint);
    final secondRequest = await fixture.nextRequest();
    fixture.reply(firstRequest, mappedPort: 41001);
    fixture.reply(secondRequest, mappedPort: 41002);
    expect((await second)?.mapped.port, 41002);
  });

  test('overlapping request is refused without disrupting the owner', () async {
    final fixture = await _LoopbackFixture.create();
    addTearDown(fixture.close);

    final first = fixture.transport.request(fixture.endpoint);
    final firstRequest = await fixture.nextRequest();
    expect(await fixture.transport.request(fixture.endpoint), isNull);
    fixture.reply(firstRequest, mappedPort: 41001);
    expect((await first)?.mapped.port, 41001);
    expect(fixture.requestCount, 1);
  });
}

/// An owned loopback responder. Tests never contact a public STUN server.
class _LoopbackFixture {
  _LoopbackFixture(this._server, this.transport) {
    _server.writeEventsEnabled = false;
    _subscription = _server.listen((event) {
      if (event != RawSocketEvent.read) return;
      for (
        var datagram = _server.receive();
        datagram != null;
        datagram = _server.receive()
      ) {
        requestCount++;
        _requests.add(datagram);
        _arrival.complete();
        _arrival = Completer<void>();
      }
    });
  }

  static Future<_LoopbackFixture> create({
    Duration timeout = const Duration(seconds: 2),
  }) async {
    final server = await RawDatagramSocket.bind(
      InternetAddress.loopbackIPv4,
      0,
    );
    try {
      final transport = await UdpStunTransport.bind(
        InternetAddressType.IPv4,
        timeout: timeout,
      );
      return _LoopbackFixture(server, transport);
    } catch (_) {
      server.close();
      rethrow;
    }
  }

  final RawDatagramSocket _server;
  final UdpStunTransport transport;
  final Queue<Datagram> _requests = Queue<Datagram>();
  late final StreamSubscription<RawSocketEvent> _subscription;
  Completer<void> _arrival = Completer<void>();
  int requestCount = 0;

  StunEndpoint get endpoint =>
      StunEndpoint(Uint8List.fromList([127, 0, 0, 1]), _server.port);

  Future<Datagram> nextRequest() async {
    while (_requests.isEmpty) {
      await _arrival.future.timeout(const Duration(seconds: 1));
    }
    return _requests.removeFirst();
  }

  void reply(Datagram request, {required int mappedPort}) {
    final response = Uint8List(32);
    final view = ByteData.sublistView(response);
    view.setUint16(0, 0x0101);
    view.setUint16(2, 12);
    response.setRange(4, 20, request.data, 4);
    view.setUint16(20, 0x0020);
    view.setUint16(22, 8);
    response[25] = 1;
    view.setUint16(26, mappedPort ^ 0x2112);
    for (var index = 0; index < 4; index++) {
      response[28 + index] =
          request.address.rawAddress[index] ^ response[4 + index];
    }
    final sent = _server.send(response, request.address, request.port);
    expect(
      sent,
      response.length,
      reason: 'The fixture must actually send each response.',
    );
  }

  Future<void> close() async {
    await transport.close();
    await _subscription.cancel();
    _server.close();
  }
}
