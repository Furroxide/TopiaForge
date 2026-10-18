import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'held_file_support.dart';

/// The record of server content keeps #214's store rules: atomic writes,
/// damage salvaged with a copy kept aside, and a file that cannot be read
/// never written over.
void main() {
  late Directory folder;
  late File file;

  setUp(() {
    folder = Directory(
      Directory.systemTemp
          .createTempSync('tf-content-store-')
          .resolveSymbolicLinksSync(),
    );
    addTearDown(() => folder.deleteSync(recursive: true));
    file = File(p.join(folder.path, ServerContentStore.fileName));
  });

  ServerContentEntry entry(String id, {String version = '1.0.0'}) =>
      ServerContentEntry(
        modId: id,
        version: version,
        sha256: 'a' * 64,
        receiptSha256: 'b' * 64,
        hidden: true,
        installedAt: 1791849600,
      );

  List<String> damagedCopies() => [
    for (final entity in folder.listSync())
      if (p.basename(entity.path).startsWith('server-content.damaged-'))
        p.basename(entity.path),
  ];

  test(
    'a missing record is empty, and an update writes it canonically',
    () async {
      final store = ServerContentStore(folder.path);
      final load = await store.load();
      expect(load.record.isEmpty, isTrue);
      expect(load.recovery, MultiplayerStoreRecovery.none);
      final written = await store.update(
        (record) => record.withInstalled([entry('example.one')]),
      );
      expect(file.readAsBytesSync(), written.encode());
      final again = await ServerContentStore(folder.path).load();
      expect(again.record.entries.single.modId, 'example.one');
      expect(again.recovery, MultiplayerStoreRecovery.none);
    },
  );

  test('a damaged record keeps a copy aside and what was valid', () async {
    final good = entry('example.one').toJson();
    file.writeAsStringSync(
      jsonEncode({
        'schema': ServerContentRecord.schema,
        'versions': [
          good,
          {'id': 7},
        ],
        'addedRecords': <String>[],
      }),
    );
    final original = file.readAsBytesSync();
    final load = await ServerContentStore(folder.path).load();
    expect(load.recovery, MultiplayerStoreRecovery.salvaged);
    expect(load.record.entries.single.modId, 'example.one');
    expect(damagedCopies(), hasLength(1));
    expect(
      File(p.join(folder.path, damagedCopies().single)).readAsBytesSync(),
      original,
    );
    expect(file.readAsBytesSync(), load.record.encode());
  });

  test('a record that cannot be used at all is reset, its copy kept', () async {
    for (final text in [
      'not json',
      jsonEncode({'schema': 'topiaforge.server-content/9'}),
    ]) {
      file.writeAsStringSync(text);
      final load = await ServerContentStore(folder.path).load();
      expect(load.recovery, MultiplayerStoreRecovery.reset, reason: text);
      expect(load.record.isEmpty, isTrue);
      expect(file.readAsBytesSync(), ServerContentRecord.empty().encode());
    }
    expect(damagedCopies(), hasLength(2));
  });

  test('a folder in the record\'s place is set aside', () async {
    Directory(file.path).createSync();
    final load = await ServerContentStore(folder.path).load();
    expect(load.recovery, MultiplayerStoreRecovery.reset);
    expect(FileSystemEntity.typeSync(file.path), FileSystemEntityType.notFound);
    expect(damagedCopies(), hasLength(1));
  });

  test('a record that cannot be read is never written over', () async {
    final store = ServerContentStore(folder.path);
    await store.update(
      (record) => record.withInstalled([entry('example.one')]),
    );
    final before = file.readAsBytesSync();
    final release = await holdUnreadable(file);
    if (release == null) return;
    final load = await store.load();
    expect(load.recovery, MultiplayerStoreRecovery.unreadable);
    var changed = false;
    await expectLater(
      store.update((record) {
        changed = true;
        return record.withInstalled([entry('example.two')]);
      }),
      throwsA(
        isA<MultiplayerStoreException>().having(
          (error) => error.unreadable,
          'unreadable',
          isTrue,
        ),
      ),
    );
    expect(changed, isFalse);
    await release();
    expect(file.readAsBytesSync(), before);
  });

  test('a write stopped between its moves is put back', () async {
    final record = ServerContentRecord.empty().withInstalled([
      entry('example.one'),
    ]);
    File(
      p.join(folder.path, '${ServerContentStore.fileName}.4242-1-7.bak'),
    ).writeAsBytesSync(record.encode());
    final load = await ServerContentStore(folder.path).load();
    expect(load.recovery, MultiplayerStoreRecovery.none);
    expect(load.record.entries.single.modId, 'example.one');
    expect(file.readAsBytesSync(), record.encode());
  });

  test('stale temporary files of the record are swept once', () async {
    final stale = File(
      p.join(folder.path, '${ServerContentStore.fileName}.4242-1-7.tmp'),
    )..writeAsStringSync('partial');
    final foreign = File(p.join(folder.path, 'state.json.4242-1.tmp'))
      ..writeAsStringSync('not the store\'s');
    final store = ServerContentStore(
      folder.path,
      clock: () => DateTime.now().add(const Duration(hours: 2)),
    );
    await store.load();
    expect(stale.existsSync(), isFalse);
    expect(foreign.existsSync(), isTrue);
  });

  test('changes run one after another', () async {
    final store = ServerContentStore(folder.path);
    await Future.wait([
      for (var index = 0; index < 8; index++)
        store.update(
          (record) => record.withInstalled([entry('example.mod$index')]),
        ),
    ]);
    final load = await ServerContentStore(folder.path).load();
    expect(load.record.entries, hasLength(8));
  });
}
