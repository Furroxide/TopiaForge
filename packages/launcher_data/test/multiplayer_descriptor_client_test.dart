import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_data/src/multiplayer/descriptor_client.dart';
import 'package:test/test.dart';

import 'multiplayer_network_support.dart';

/// The descriptor client against endpoints on the loopback interface, and
/// against a recording connector where nothing may be contacted.
void main() {
  final fixture = readMultiplayerFixture('descriptor/cases.json');
  final loopback = InternetAddress.loopbackIPv4;

  Future<LoopbackDescriptorHost> host(
    FutureOr<void> Function(Socket socket, String request) respond,
  ) async {
    final started = await LoopbackDescriptorHost.start(respond);
    addTearDown(started.close);
    return started;
  }

  Future<DescriptorFetch> fetchFrom(
    LoopbackDescriptorHost endpoint, {
    String? expectedServerId,
    DescriptorClient? client,
    CancellationToken? cancellation,
  }) => (client ?? DescriptorClient()).fetchFrom(
    loopback,
    endpoint.port,
    allowDirect: false,
    expectedServerId: expectedServerId,
    cancellation: cancellation,
  );

  DescriptorFetchFailure failureOf(DescriptorFetch fetch) =>
      (fetch as DescriptorFetchFailed).failure;

  group('replays the shared descriptor fixtures', () {
    for (final item in asObjects(fixture['valid'])) {
      test('${item['id']} is fetched and verified', () async {
        final endpoint = await host(
          (socket, _) => LoopbackDescriptorHost.answer(
            socket,
            textBytes(item['signedBytes']! as String),
          ),
        );
        final fetch = await fetchFrom(
          endpoint,
          expectedServerId: item['serverId']! as String,
        );
        final descriptor = (fetch as DescriptorFetched).descriptor;
        expect(descriptor.serverId, item['serverId']);
        expect(descriptor.descriptorSha256, item['descriptorSha256']);
        expect(descriptor.packageDigest, item['packageDigest']);
        expect(endpoint.requests, ['TFD1 descriptor\n']);
      });
    }

    test('every tampered descriptor is refused for its own reason', () async {
      const signatureCases = {
        'signature-flipped',
        'signature-without-context',
        'signed-by-other-key',
      };
      var bytes = Uint8List(0);
      final endpoint = await host(
        (socket, _) => LoopbackDescriptorHost.answer(socket, bytes),
      );
      final cases = asObjects(fixture['invalid']);
      expect(cases.length, greaterThanOrEqualTo(30));
      for (final item in cases) {
        final id = item['id']! as String;
        bytes = item.containsKey('bytesHex')
            ? hexBytes(item['bytesHex']! as String)
            : textBytes(item['bytes']! as String);
        final fetch =
            await fetchFrom(
                  endpoint,
                  expectedServerId: item['expectedServerId'] as String?,
                )
                as DescriptorFetchFailed;
        expect(
          fetch.failure,
          signatureCases.contains(id)
              ? DescriptorFetchFailure.badSignature
              : id == 'other-server-expected'
              ? DescriptorFetchFailure.otherServer
              : DescriptorFetchFailure.invalidDescriptor,
          reason: '$id: ${item['about']}',
        );
        if (id == 'other-server-expected') {
          expect(fetch.servedServerId, hostAServerId);
        }
      }
    });
  });

  test('a descriptor for another server than expected is refused', () async {
    final endpoint = await host(
      (socket, _) => LoopbackDescriptorHost.answer(socket, hostBDescriptor),
    );
    final fetch =
        await fetchFrom(endpoint, expectedServerId: hostAServerId)
            as DescriptorFetchFailed;
    expect(fetch.failure, DescriptorFetchFailure.otherServer);
    expect(fetch.servedServerId, hostBServerId);
  });

  test('a flipped signature bit is refused', () async {
    final tampered = Uint8List.fromList(hostADescriptor);
    final text = utf8.decode(tampered);
    final at = text.indexOf('"signature":"') + '"signature":"'.length;
    tampered[at] = tampered[at] == 0x41 ? 0x42 : 0x41;
    final endpoint = await host(
      (socket, _) => LoopbackDescriptorHost.answer(socket, tampered),
    );
    expect(
      failureOf(await fetchFrom(endpoint, expectedServerId: hostAServerId)),
      DescriptorFetchFailure.badSignature,
    );
  });

  test('statuses without a body name their reason', () async {
    var status = '';
    final endpoint = await host(
      (socket, _) => LoopbackDescriptorHost.raw(socket, ascii.encode(status)),
    );
    for (final (line, failure) in [
      ('TFD1 404\n', DescriptorFetchFailure.notServing),
      ('TFD1 429\n', DescriptorFetchFailure.rateLimited),
      ('TFD1 503\n', DescriptorFetchFailure.busy),
      ('TFD1 400\n', DescriptorFetchFailure.refused),
      ('TFD1 999\n', DescriptorFetchFailure.refused),
    ]) {
      status = line;
      expect(failureOf(await fetchFrom(endpoint)), failure, reason: line);
    }
  });

  test('answers that break the protocol are refused', () async {
    var answer = <int>[];
    final endpoint = await host(
      (socket, _) => LoopbackDescriptorHost.raw(socket, answer),
    );
    for (final text in [
      'TFD1 200\n',
      'HTTP/1.1 200 OK\r\n\r\n',
      'TFD1 200 abc\n',
      'TFD1 200 0\n',
      'TFD1 200 0100\n',
      'TFD1 200 -5\n',
      'TFD1 200 262145\n',
      'TFD1 200 99999999\n',
      'TFD1 4040\n',
      '\n',
      '${'A' * 64}\n',
      '',
      'TFD1 200 5\nabc',
    ]) {
      answer = ascii.encode(text);
      expect(
        failureOf(await fetchFrom(endpoint)),
        DescriptorFetchFailure.malformedResponse,
        reason: jsonEncode(text),
      );
    }
    answer = [...ascii.encode('TFD1 '), 0xE9, ...ascii.encode('00\n')];
    expect(
      failureOf(await fetchFrom(endpoint)),
      DescriptorFetchFailure.malformedResponse,
    );
  });

  test('more bytes than announced are refused', () async {
    final body = hostADescriptor;
    final endpoint = await host(
      (socket, _) => LoopbackDescriptorHost.raw(socket, [
        ...ascii.encode('TFD1 200 ${body.length - 1}\n'),
        ...body,
      ]),
    );
    expect(
      failureOf(await fetchFrom(endpoint)),
      DescriptorFetchFailure.malformedResponse,
    );
  });

  test('an impossible size is refused without waiting for the body', () async {
    final timers = ManualTimers();
    final endpoint = await host((socket, _) {
      // Announce too much and keep the connection open: the client must not
      // wait for a body it will never accept.
      socket.add(ascii.encode('TFD1 200 ${256 * 1024 + 1}\n'));
    });
    final fetch = await fetchFrom(
      endpoint,
      client: DescriptorClient(timer: timers.call),
    );
    expect(failureOf(fetch), DescriptorFetchFailure.malformedResponse);
    expect((fetch as DescriptorFetchFailed).message, contains('262144 bytes'));
    expect(timers.timers.where((timer) => timer.isActive), isEmpty);
  });

  test('the largest descriptor the bound allows still arrives whole', () async {
    final body = Uint8List(256 * 1024)..fillRange(0, 256 * 1024, 0x20);
    final endpoint = await host(
      (socket, _) => LoopbackDescriptorHost.answer(socket, body),
    );
    // Spaces are not a descriptor, so it is refused only once read in full.
    expect(
      failureOf(await fetchFrom(endpoint)),
      DescriptorFetchFailure.invalidDescriptor,
    );
  });

  test('a host that never answers times out', () async {
    final timers = ManualTimers();
    final endpoint = await host((_, _) {});
    final accepted = endpoint.accepted.first;
    final disconnected = endpoint.disconnected.first;
    final fetch = fetchFrom(
      endpoint,
      client: DescriptorClient(timer: timers.call),
    );
    await accepted;
    expect(timers.timers.last.delay, const Duration(seconds: 5));
    timers.fireLatest();
    expect(failureOf(await fetch), DescriptorFetchFailure.timedOut);
    await disconnected.timeout(const Duration(seconds: 10));
  });

  test('a connection that never opens times out', () async {
    final timers = ManualTimers();
    final pending = Completer<ConnectionTask<Socket>>();
    final client = DescriptorClient(
      timer: timers.call,
      connect: (_, _) => pending.future,
    );
    final fetch = client.fetchFrom(lanHost, 27250, allowDirect: false);
    await Future<void>.delayed(Duration.zero);
    expect(timers.timers.single.delay, const Duration(seconds: 5));
    timers.fireLatest();
    expect(failureOf(await fetch), DescriptorFetchFailure.timedOut);
  });

  test('cancelling stops a fetch at once and closes the connection', () async {
    final endpoint = await host((_, _) {});
    final accepted = endpoint.accepted.first;
    final disconnected = endpoint.disconnected.first;
    final cancellation = CancellationToken();
    final fetch = fetchFrom(endpoint, cancellation: cancellation);
    await accepted;
    cancellation.cancel();
    expect(failureOf(await fetch), DescriptorFetchFailure.cancelled);
    await disconnected.timeout(const Duration(seconds: 10));
  });

  test('a cancelled token contacts nothing', () async {
    final connects = <InternetAddress>[];
    final client = DescriptorClient(
      connect: (address, _) async {
        connects.add(address);
        throw const SocketException('refused');
      },
    );
    final cancellation = CancellationToken()..cancel();
    final fetch = await client.fetchFrom(
      lanHost,
      27250,
      allowDirect: false,
      cancellation: cancellation,
    );
    expect(failureOf(fetch), DescriptorFetchFailure.cancelled);
    expect(connects, isEmpty);
  });

  group('relay-only', () {
    final beyond = [
      publicHost,
      publicHostV6,
      InternetAddress('100.64.0.1'),
      InternetAddress('8.8.8.8'),
      InternetAddress('::ffff:203.0.113.7'),
      InternetAddress('172.32.0.1'),
    ];

    test('refuses every address beyond the local network before a socket '
        'opens', () async {
      var attempts = 0;
      await IOOverrides.runZoned(
        () async {
          for (final address in beyond) {
            final fetch =
                await DescriptorClient().fetchFrom(
                      address,
                      27250,
                      allowDirect: false,
                    )
                    as DescriptorFetchFailed;
            expect(
              fetch.failure,
              DescriptorFetchFailure.directNotAllowed,
              reason: address.address,
            );
            expect(fetch.message, isNot(contains(address.address)));
          }
        },
        socketStartConnect: (host, port, {sourceAddress, sourcePort = 0}) {
          attempts++;
          throw const SocketException('no socket may open');
        },
        socketConnect: (host, port, {sourceAddress, sourcePort = 0, timeout}) {
          attempts++;
          throw const SocketException('no socket may open');
        },
      );
      expect(attempts, 0);
    });

    test('contacts the local network: loopback, private, link-local and '
        'unique local', () async {
      final connects = <InternetAddress>[];
      final client = DescriptorClient(
        connect: (address, _) async {
          connects.add(address);
          throw const SocketException('refused');
        },
      );
      final local = [
        '127.0.0.1',
        '10.0.0.1',
        '172.16.0.1',
        '192.168.1.1',
        '169.254.1.1',
        '::1',
        'fe80::1',
        'fd00::1',
      ].map(InternetAddress.new).toList();
      for (final address in local) {
        expect(
          failureOf(await client.fetchFrom(address, 27250, allowDirect: false)),
          DescriptorFetchFailure.unreachable,
        );
      }
      expect(connects, local);
    });

    test('a session allowed direct contact may reach beyond it', () async {
      final connects = <InternetAddress>[];
      final client = DescriptorClient(
        connect: (address, _) async {
          connects.add(address);
          throw const SocketException('refused');
        },
      );
      for (final address in beyond) {
        await client.fetchFrom(address, 27250, allowDirect: true);
      }
      expect(connects, beyond);
    });
  });

  test('socket failures name a reason, never the address', () async {
    for (final (code, failure) in [
      (10061, DescriptorFetchFailure.unreachable),
      (111, DescriptorFetchFailure.unreachable),
      (10051, DescriptorFetchFailure.offline),
      (101, DescriptorFetchFailure.offline),
      (null, DescriptorFetchFailure.unreachable),
    ]) {
      final client = DescriptorClient(
        connect: (address, port) async => throw SocketException(
          'Connection failed to $address:$port',
          osError: code == null ? null : OSError('failed', code),
          address: address,
          port: port,
        ),
      );
      final fetch =
          await client.fetchFrom(lanHost, 27250, allowDirect: false)
              as DescriptorFetchFailed;
      expect(fetch.failure, failure, reason: '$code');
      for (final address in addressTexts) {
        expect(fetch.message, isNot(contains(address)));
        expect(fetch.toString(), isNot(contains(address)));
      }
    }
  });

  test('an impossible port contacts nothing', () async {
    final connects = <int>[];
    final client = DescriptorClient(
      connect: (_, port) async {
        connects.add(port);
        throw const SocketException('refused');
      },
    );
    for (final port in [0, -1, 65536]) {
      expect(
        failureOf(await client.fetchFrom(lanHost, port, allowDirect: false)),
        DescriptorFetchFailure.unreachable,
      );
    }
    expect(connects, isEmpty);
  });
}
