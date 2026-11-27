import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'multiplayer_network_support.dart';

/// The launcher's known-hosts record keeps its pins in any valid layout: a
/// record another program rewrote with white space, a byte-order mark or its
/// members in another order reads whole, is kept aside as it was, and is
/// rewritten in canonical form, every pin with it.
void main() {
  late Directory root;
  late String folder;
  final now = DateTime.utc(2026, 10, 22, 12);
  final pinned = JoinAddressTarget(host: '192.168.1.20', port: 27250);
  final other = JoinAddressTarget(host: '192.168.1.30', port: 27250);
  final fresh = JoinAddressTarget(host: '192.168.1.40', port: 27250);
  final pins = [(pinned, hostAServerId), (other, hostBServerId)];

  setUp(() {
    root = Directory.systemTemp.createTempSync('tf-mp-layout-');
    folder = p.join(root.resolveSymbolicLinksSync(), 'multiplayer');
  });
  tearDown(() => root.deleteSync(recursive: true));

  MultiplayerStore store() => MultiplayerStore(folder, clock: () => now);
  File knownHostsFile() => File(p.join(folder, 'known-hosts.json'));
  List<File> damagedCopies() => Directory(folder)
      .listSync()
      .whereType<File>()
      .where((file) => p.basename(file.path).startsWith('known-hosts.damaged-'))
      .toList();

  for (final MapEntry(key: layout, value: bytes) in layouts(pins).entries) {
    test('a record with $layout keeps every pin, is kept aside as it was, '
        'and is rewritten canonically', () async {
      Directory(folder).createSync(recursive: true);
      knownHostsFile().writeAsBytesSync(bytes);

      final hosts = await store().loadKnownHosts();
      expect(hosts.length, 2);
      expect(hosts.check(pinned, hostAServerId), KnownHostCheck.match);
      expect(hosts.check(other, hostBServerId), KnownHostCheck.match);
      expect(
        hosts.check(pinned, hostBServerId),
        KnownHostCheck.mismatch,
        reason: 'another server at a pinned address is a change',
      );
      final copies = damagedCopies();
      expect(copies, hasLength(1));
      expect(copies.single.readAsBytesSync(), bytes);
      final written = knownHostsFile().readAsBytesSync();
      expect(written, laid(pins));
      expect(
        CanonicalJson.decode(written, maximumBytes: KnownHosts.maximumBytes),
        isA<Map<String, Object?>>(),
      );

      final updated = await store().updateKnownHosts(
        (current) => current.remember(fresh, hostAServerId, now),
      );
      expect(updated.length, 3);
      expect(updated.check(pinned, hostAServerId), KnownHostCheck.match);
      expect(
        damagedCopies(),
        hasLength(1),
        reason: 'a canonical record is not copied',
      );
    });
  }
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
  return Uint8List.fromList(
    utf8.encode(
      block(
        '{',
        '}',
        ordered([
          member('hosts', block('[', ']', entries, 1)),
          member('schema', '"${KnownHosts.schema}"'),
        ]),
        0,
      ),
    ),
  );
}

Uint8List bom(List<int> bytes) =>
    Uint8List.fromList([0xEF, 0xBB, 0xBF, ...bytes]);
