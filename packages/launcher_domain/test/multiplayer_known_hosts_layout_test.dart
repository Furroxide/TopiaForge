import 'dart:convert';
import 'dart:typed_data';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_fixtures.dart';
import 'multiplayer_join_support.dart';

/// The known-hosts record keeps its pins in any valid layout, as the game's
/// does: a record another program rewrote with white space, a byte-order mark
/// or its members in another order reads whole. Every signed or hashed
/// document is still read in canonical form only.
void main() {
  const otherServer =
      '9f0b4c51e77a0e2c36bd4de2a8b0f1b2c3d4e5f60718293a4b5c6d7e8f901234';
  final pinned = JoinAddressTarget(host: '192.168.1.20', port: 27250);
  final other = JoinAddressTarget(host: '192.168.1.30', port: 27250);
  final pins = [(pinned, hostAServerId), (other, otherServer)];

  group('the known-hosts record', () {
    test('is built here from its own canonical bytes', () {
      final first = DateTime.fromMillisecondsSinceEpoch(
        1760000000 * 1000,
        isUtc: true,
      );
      final record = KnownHosts.empty()
          .remember(pinned, hostAServerId, first)
          .remember(other, otherServer, first.add(const Duration(seconds: 1)));
      expect(laid(pins), record.encode());
    });

    test('keeps every pin in any valid layout', () {
      final canonical = laid(pins);
      for (final MapEntry(key: layout, value: bytes) in layouts(pins).entries) {
        expect(bytes, isNot(canonical), reason: layout);
        final read = KnownHosts.decode(bytes);
        expect(read.length, 2, reason: 'a record with $layout keeps its pins');
        expect(
          read.check(pinned, hostAServerId),
          KnownHostCheck.match,
          reason: layout,
        );
        expect(
          read.check(pinned, otherServer),
          KnownHostCheck.mismatch,
          reason: '$layout: another server at a pinned address is a change',
        );
        expect(
          read.check(other, otherServer),
          KnownHostCheck.match,
          reason: layout,
        );
        expect(
          read.encode(),
          canonical,
          reason: '$layout: the record is written back in canonical form',
        );
      }
    });

    test('reads a full record in a wide layout within its bound', () {
      final full = [
        for (var index = 0; index < KnownHosts.maximumEntries; index++)
          (
            JoinAddressTarget(host: 'host$index.example', port: 1),
            index.isEven ? hostAServerId : otherServer,
          ),
      ];
      final wide = laid(full, indent: '    ', newline: '\r\n', reversed: true);
      expect(wide.length, lessThanOrEqualTo(KnownHosts.maximumBytes));
      expect(KnownHosts.decode(wide).length, KnownHosts.maximumEntries);
    });

    test('still holds nothing for what is not the record', () {
      final canonical = laid(pins);
      final cases = {
        'a truncated record': canonical.sublist(0, canonical.length - 3),
        'text that is not JSON': textBytes('not json'),
        'a repeated member': textBytes(
          '{"hosts": [], "hosts": [], "schema": "${KnownHosts.schema}"}',
        ),
        'another schema': textBytes(
          '{ "hosts": [], "schema": "topiaforge.known-hosts/2" }',
        ),
        'a byte-order mark only': Uint8List.fromList([0xEF, 0xBB, 0xBF]),
        'white space only': textBytes(' \r\n\t '),
      };
      for (final MapEntry(key: what, value: bytes) in cases.entries) {
        expect(KnownHosts.decode(bytes).length, 0, reason: what);
      }
    });
  });

  group('every other document', () {
    test('is read in canonical form only, in each layout the record reads', () {
      final canonical = laid(pins);
      expect(
        CanonicalJson.decode(canonical, maximumBytes: KnownHosts.maximumBytes),
        isA<Map<String, Object?>>(),
      );
      for (final MapEntry(key: layout, value: bytes) in layouts(pins).entries) {
        expect(
          () => CanonicalJson.decode(
            bytes,
            maximumBytes: KnownHosts.maximumBytes,
          ),
          throwsA(isA<CanonicalJsonException>()),
          reason: 'the canonical reader refuses $layout',
        );
      }
    });

    test('refuses a signed descriptor in another layout', () {
      final fixture = readMultiplayerFixture('descriptor/cases.json');
      final served = textBytes(
        asObjects(fixture['valid']).first['signedBytes']! as String,
      );
      expect(
        SignedServerDescriptorV1.parse(served),
        isA<SignedServerDescriptorV1>(),
      );
      final cases = {
        'a trailing newline': Uint8List.fromList([...served, 0x0A]),
        'a byte-order mark': bom(served),
        'spaces after its separators': spaced(served),
      };
      for (final MapEntry(key: layout, value: bytes) in cases.entries) {
        expect(
          () => SignedServerDescriptorV1.parse(bytes),
          throwsFormatException,
          reason: 'a descriptor with $layout is refused',
        );
      }
    });
  });
}

/// The same record in each layout another program may leave it in, by name.
Map<String, Uint8List> layouts(List<(JoinAddressTarget, String)> pins) {
  final canonical = laid(pins);
  return {
    'a trailing newline': Uint8List.fromList([...canonical, 0x0A]),
    'a trailing CR LF': Uint8List.fromList([...canonical, 0x0D, 0x0A]),
    'a byte-order mark': bom(canonical),
    'two-space indentation': Uint8List.fromList([
      ...laid(pins, indent: '  ', newline: '\n'),
      0x0A,
    ]),
    'tabs and CR LF': laid(pins, indent: '\t', newline: '\r\n'),
    'its members in another order': laid(pins, reversed: true),
    'all of them at once': bom(
      Uint8List.fromList([
        ...laid(pins, indent: '    ', newline: '\r\n', reversed: true),
        0x0D,
        0x0A,
      ]),
    ),
  };
}

/// The record as another writer may lay it out: [indent] per level and
/// [newline] between lines, a space after each colon when indented, and every
/// object's members reversed when asked. With none of them, it is the
/// canonical record.
Uint8List laid(
  List<(JoinAddressTarget, String)> pins, {
  String indent = '',
  String newline = '',
  bool reversed = false,
}) {
  String member(String key, String value) =>
      '"$key":${indent.isEmpty ? '' : ' '}$value';
  String block(String open, String close, List<String> items, int depth) {
    if (items.isEmpty) return '$open$close';
    final inner = newline + indent * (depth + 1);
    final outer = newline + indent * depth;
    return '$open$inner${items.join(',$inner')}$outer$close';
  }

  List<String> ordered(List<String> members) =>
      reversed ? members.reversed.toList() : members;

  var seenAt = 1760000000;
  final entries = [
    for (final (target, serverId) in pins)
      block(
        '{',
        '}',
        ordered([
          member('seenAt', '${seenAt++}'),
          member('serverId', '"$serverId"'),
          member(
            'target',
            '"${KnownHosts.targetKey(target.host, target.port)}"',
          ),
        ]),
        2,
      ),
  ];
  return textBytes(
    block(
      '{',
      '}',
      ordered([
        member('hosts', block('[', ']', entries, 1)),
        member('schema', '"${KnownHosts.schema}"'),
      ]),
      0,
    ),
  );
}

Uint8List bom(List<int> bytes) =>
    Uint8List.fromList([0xEF, 0xBB, 0xBF, ...bytes]);

/// The same document with a space after every colon and comma outside its
/// strings.
Uint8List spaced(List<int> bytes) {
  final text = utf8.decode(bytes);
  final buffer = StringBuffer();
  var inString = false;
  for (var index = 0; index < text.length; index++) {
    final unit = text[index];
    buffer.write(unit);
    if (inString) {
      if (unit == r'\') {
        buffer.write(text[++index]);
      } else if (unit == '"') {
        inString = false;
      }
    } else if (unit == '"') {
      inString = true;
    } else if (unit == ':' || unit == ',') {
      buffer.write(' ');
    }
  }
  return textBytes(buffer.toString());
}
