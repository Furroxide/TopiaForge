import 'dart:convert';
import 'dart:typed_data';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_fixtures.dart';

void main() {
  group('PureSha256', () {
    test('matches the FIPS 180-4 example vectors', () {
      const vectors = {
        '': 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855',
        'abc':
            'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad',
        'abcdbcdecdefdefgefghfghighijhijkijkljklmklmnlmnomnopnopq':
            '248d6a61d20638b8e5c026930c3e6039a33ce45964ff2167f6ecedd419db06c1',
        'abcdefghbcdefghicdefghijdefghijkefghijklfghijklmghijklmnhijklmno'
                'ijklmnopjklmnopqklmnopqrlmnopqrsmnopqrstnopqrstu':
            'cf5b16a778af8380036ce59e7b0492370b249b11e8f07a51afac45037afee9d1',
      };
      vectors.forEach((message, digest) {
        expect(PureSha256.hex(ascii.encode(message)), digest, reason: message);
      });
      expect(
        PureSha256.hex(Uint8List(1000000)..fillRange(0, 1000000, 0x61)),
        'cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0',
      );
      expect(PureSha256.hash(const []), hasLength(32));
    });

    test('derives the fixture server ids and the empty package digest', () {
      final fixture = readMultiplayerFixture('descriptor/cases.json');
      for (final key in asObjects(fixture['keys'])) {
        final signingKey = WireText.tryParseBase64Url(
          key['signingKey']! as String,
          32,
        )!;
        expect(PureSha256.hex(signingKey), key['serverId']);
        expect(ServerDescriptorV1.serverIdOf(signingKey), key['serverId']);
      }
      expect(
        ServerDescriptorV1.packageDigestOf(const []),
        PureSha256.hex(ascii.encode('[]')),
      );
      for (final item in asObjects(fixture['valid'])) {
        expect(
          PureSha256.hex(textBytes(item['signedBytes']! as String)),
          item['descriptorSha256'],
        );
      }
    });

    test('refuses values that are not bytes', () {
      expect(() => PureSha256.hash(const [256]), throwsArgumentError);
      expect(() => PureSha256.hash(const [-1]), throwsArgumentError);
    });
  });

  group('WireText', () {
    const alphabet =
        'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_';
    final bytes = Uint8List.fromList([
      for (var index = 0; index < 32; index++) (index * 7) & 0xFF,
    ]);

    test('base64url is the standard alphabet, unpadded, one spelling', () {
      final text = WireText.toBase64Url(bytes);
      expect(text, base64Url.encode(bytes).replaceAll('=', ''));
      expect(WireText.tryParseBase64Url(text, 32), bytes);
      for (final size in [1, 2, 31, 32, 64]) {
        final value = WireText.toBase64Url(List.filled(size, 0xA5));
        expect(
          WireText.tryParseBase64Url(value, size),
          List.filled(size, 0xA5),
        );
        if ((size * 8) % 6 == 0) continue;
        final last = alphabet.indexOf(value[value.length - 1]);
        final noisy = value.substring(0, value.length - 1) + alphabet[last | 1];
        expect(
          WireText.tryParseBase64Url(noisy, size),
          isNull,
          reason: 'a set unused bit for $size bytes',
        );
      }
      expect(WireText.tryParseBase64Url('$text=', 32), isNull);
      expect(
        WireText.tryParseBase64Url(text.substring(0, text.length - 1), 32),
        isNull,
      );
      expect(WireText.tryParseBase64Url(text.replaceAll('A', '+'), 32), isNull);
      expect(WireText.tryParseBase64Url(text.replaceAll('A', '/'), 32), isNull);
      expect(WireText.tryParseBase64Url(text, 31), isNull);
      expect(WireText.tryParseBase64Url(null, 32), isNull);
      expect(WireText.toBase64Url(const []), '');
    });

    test('hexadecimal is lowercase and exact in length', () {
      final text = WireText.toHex(bytes);
      expect(
        text,
        bytes.map((b) => b.toRadixString(16).padLeft(2, '0')).join(),
      );
      expect(WireText.tryParseHex(text, 32), bytes);
      expect(WireText.isHex(text, 32), isTrue);
      expect(WireText.isHex(text.toUpperCase(), 32), isFalse);
      expect(WireText.isHex(text.substring(2), 32), isFalse);
      expect(WireText.isHex('${text}00', 32), isFalse);
      expect(WireText.isHex('g${text.substring(1)}', 32), isFalse);
      expect(WireText.isHex(null, 32), isFalse);
      expect(() => WireText.toHex(const [256]), throwsArgumentError);
    });

    test('display text follows the rules the game applies', () {
      for (final text in [
        'a',
        'x' * 48,
        '東' * 48,
        "Zoë's server",
        '\u{1F600}',
        'a b',
        'a b',
        // Not white space or control characters to .NET either.
        '​a',
        '᠎a',
        '﻿a',
      ]) {
        expect(WireText.isDisplayText(text, 48), isTrue, reason: text);
      }
      for (final text in [
        '',
        'x' * 49,
        ' a',
        'a ',
        '\ta',
        'a\n',
        'a ',
        ' a',
        ' a',
        'a ',
        'a ',
        ' a',
        ' a',
        ' a',
        '　a',
        'bad\u0007name',
        'a\u007fb',
        'a\u0085b',
        'a\u009fb',
        '\ud800',
        'a\udc00',
        'a\ud800',
        '\udc00\ud800',
      ]) {
        expect(
          WireText.isDisplayText(text, 48),
          isFalse,
          reason: text.codeUnits.toString(),
        );
      }
      expect(WireText.isDisplayText('\u{1F600}' * 24, 48), isTrue);
      expect(WireText.isDisplayText('\u{1F600}' * 25, 48), isFalse);
      expect(WireText.isDisplayText('a', 24, minimum: 2), isFalse);
      expect(WireText.isDisplayText(null, 24), isFalse);
    });
  });
}
