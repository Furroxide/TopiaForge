import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

part 'reachability_probe_service_test_part.dart';

StunEndpoint _v4(List<int> address, int port) =>
    StunEndpoint(Uint8List.fromList(address), port);

/// The scripted server endpoints every group in this suite shares.
final primary = _v4([198, 51, 100, 10], 3478);
final alternate = _v4([203, 0, 113, 20], 3479);
final alternateOnPrimaryPort = _v4([203, 0, 113, 20], 3478);

/// A scripted transport. Every reply is chosen by the destination it was sent to, so a test can model a NAT by
/// deciding which reflexive endpoint each destination sees.
class _FakeStunTransport implements StunTransport {
  _FakeStunTransport({
    required this.mappedFor,
    this.answersChangeAddress = false,
    this.answersChangePort = false,
    this.otherAddress,
    this.localEndpoints = const [],
  });

  /// Destination endpoint -> the reflexive endpoint the server reports back. A missing entry means no answer.
  final Map<StunEndpoint, StunEndpoint?> mappedFor;
  final bool answersChangeAddress;
  final bool answersChangePort;
  final StunEndpoint? otherAddress;
  final List<StunEndpoint> localEndpoints;

  final List<String> calls = [];
  bool closed = false;

  @override
  Future<StunBindingResponse?> request(
    StunEndpoint server, {
    bool changeAddress = false,
    bool changePort = false,
  }) async {
    calls.add('$server change=$changeAddress/$changePort');
    if (changeAddress && !answersChangeAddress) return null;
    if (!changeAddress && changePort && !answersChangePort) return null;
    final mapped = mappedFor[server];
    if (mapped == null) return null;
    return StunBindingResponse(mapped: mapped, otherAddress: otherAddress);
  }

  @override
  bool matchesLocalEndpoint(StunEndpoint candidate) =>
      localEndpoints.contains(candidate);

  @override
  Future<void> close() async => closed = true;
}

void main() {
  group('StunCodec', () {
    const codec = StunCodec();
    final transactionId = Uint8List.fromList(List<int>.generate(12, (i) => i));

    test('encodes a bare binding request as a 20-byte header', () {
      final message = codec.encodeBindingRequest(transactionId: transactionId);

      expect(message.length, StunCodec.headerLength);
      expect(message[0], 0x00);
      expect(message[1], 0x01);
      expect(message.sublist(4, 8), [0x21, 0x12, 0xA4, 0x42]);
      expect(message.sublist(8), transactionId);
    });

    test('appends CHANGE-REQUEST with the requested flags', () {
      final message = codec.encodeBindingRequest(
        transactionId: transactionId,
        changeAddress: true,
        changePort: true,
      );

      expect(message.length, StunCodec.headerLength + 8);
      expect(message[21], 0x03, reason: 'CHANGE-REQUEST attribute type');
      expect(message[27], 0x06, reason: 'change address (0x04) | port (0x02)');
    });

    test('rejects a transaction id of the wrong length', () {
      expect(
        () => codec.encodeBindingRequest(transactionId: Uint8List(8)),
        throwsArgumentError,
      );
    });

    test('decodes an XOR-MAPPED-ADDRESS round trip', () {
      final datagram = _successResponse(
        transactionId: transactionId,
        mapped: _v4([192, 0, 2, 55], 51234),
      );

      final decoded = codec.decodeBindingResponse(datagram, transactionId);

      expect(decoded, isNotNull);
      expect(decoded!.mapped, _v4([192, 0, 2, 55], 51234));
    });

    test('decodes OTHER-ADDRESS alongside the mapped address', () {
      final datagram = _successResponse(
        transactionId: transactionId,
        mapped: _v4([192, 0, 2, 55], 51234),
        other: alternate,
      );

      final decoded = codec.decodeBindingResponse(datagram, transactionId);

      expect(decoded!.otherAddress, alternate);
    });

    test('rejects a response for a different transaction', () {
      final datagram = _successResponse(
        transactionId: transactionId,
        mapped: _v4([192, 0, 2, 55], 51234),
      );
      final other = Uint8List.fromList(List<int>.filled(12, 9));

      expect(codec.decodeBindingResponse(datagram, other), isNull);
    });

    test('returns null rather than throwing on malformed input', () {
      // The probe parses bytes from the open internet. Every failure must be a null.
      expect(codec.decodeBindingResponse(Uint8List(0), transactionId), isNull);
      expect(codec.decodeBindingResponse(Uint8List(19), transactionId), isNull);

      final truncated = _successResponse(
        transactionId: transactionId,
        mapped: _v4([192, 0, 2, 55], 51234),
      );
      expect(
        codec.decodeBindingResponse(
          Uint8List.sublistView(truncated, 0, truncated.length - 4),
          transactionId,
        ),
        isNull,
      );

      final wrongCookie = Uint8List.fromList(truncated)..[4] = 0x00;
      expect(codec.decodeBindingResponse(wrongCookie, transactionId), isNull);
    });
  });

  group('StunServerList', () {
    const list = StunServerList();

    test('parses IPv4 and bracketed IPv6 entries', () {
      expect(list.parseOne('198.51.100.10:3478'), primary);
      expect(
        list.parseOne('[2001:db8::1]:3478'),
        StunEndpoint(
          Uint8List.fromList([
            0x20, 0x01, 0x0d, 0xb8, 0, 0, 0, 0, //
            0, 0, 0, 0, 0, 0, 0, 0x01,
          ]),
          3478,
        ),
      );
    });

    test('rejects hostnames, bad ports, and malformed addresses', () {
      // Hostname resolution is deliberately out of scope.
      expect(list.parseOne('stun.example.com:3478'), isNull);
      expect(list.parseOne('198.51.100.10'), isNull);
      expect(list.parseOne('198.51.100.10:0'), isNull);
      expect(list.parseOne('198.51.100.10:70000'), isNull);
      expect(list.parseOne('198.51.100.999:3478'), isNull);
      expect(list.parseOne(''), isNull);
    });

    test('skips unparsable entries instead of failing the whole list', () {
      expect(list.parse(['bad', '198.51.100.10:3478']), [primary]);
    });
  });

  group('ReachabilityProbeRunner', () {
    const runner = ReachabilityProbeRunner();

    test('reports no response when nothing answers', () async {
      final transport = _FakeStunTransport(mappedFor: const {});

      final observation = await runner.run(transport, [primary]);

      expect(observation.respondedAtAll, isFalse);
      expect(
        const ReachabilityClassifier().classify(observation).reachability,
        HostReachability.udpBlocked,
      );
    });

    test('classifies an endpoint-independent NAT as hole-punchable', () async {
      final reflexive = _v4([192, 0, 2, 55], 51234);
      final transport = _FakeStunTransport(
        mappedFor: {
          primary: reflexive,
          alternateOnPrimaryPort: reflexive,
          alternate: reflexive,
        },
        otherAddress: alternate,
      );

      final observation = await runner.run(transport, [primary]);

      expect(observation.completedMappingTransactions, 3);
      expect(observation.sameMappingAcrossServerAddresses, isTrue);
      expect(observation.sameMappingAcrossServerPorts, isTrue);
      expect(
        const ReachabilityClassifier().classify(observation).reachability,
        HostReachability.holePunchable,
      );
    });

    test('classifies a symmetric NAT as needing a relay', () async {
      final transport = _FakeStunTransport(
        mappedFor: {
          primary: _v4([192, 0, 2, 55], 51234),
          alternateOnPrimaryPort: _v4([192, 0, 2, 55], 51235),
          alternate: _v4([192, 0, 2, 55], 51236),
        },
        otherAddress: alternate,
      );

      final observation = await runner.run(transport, [primary]);

      expect(observation.sameMappingAcrossServerAddresses, isFalse);
      expect(observation.sameMappingAcrossServerPorts, isFalse);
      final classification = const ReachabilityClassifier().classify(
        observation,
      );
      expect(classification.reachability, HostReachability.relayRequired);
      expect(
        classification.mapping,
        NatMappingBehavior.addressAndPortDependent,
      );
    });

    test('classifies an unmapped host as directly reachable', () async {
      final local = _v4([192, 0, 2, 55], 51234);
      final transport = _FakeStunTransport(
        mappedFor: {primary: local},
        localEndpoints: [local],
      );

      final observation = await runner.run(transport, [primary]);

      expect(observation.mappedMatchesLocalEndpoint, isTrue);
      expect(
        const ReachabilityClassifier().classify(observation).reachability,
        HostReachability.direct,
      );
    });

    test(
      'leaves mapping unknown without an alternate server address',
      () async {
        final transport = _FakeStunTransport(
          mappedFor: {
            primary: _v4([192, 0, 2, 55], 51234),
          },
        );

        final observation = await runner.run(transport, [primary]);

        expect(observation.completedMappingTransactions, 1);
        expect(
          const ReachabilityClassifier().classify(observation).reachability,
          HostReachability.unknown,
        );
      },
    );

    test(
      'records filtering behaviour from the CHANGE-REQUEST probes',
      () async {
        final reflexive = _v4([192, 0, 2, 55], 51234);
        final transport = _FakeStunTransport(
          mappedFor: {primary: reflexive},
          answersChangePort: true,
        );

        final observation = await runner.run(transport, [primary]);

        expect(observation.acceptedFromUnsolicitedAddress, isFalse);
        expect(observation.acceptedFromUnsolicitedPort, isTrue);
        expect(
          const ReachabilityClassifier().classify(observation).filtering,
          NatFilteringBehavior.addressDependent,
        );
      },
    );

    test(
      'records endpoint-independent filtering for a full-cone NAT',
      () async {
        final transport = _FakeStunTransport(
          mappedFor: {
            primary: _v4([192, 0, 2, 55], 51234),
          },
          answersChangeAddress: true,
          answersChangePort: true,
        );

        final observation = await runner.run(transport, [primary]);

        expect(observation.acceptedFromUnsolicitedAddress, isTrue);
        expect(
          const ReachabilityClassifier().classify(observation).filtering,
          NatFilteringBehavior.endpointIndependent,
        );
      },
    );

    test('falls through to the next server when the first is silent', () async {
      final reflexive = _v4([192, 0, 2, 55], 51234);
      final second = _v4([198, 51, 100, 11], 3478);
      final transport = _FakeStunTransport(mappedFor: {second: reflexive});

      final observation = await runner.run(transport, [primary, second]);

      expect(observation.respondedAtAll, isTrue);
    });
  });

  _reachabilityProbeServiceTests();
}

/// Builds a Binding Success Response carrying XOR-MAPPED-ADDRESS and optionally OTHER-ADDRESS.
Uint8List _successResponse({
  required Uint8List transactionId,
  required StunEndpoint mapped,
  StunEndpoint? other,
}) {
  const magic = 0x2112A442;
  final attributes = <int>[];

  void addAddress(int type, StunEndpoint endpoint, {required bool xor}) {
    final port = xor ? endpoint.port ^ ((magic >> 16) & 0xFFFF) : endpoint.port;
    final address = Uint8List.fromList(endpoint.address);
    if (xor) {
      for (var index = 0; index < address.length; index++) {
        address[index] ^= index < 4
            ? (magic >> (24 - (index * 8))) & 0xFF
            : transactionId[index - 4];
      }
    }
    final value = <int>[
      0,
      endpoint.isIPv4 ? 0x01 : 0x02,
      port >> 8,
      port & 0xFF,
      ...address,
    ];
    attributes.addAll([
      type >> 8,
      type & 0xFF,
      value.length >> 8,
      value.length & 0xFF,
      ...value,
    ]);
  }

  addAddress(0x0020, mapped, xor: true);
  if (other != null) addAddress(0x802C, other, xor: false);

  return Uint8List.fromList([
    0x01,
    0x01,
    attributes.length >> 8,
    attributes.length & 0xFF,
    0x21,
    0x12,
    0xA4,
    0x42,
    ...transactionId,
    ...attributes,
  ]);
}
