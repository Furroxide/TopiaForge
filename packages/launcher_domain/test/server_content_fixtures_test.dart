import 'dart:convert';
import 'dart:typed_data';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_fixtures.dart';

/// Replays tests/fixtures/multiplayer/server-content/cases.json, which the
/// game's C# tests replay too: the launcher writes the record of server
/// content and both read it, so both must take the same documents as
/// records, withhold the same versions from the player's own play, and fail
/// closed on everything else.
void main() {
  final fixture = readMultiplayerFixture('server-content/cases.json');

  test('the limits are the fixture\'s', () {
    final limits = asObject(fixture['limits']);
    expect(limits['schemaVersion'], ServerContentRecord.schemaVersion);
    expect(limits['maximumBytes'], ServerContentRecord.maximumBytes);
    expect(limits['maximumEntries'], ServerContentRecord.maximumEntries);
    expect(limits['maximumSafeInteger'], CanonicalJson.maximumSafeInteger);
  });

  group('valid', () {
    for (final item in asObjects(fixture['valid'])) {
      final bytes = textBytes(item['bytes']! as String);
      test('${item['id']} is written exactly as the launcher writes it', () {
        expect(_fromDocument(asObject(item['document'])).encode(), bytes);
      });
      for (final (spelling, text) in [
        ('canonical', item['bytes']! as String),
        ('reordered', item['reordered']! as String),
      ]) {
        test('${item['id']} reads, $spelling', () {
          final read = ServerContentRecord.read(textBytes(text));
          expect(read.status, ServerContentStatus.valid, reason: read.problem);
          final record = read.record!;
          expect(record.encode(), bytes);
          for (final probe in asObjects(item['hides'])) {
            expect(
              record.withholds(
                probe['id']! as String,
                probe['version']! as String,
                probe['receipt']! as String,
              ),
              probe['hidden'],
              reason: '${probe['id']}@${probe['version']}',
            );
          }
          expect(record.addedRecords.toList()..sort(), item['addedRecords']);
        });
      }
    }
  });

  for (final item in asObjects(fixture['newer'])) {
    test('newer ${item['id']} is never guessed at', () {
      final read = ServerContentRecord.read(textBytes(item['text']! as String));
      expect(read.status, ServerContentStatus.newer);
      expect(read.record, isNull);
      expect(read.problem, isNotEmpty);
    });
  }

  for (final item in asObjects(fixture['invalid'])) {
    test('invalid ${item['id']} is damaged: ${item['about']}', () {
      final read = ServerContentRecord.read(
        item.containsKey('hex')
            ? hexBytes(item['hex']! as String)
            : textBytes(item['text']! as String),
      );
      expect(read.status, ServerContentStatus.damaged);
      expect(read.record, isNull);
      expect(read.problem, isNotEmpty);
    });
  }

  for (final item in asObjects(fixture['generated'])) {
    test('generated ${item['id']}', () {
      final data = _build(item);
      expect(data.length, item['length']);
      expect(PureSha256.hex(data), item['sha256']);
      final read = ServerContentRecord.read(data);
      if (item['valid'] == true) {
        expect(read.status, ServerContentStatus.valid, reason: read.problem);
        if (item['recipe'] == 'maximal') {
          final count = item['entries']! as int;
          expect(read.record!.entries, hasLength(count));
          expect(read.record!.withheld, hasLength(count));
          expect(read.record!.addedRecords, hasLength(count));
          expect(read.record!.encode(), data);
        }
      } else {
        expect(read.status, ServerContentStatus.damaged);
      }
    });
  }
}

/// Builds the record a valid case's document describes, member by member,
/// so the record's own writer is what gets compared.
ServerContentRecord _fromDocument(Map<String, Object?> document) {
  var record = ServerContentRecord.empty().withInstalled(
    [
      for (final item in asObjects(document['versions']))
        ServerContentEntry(
          modId: item['id']! as String,
          version: item['version']! as String,
          sha256: item['sha256']! as String,
          receiptSha256: item['receipt']! as String,
          hidden: item['hidden']! as bool,
          installedAt: item['installedAt']! as int,
        ),
    ],
    addedRecords: [
      for (final id in document['addedRecords']! as List) id as String,
    ],
  );
  record = record.withWithheld([
    for (final item in asObjects(document['withheld']))
      WithheldServerVersion(
        modId: item['id']! as String,
        version: item['version']! as String,
        receiptSha256: item['receipt']! as String,
      ),
  ]);
  return record;
}

Uint8List _build(Map<String, Object?> item) {
  String hex64(int value) => value.toRadixString(16).padLeft(64, '0');
  String canonical({
    Iterable<String> versions = const [],
    Iterable<String> withheld = const [],
    Iterable<String> added = const [],
  }) =>
      '{"addedRecords":[${added.map((id) => '"$id"').join(',')}],'
      '"schemaVersion":${ServerContentRecord.schemaVersion},'
      '"versions":[${versions.join(',')}],"withheld":[${withheld.join(',')}]}';
  String version(String id, String version, String sha, String receipt) =>
      '{"hidden":true,"id":"$id","installedAt":'
      '${CanonicalJson.maximumSafeInteger},"receipt":"$receipt",'
      '"sha256":"$sha","version":"$version"}';
  String withheld(String id, String version, String receipt) =>
      '{"id":"$id","receipt":"$receipt","version":"$version"}';

  switch (item['recipe']) {
    case 'maximal':
      final count = item['entries']! as int;
      String id(int index) => 'm${index.toString().padLeft(4, '0')}${'x' * 59}';
      return textBytes(
        canonical(
          versions: [
            for (var index = 0; index < count; index++)
              version(
                id(index),
                '1.0.0-${'a' * 58}',
                hex64(index),
                hex64(index + count),
              ),
          ],
          withheld: [
            for (var index = 0; index < count; index++)
              withheld(
                id(index),
                '2.0.0-${'b' * 58}',
                hex64(index + 2 * count),
              ),
          ],
          added: [for (var index = 0; index < count; index++) id(index)],
        ),
      );
    case 'too-many':
      final count = item['entries']! as int;
      final list = item['list'];
      String id(int index) => 'm${index.toString().padLeft(5, '0')}';
      return textBytes(
        canonical(
          versions: [
            if (list == 'versions')
              for (var index = 0; index < count; index++)
                version(id(index), '1.0.0', hex64(index), hex64(index + count)),
          ],
          withheld: [
            if (list == 'withheld')
              for (var index = 0; index < count; index++)
                withheld(id(index), '2.0.0', hex64(index + 2 * count)),
          ],
          added: [
            if (list == 'addedRecords')
              for (var index = 0; index < count; index++) id(index),
          ],
        ),
      );
    case 'padded':
      final empty = canonical();
      return textBytes(
        empty + ' ' * ((item['length']! as int) - utf8.encode(empty).length),
      );
  }
  throw ArgumentError('Unknown recipe ${item['recipe']}.');
}
