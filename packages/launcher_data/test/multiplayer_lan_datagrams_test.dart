import 'dart:math';
import 'dart:typed_data';

import 'package:launcher_data/src/multiplayer/lan_datagrams.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_network_support.dart';

void main() {
  final id = [1, 2, 3, 4, 5, 6, 7, 0xFF];

  group('info query', () {
    test(
      'is E4, version 1, the id and zero padding to exactly 1,200 bytes',
      () {
        final query = LanInfoQuery.encode(id);
        expect(query, hasLength(1200));
        expect(query[0], 0xE4);
        expect(query[1], 1);
        expect(query.sublist(2, 10), id);
        expect(query.sublist(10).every((value) => value == 0), isTrue);
      },
    );

    test('refuses an id that is not eight bytes', () {
      expect(() => LanInfoQuery.encode(const [1, 2]), throwsArgumentError);
      expect(
        () => LanInfoQuery.encode(const [1, 2, 3, 4, 5, 6, 7, 256]),
        throwsArgumentError,
      );
    });
  });

  group('info reply', () {
    test('reads a reply with info, and one from another version without', () {
      final info = sessionInfoFixture('typical');
      final reply = LanInfoReply.tryDecode(
        LanInfoReply.encode(queryId: id, info: info),
      )!;
      expect(reply.version, 1);
      expect(reply.isCompatible, isTrue);
      expect(reply.queryId, id);
      expect(reply.info, info);
      expect(reply.sessionInfo!.serverId, hostAServerId);

      final other = LanInfoReply.tryDecode(
        LanInfoReply.encode(queryId: id, info: const [], version: 2),
      )!;
      expect(other.isCompatible, isFalse);
      expect(other.info, isEmpty);
      expect(other.sessionInfo, isNull);
    });

    test('takes info only from a host of the same version', () {
      final reply = LanInfoReply.tryDecode(
        LanInfoReply.encode(
          queryId: id,
          info: sessionInfoFixture('typical'),
          version: 2,
        ),
      )!;
      expect(reply.isCompatible, isFalse);
      expect(reply.sessionInfo, isNull);
    });

    test('accepts a reply exactly as large as the query', () {
      final info = Uint8List(LanInfoReply.maximumInfoBytes);
      final datagram = LanInfoReply.encode(queryId: id, info: info);
      expect(datagram, hasLength(LanInfoQuery.size));
      expect(LanInfoReply.tryDecode(datagram), isNotNull);
      expect(
        () => LanInfoReply.encode(queryId: id, info: Uint8List(1189)),
        throwsArgumentError,
      );
    });

    test('refuses a reply larger than the query, whatever its length says', () {
      for (final size in [1201, 1500, 2000, 65535 + 12]) {
        final datagram = Uint8List(size)
          ..[0] = LanInfoReply.packetType
          ..[1] = LanInfoQuery.version
          ..setRange(2, 10, id)
          ..[10] = (size - 12) & 0xFF
          ..[11] = ((size - 12) >> 8) & 0xFF;
        expect(LanInfoReply.tryDecode(datagram), isNull, reason: '$size');
      }
    });

    test('refuses truncated replies and length fields that disagree', () {
      final valid = LanInfoReply.encode(queryId: id, info: const [1, 2, 3, 4]);
      for (var length = 0; length < valid.length; length++) {
        expect(
          LanInfoReply.tryDecode(valid.sublist(0, length)),
          isNull,
          reason: 'cut to $length bytes',
        );
      }
      for (final claimed in [0, 3, 5, 1188, 0xFFFF]) {
        final datagram = Uint8List.fromList(valid)
          ..[10] = claimed & 0xFF
          ..[11] = claimed >> 8;
        expect(LanInfoReply.tryDecode(datagram), isNull, reason: '$claimed');
      }
    });

    test('refuses another packet type and values beyond a byte', () {
      final valid = LanInfoReply.encode(queryId: id, info: const [7]);
      for (final type in [0xE4, 0xE6, 0x00, 0xFF]) {
        expect(
          LanInfoReply.tryDecode(Uint8List.fromList(valid)..[0] = type),
          isNull,
        );
      }
      expect(LanInfoReply.tryDecode([...valid]..[12] = 256), isNull);
      expect(LanInfoReply.tryDecode([...valid]..[3] = -1), isNull);
    });

    test('never throws on hostile datagrams', () {
      final random = Random(20261015);
      for (var round = 0; round < 4000; round++) {
        final length = random.nextInt(1300);
        final datagram = Uint8List.fromList(
          List.generate(length, (_) => random.nextInt(256)),
        );
        if (length > 0 && random.nextBool()) {
          datagram[0] = LanInfoReply.packetType;
        }
        if (length >= 12 && random.nextBool()) {
          datagram[10] = (length - 12) & 0xFF;
          datagram[11] = (length - 12) >> 8;
        }
        final reply = LanInfoReply.tryDecode(datagram);
        if (reply != null) {
          expect(datagram.length, lessThanOrEqualTo(LanInfoQuery.size));
          expect(reply.sessionInfo, anyOf(isNull, isA<SessionInfo>()));
        }
      }
    });

    test('replays the shared session info, valid and invalid', () {
      final info = asObject(
        readMultiplayerFixture('lan/cases.json')['sessionInfo'],
      );
      expect(info['maximumBytes'], LanInfoReply.maximumInfoBytes);
      for (final item in asObjects(info['valid'])) {
        final bytes = textBytes(item['canonical']! as String);
        final reply = LanInfoReply.tryDecode(
          LanInfoReply.encode(queryId: id, info: bytes),
        )!;
        expect(reply.sessionInfo!.toJson(), asObject(item['info']));
      }
      for (final item in asObjects(info['invalid'])) {
        final reply = LanInfoReply.tryDecode(
          LanInfoReply.encode(
            queryId: id,
            info: textBytes(item['text']! as String),
          ),
        );
        expect(reply, isNotNull);
        expect(reply!.sessionInfo, isNull, reason: item['about']! as String);
      }
    });
  });
}
