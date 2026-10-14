import 'dart:convert';
import 'dart:io';
import 'dart:math';
import 'dart:typed_data';

import 'package:crypto/crypto.dart' as crypto;
import 'package:cryptography/cryptography.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

/// Replays tests/fixtures/multiplayer/descriptor/cases.json with real
/// Ed25519, as the game's C# tests do with their own implementation.
void main() {
  final fixture = _readFixture('descriptor/cases.json');
  final keys = {
    for (final key in _objects(fixture['keys'])) key['id']! as String: key,
  };
  const verifier = ServerDescriptorVerifier(
    Ed25519DescriptorSignatureVerifier(),
  );

  group('valid descriptors', () {
    for (final item in _objects(fixture['valid'])) {
      final id = item['id']! as String;
      test('$id verifies under its signing key', () async {
        final bytes = _utf8(item['signedBytes']! as String);
        final accepted =
            await verifier.verify(
                  bytes,
                  expectedServerId: item['serverId']! as String,
                )
                as ServerDescriptorAccepted;
        expect(accepted.descriptor.descriptorSha256, item['descriptorSha256']);
        expect(accepted.descriptor.serverId, item['serverId']);
        expect(accepted.descriptor.packageDigest, item['packageDigest']);
        expect(await verifier.verify(bytes), isA<ServerDescriptorAccepted>());
      });

      test('$id is reproduced by signing it again', () async {
        final key = keys[item['key']]!;
        final algorithm = Ed25519();
        final pair = await algorithm.newKeyPairFromSeed(
          _hex(key['signingSeed']! as String),
        );
        final publicKey = await pair.extractPublicKey();
        expect(WireText.toBase64Url(publicKey.bytes), key['signingKey']);
        final envelope = SignedServerDescriptorV1.parse(
          _utf8(item['signedBytes']! as String),
        );
        final signature = await algorithm.sign([
          ...utf8.encode(fixture['context']! as String),
          ...envelope.descriptor.canonicalBytes,
        ], keyPair: pair);
        expect(WireText.toBase64Url(signature.bytes), item['signature']);
        expect(
          SignedServerDescriptorV1.assemble(
            envelope.descriptor,
            signature.bytes,
          ).bytes,
          _utf8(item['signedBytes']! as String),
        );
      });
    }
  });

  test(
    'every tampered descriptor is refused, each for its own reason',
    () async {
      const signatureCases = {
        'signature-flipped',
        'signature-without-context',
        'signed-by-other-key',
      };
      final cases = _objects(fixture['invalid']);
      expect(cases.length, greaterThanOrEqualTo(30));
      for (final item in cases) {
        final id = item['id']! as String;
        final bytes = item.containsKey('bytesHex')
            ? _hex(item['bytesHex']! as String)
            : _utf8(item['bytes']! as String);
        final result = await verifier.verify(
          bytes,
          expectedServerId: item['expectedServerId'] as String?,
        );
        expect(result, isA<ServerDescriptorRefused>(), reason: id);
        expect(
          (result as ServerDescriptorRefused).refusal,
          signatureCases.contains(id)
              ? ServerDescriptorRefusal.badSignature
              : id == 'other-server-expected'
              ? ServerDescriptorRefusal.otherServer
              : ServerDescriptorRefusal.malformed,
          reason: '$id: ${item['about']}',
        );
      }
    },
  );

  test('any flipped signature bit is refused', () async {
    final valid = _objects(fixture['valid']).first;
    final envelope = SignedServerDescriptorV1.parse(
      _utf8(valid['signedBytes']! as String),
    );
    for (final index in [0, 1, 31, 32, 33, 62, 63]) {
      for (final bit in [0x01, 0x80]) {
        final signature = Uint8List.fromList(envelope.signature);
        signature[index] ^= bit;
        final tampered = SignedServerDescriptorV1.assemble(
          envelope.descriptor,
          signature,
        );
        expect(
          await verifier.verify(tampered.bytes),
          isA<ServerDescriptorRefused>(),
          reason: 'byte $index, bit $bit',
        );
      }
    }
  });

  group('small-order signing keys', () {
    test('are refused although plain Ed25519 accepts a forgery', () async {
      // Under the identity point every message verifies with R = identity
      // and S = 0, so the key proves nothing about who signed.
      final identity = Uint8List(32)..[0] = 1;
      final descriptor = ServerDescriptorV1(
        signingKey: identity,
        transportKey: Uint8List(32)..[0] = 9,
        issuedAt: 1791849600,
        name: 'Forged',
        gameBuild: '2545',
        runtimeVersion: '0.2.0',
        protocolVersion: '1.0.0',
        maxPlayers: 8,
        passwordRequired: false,
        voice: false,
        visibility: ServerVisibility.public,
        hostServesPackages: false,
        gamemode: null,
        world: null,
        packages: const [],
      );
      final forgery = Uint8List(64)..[0] = 1;
      final envelope = SignedServerDescriptorV1.assemble(descriptor, forgery);
      expect(
        await Ed25519().verify(
          envelope.signedMessage,
          signature: Signature(
            forgery,
            publicKey: SimplePublicKey(identity, type: KeyPairType.ed25519),
          ),
        ),
        isTrue,
        reason: 'the library alone accepts the forgery',
      );
      final refused =
          await verifier.verify(
                envelope.bytes,
                expectedServerId: descriptor.serverId,
              )
              as ServerDescriptorRefused;
      expect(refused.refusal, ServerDescriptorRefusal.badSignature);
    });

    test('cover the eight points of order dividing 8, and no real key', () {
      const smallOrderY = [
        '0000000000000000000000000000000000000000000000000000000000000000',
        '0100000000000000000000000000000000000000000000000000000000000000',
        '26e8958fc2b227b045c3f489f2ef98f0d5dfac05d3c63339b13802886d53fc05',
        'c7176a703d4dd84fba3c0b760d10670f2a2053fa2c39ccc64ec7fd7792ac037a',
        'ecffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff7f',
      ];
      for (final y in smallOrderY) {
        final key = _hex(y);
        expect(Ed25519DescriptorSignatureVerifier.hasSmallOrder(key), isTrue);
        key[31] |= 0x80;
        expect(Ed25519DescriptorSignatureVerifier.hasSmallOrder(key), isTrue);
      }
      for (final key in keys.values) {
        final bytes = WireText.tryParseBase64Url(
          key['signingKey']! as String,
          32,
        )!;
        expect(
          Ed25519DescriptorSignatureVerifier.hasSmallOrder(bytes),
          isFalse,
        );
      }
      expect(
        Ed25519DescriptorSignatureVerifier.hasSmallOrder(Uint8List(31)),
        isFalse,
      );
    });
  });

  test('the domain SHA-256 matches package:crypto at every padding '
      'boundary', () {
    final random = Random(20261013);
    for (var length = 0; length <= 300; length++) {
      final data = Uint8List.fromList(
        List.generate(length, (_) => random.nextInt(256)),
      );
      expect(
        PureSha256.hex(data),
        crypto.sha256.convert(data).toString(),
        reason: '$length bytes',
      );
    }
    for (final length in [1023, 1024, 1025, 65536, 262145]) {
      final data = Uint8List.fromList(
        List.generate(length, (index) => (index * 31 + 7) & 0xFF),
      );
      expect(
        PureSha256.hex(data),
        crypto.sha256.convert(data).toString(),
        reason: '$length bytes',
      );
    }
  });
}

Map<String, Object?> _readFixture(String relative) {
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
  return _object(jsonDecode(File(path).readAsStringSync()));
}

Map<String, Object?> _object(Object? value) =>
    (value! as Map).map((key, item) => MapEntry(key as String, item));

List<Map<String, Object?>> _objects(Object? value) =>
    (value! as List<Object?>).map(_object).toList();

Uint8List _utf8(String text) => Uint8List.fromList(utf8.encode(text));

Uint8List _hex(String text) => Uint8List.fromList([
  for (var index = 0; index < text.length; index += 2)
    int.parse(text.substring(index, index + 2), radix: 16),
]);
