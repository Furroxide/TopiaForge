import 'dart:convert';
import 'dart:typed_data';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_fixtures.dart';

void main() {
  Object? decode(List<int> bytes, [int maximum = 1024]) =>
      CanonicalJson.decode(bytes, maximumBytes: maximum);

  final refusal = throwsA(isA<CanonicalJsonException>());

  group('canonical JSON writer', () {
    test('sorts keys, escapes only control characters, keeps UTF-8', () {
      final value = {
        'b': 'tab\tquote"slash/backslash\\bell\u0007del\u007f\u00e9\u{1F600}',
        'a': -9007199254740991,
        'c': [true, null, 0],
      };
      const expected =
          '{"a":-9007199254740991,"b":"tab\\tquote\\"slash/backslash\\\\bell'
          '\\u0007del\u007f\u00e9\u{1F600}","c":[true,null,0]}';
      final bytes = CanonicalJson.encode(value);
      expect(utf8.decode(bytes), expected);
      expect(decode(bytes), value);
      expect(
        (jsonDecode(utf8.decode(bytes)) as Map)['b'],
        value['b'],
        reason: 'a general JSON reader reads the same string',
      );
    });

    test('escapes every ASCII code unit exactly as the shared form says', () {
      for (var unit = 0; unit < 0x80; unit++) {
        final expected = switch (unit) {
          0x22 => r'\"',
          0x5C => r'\\',
          0x08 => r'\b',
          0x09 => r'\t',
          0x0A => r'\n',
          0x0C => r'\f',
          0x0D => r'\r',
          _ when unit < 0x20 =>
            '\\u00${unit.toRadixString(16).padLeft(2, '0')}',
          _ => String.fromCharCode(unit),
        };
        final text = CanonicalJson.encodeToString(String.fromCharCode(unit));
        expect(text, '"$expected"', reason: 'U+${unit.toRadixString(16)}');
        expect(decode(utf8.encode(text)), String.fromCharCode(unit));
      }
      for (final text in ['\u0080', '\u2028', '\ufeff', '\u{10FFFF}']) {
        expect(CanonicalJson.encodeToString(text), '"$text"');
      }
    });

    test('agrees with jsonEncode on escapes but not on what jsonEncode '
        'gets wrong', () {
      for (var unit = 0; unit < 0xD800; unit += 7) {
        final text = String.fromCharCode(unit);
        expect(CanonicalJson.encodeToString(text), jsonEncode(text));
      }
      // jsonEncode keeps insertion order, writes doubles, and escapes a lone
      // surrogate where the shared form refuses it.
      expect(jsonEncode({'b': 1, 'a': 2}), '{"b":1,"a":2}');
      expect(CanonicalJson.encodeToString({'b': 1, 'a': 2}), '{"a":2,"b":1}');
      expect(jsonEncode(1.0), '1.0');
      expect(() => CanonicalJson.encode(1.0), refusal);
      expect(jsonEncode('\ud800'), r'"\ud800"');
      expect(() => CanonicalJson.encode('\ud800'), refusal);
    });

    test('orders keys by code unit, so case is significant', () {
      expect(
        CanonicalJson.encodeToString({'a': 1, 'B': 2, 'A': 3, '1': 4}),
        '{"1":4,"A":3,"B":2,"a":1}',
      );
    });

    test('refuses what the reader would refuse', () {
      expect(() => CanonicalJson.encode({'bad key': 1}), refusal);
      expect(() => CanonicalJson.encode({'': 1}), refusal);
      expect(() => CanonicalJson.encode({'k' * 65: 1}), refusal);
      expect(() => CanonicalJson.encode({1: 1}), refusal);
      expect(() => CanonicalJson.encode(9007199254740992), refusal);
      expect(() => CanonicalJson.encode(-9007199254740992), refusal);
      expect(() => CanonicalJson.encode(2.0), refusal);
      expect(() => CanonicalJson.encode(DateTime(2026)), refusal);
      expect(() => CanonicalJson.encode(['\udc00\ud800']), refusal);
      expect(CanonicalJson.encodeToString({'k' * 64: 1}), '{"${'k' * 64}":1}');
      expect(CanonicalJson.isKey('a1Z9'), isTrue);
      expect(CanonicalJson.isKey('a_b'), isFalse);
      expect(CanonicalJson.isKey('\u00e9'), isFalse);
    });
  });

  group('canonical JSON reader', () {
    test('accepts only the one canonical spelling', () {
      const refused = {
        '{"a":1 }': 'white space',
        ' {"a":1}': 'leading white space',
        '{"a":true} ': 'trailing white space',
        '{"b":1,"a":2}': 'unsorted keys',
        '{"a":2,"A":1}': 'keys sorted without regard to case',
        '{"a":1,"a":1}': 'a repeated key',
        '{"a-b":1}': 'a key outside letters and digits',
        '{"":1}': 'an empty key',
        '{"a":1.5}': 'a fraction',
        '{"a":1e3}': 'an exponent',
        '{"a":1E3}': 'an upper-case exponent',
        '{"a":01}': 'a leading zero',
        '{"a":-0}': 'negative zero',
        '{"a":+1}': 'a plus sign',
        '{"a":-}': 'a lone minus',
        '{"a":9007199254740992}': 'an integer past 2^53 - 1',
        '{"a":-9007199254740992}': 'an integer below -(2^53 - 1)',
        '{"a":123456789012345678}': 'too many digits',
        '{"a":"\\u0041"}': 'an escape for a printable character',
        '{"a":"\\u001F"}': 'an upper-case escape',
        '{"a":"\\u000a"}': 'a hexadecimal escape where \\n exists',
        '{"a":"\\/"}': 'an escaped slash',
        '{"a":"\\x"}': 'an unknown escape',
        '{"a":"\\u12"}': 'a truncated escape',
        '{"a":"\\ud83d"}': 'an unpaired high surrogate',
        '{"a":"\\ude00"}': 'an unpaired low surrogate',
        '{"a":"\\ude00\\ud83d"}': 'a reversed pair',
        '{"a":"\\ud83d\\ude00"}': 'an escaped pair, which is written raw',
        '{"a":"\u0001"}': 'a raw control character',
        '{"a":"abc}': 'an unterminated string',
        '[1,]': 'a trailing comma',
        '[,1]': 'a leading comma',
        '{"a":tru}': 'a broken literal',
        '{"a":nul}': 'a broken null',
        '{"a" :1}': 'white space before a colon',
        '{"a"1}': 'a missing colon',
        '{"a":1}{"b":2}': 'trailing content',
        '{a:1}': 'an unquoted key',
        "{'a':1}": 'single quotes',
        '[1 2]': 'a missing comma',
      };
      refused.forEach((text, why) {
        expect(() => decode(textBytes(text)), refusal, reason: why);
      });
      for (final text in [
        '{"A":1,"a":2}',
        '{"1":1,"a":2}',
        '[]',
        '{}',
        '""',
        '0',
        '-1',
        '9007199254740991',
        '-9007199254740991',
        'true',
        'null',
        '"\u007f\u00e9\u{1F600}"',
        '{"a":[{"b":null}]}',
      ]) {
        expect(
          CanonicalJson.encode(decode(textBytes(text))),
          textBytes(text),
          reason: text,
        );
      }
    });

    test('refuses a byte-order mark, malformed UTF-8 and empty input', () {
      expect(
        () => decode([0xEF, 0xBB, 0xBF, ...textBytes('{"a":1}')]),
        refusal,
      );
      expect(() => decode([0x22, 0xC3, 0x28, 0x22]), refusal);
      expect(() => decode([0x22, 0xED, 0xA0, 0x80, 0x22]), refusal);
      expect(() => decode([0x22, 0xC0, 0xA2, 0x22]), refusal);
      expect(() => decode([0x22, 0xFF, 0x22]), refusal);
      expect(() => decode(const []), refusal);
      expect(() => decode(const [0x22, 0x161, 0x22]), refusal);
      expect(() => decode(const [0x30, -1]), refusal);
    });

    test('enforces the size bound before parsing', () {
      final exact = textBytes('"${'x' * 98}"');
      expect(exact, hasLength(100));
      expect(decode(exact, 100), 'x' * 98);
      expect(() => decode(exact, 99), refusal);
      expect(
        () => decode(Uint8List(1025)..fillRange(0, 1025, 0x20)),
        throwsA(
          isA<CanonicalJsonException>().having(
            (error) => error.message,
            'message',
            contains('larger'),
          ),
        ),
      );
    });

    test('nests at most 16 deep, the same for reader and writer', () {
      String nested(int arrays, {bool scalar = false}) =>
          '${'[' * arrays}${scalar ? '1' : ''}${']' * arrays}';
      Object? list(int arrays) => arrays == 0 ? 1 : [list(arrays - 1)];

      expect(decode(textBytes(nested(17))), isA<List<Object?>>());
      expect(() => decode(textBytes(nested(18))), refusal);
      expect(decode(textBytes(nested(16, scalar: true))), isA<List<Object?>>());
      expect(() => decode(textBytes(nested(17, scalar: true))), refusal);
      expect(CanonicalJson.encodeToString(list(16)), nested(16, scalar: true));
      expect(() => CanonicalJson.encode(list(17)), refusal);
    });

    test('returns unmodifiable maps and lists', () {
      final value = decode(textBytes('{"a":[1]}'))! as Map<String, Object?>;
      expect(() => value['b'] = 1, throwsUnsupportedError);
      expect(() => (value['a']! as List).add(2), throwsUnsupportedError);
    });
  });

  group('shared fixtures', () {
    final descriptors = readMultiplayerFixture('descriptor/cases.json');
    final lan = readMultiplayerFixture('lan/cases.json');

    test('every canonical document in the fixtures reads back to itself', () {
      final documents = [
        for (final item in asObjects(descriptors['valid'])) ...[
          item['canonicalDescriptor']! as String,
          item['signedBytes']! as String,
        ],
        for (final item in asObjects(asObject(lan['sessionInfo'])['valid']))
          item['canonical']! as String,
      ];
      expect(documents, hasLength(9));
      for (final text in documents) {
        final bytes = textBytes(text);
        expect(CanonicalJson.encode(decode(bytes, 262144)), bytes);
      }
    });

    test('the writer reproduces each fixture descriptor from its members', () {
      for (final item in asObjects(descriptors['valid'])) {
        expect(
          CanonicalJson.encodeToString(item['descriptor']),
          item['canonicalDescriptor'],
          reason: item['id']! as String,
        );
      }
    });

    test('the canonical-form tampering cases fail at the reader', () {
      const readerCases = {
        'whitespace',
        'escaped-slash',
        'uppercase-unicode-escape',
        'members-out-of-order',
        'float-number',
        'negative-zero',
        'lone-surrogate',
        'duplicate-key',
        'invalid-utf8',
      };
      final seen = <String>{};
      for (final item in asObjects(descriptors['invalid'])) {
        final id = item['id']! as String;
        if (!readerCases.contains(id)) continue;
        final bytes = item.containsKey('bytesHex')
            ? hexBytes(item['bytesHex']! as String)
            : textBytes(item['bytes']! as String);
        expect(() => decode(bytes, 262144), refusal, reason: id);
        seen.add(id);
      }
      expect(seen, readerCases);
    });
  });
}
