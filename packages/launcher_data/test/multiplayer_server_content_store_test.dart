import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'held_file_support.dart';

/// The record of server content keeps #214's store rules where the game's
/// loader allows: atomic writes, and a file that cannot be read never written
/// over. Because the loader reads it too, a damaged record is read not at
/// all, and only an explicit repair writes over it, with a copy kept aside;
/// a newer launcher's record is never touched.
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

  final withheld = WithheldServerVersion(
    modId: 'example.two',
    version: '1.0.0',
    receiptSha256: 'c' * 64,
  );

  List<String> damagedCopies() => [
    for (final entity in folder.listSync())
      if (p.basename(entity.path).startsWith('server-content.damaged-'))
        p.basename(entity.path),
  ];

  Future<void> expectRefusedUpdate(ServerContentStore store) async {
    var changed = false;
    await expectLater(
      store.update((record) {
        changed = true;
        return record.withInstalled([entry('example.three')]);
      }),
      throwsA(isA<MultiplayerStoreException>()),
    );
    expect(changed, isFalse);
  }

  test(
    'a missing record is empty, and an update writes it canonically',
    () async {
      final store = ServerContentStore(folder.path);
      final load = await store.load();
      expect(load.record.isEmpty, isTrue);
      expect(load.status, ServerContentStatus.missing);
      await store.update((record) => record);
      expect(file.existsSync(), isFalse, reason: 'nothing to record yet');
      final written = await store.update(
        (record) => record.withInstalled([entry('example.one')]),
      );
      expect(file.readAsBytesSync(), written.encode());
      final again = await ServerContentStore(folder.path).load();
      expect(again.record.entries.single.modId, 'example.one');
      expect(again.status, ServerContentStatus.valid);
    },
  );

  test('a damaged record is read not at all and never updated', () async {
    final good = entry('example.one').toJson();
    file.writeAsStringSync(
      jsonEncode({
        'schemaVersion': ServerContentRecord.schemaVersion,
        'versions': [
          good,
          {'id': 7},
        ],
        'withheld': <Object>[],
        'addedRecords': <String>[],
      }),
    );
    final original = file.readAsBytesSync();
    final store = ServerContentStore(folder.path);
    final load = await store.load();
    expect(load.status, ServerContentStatus.damaged);
    expect(load.record.isEmpty, isTrue);
    expect(load.problem, isNotEmpty);
    expect(load.damagedBytes, original);
    await expectRefusedUpdate(store);
    expect(file.readAsBytesSync(), original);
    expect(damagedCopies(), isEmpty);
  });

  test(
    'a repair keeps a copy aside and writes what the rebuild makes',
    () async {
      final good = entry('example.one').toJson();
      file.writeAsStringSync(
        jsonEncode({
          'schemaVersion': ServerContentRecord.schemaVersion,
          'versions': [
            good,
            {'id': 7},
          ],
        }),
      );
      final original = file.readAsBytesSync();
      final store = ServerContentStore(folder.path);
      ServerContentRecord? given;
      final repaired = await store.repair((salvaged) async {
        given = salvaged;
        return salvaged.withWithheld([withheld]);
      });
      expect(given!.entries.single.modId, 'example.one');
      expect(repaired.status, ServerContentStatus.valid);
      expect(repaired.record.withheld, [withheld]);
      expect(file.readAsBytesSync(), repaired.record.encode());
      expect(
        File(p.join(folder.path, damagedCopies().single)).readAsBytesSync(),
        original,
      );
      // A valid record is left as it is.
      var rebuilt = false;
      final again = await store.repair((salvaged) async {
        rebuilt = true;
        return salvaged;
      });
      expect(rebuilt, isFalse);
      expect(again.status, ServerContentStatus.valid);
    },
  );

  test('a repair that cannot rebuild leaves the damaged record', () async {
    file.writeAsStringSync('{"schemaVersion":1,"versions":[');
    final original = file.readAsBytesSync();
    final store = ServerContentStore(folder.path);
    final load = await store.repair(
      (_) async => throw StateError('A receipt could not be read.'),
    );
    expect(load.status, ServerContentStatus.damaged);
    expect(load.problem, contains('could not be repaired'));
    expect(file.readAsBytesSync(), original);
    expect(damagedCopies(), isEmpty);
  });

  test('a newer launcher\'s record is never read or written over', () async {
    file.writeAsStringSync('{"schemaVersion":2,"future":[]}');
    final original = file.readAsBytesSync();
    final store = ServerContentStore(folder.path);
    final load = await store.load();
    expect(load.status, ServerContentStatus.newer);
    expect(load.damagedBytes, isNull);
    await expectRefusedUpdate(store);
    expect(
      (await store.repair((salvaged) async => salvaged)).status,
      ServerContentStatus.newer,
    );
    expect(file.readAsBytesSync(), original);
    expect(damagedCopies(), isEmpty);
  });

  test('a folder in the record\'s place is set aside by a repair', () async {
    Directory(file.path).createSync();
    final store = ServerContentStore(folder.path);
    final load = await store.load();
    expect(load.status, ServerContentStatus.damaged);
    expect(load.damagedBytes, isNull);
    final repaired = await store.repair(
      (salvaged) async => salvaged.withWithheld([withheld]),
    );
    expect(repaired.status, ServerContentStatus.valid);
    expect(FileSystemEntity.typeSync(file.path), FileSystemEntityType.file);
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
    expect(load.status, ServerContentStatus.unreadable);
    await expectLater(
      store.update((record) => record.withInstalled([entry('example.two')])),
      throwsA(
        isA<MultiplayerStoreException>().having(
          (error) => error.unreadable,
          'unreadable',
          isTrue,
        ),
      ),
    );
    await release();
    expect(file.readAsBytesSync(), before);
  });

  test('a write stopped between its moves is read where it stands, and put '
      'back by the next change', () async {
    final record = ServerContentRecord.empty().withInstalled([
      entry('example.one'),
    ]);
    final backup = File(
      p.join(folder.path, '${ServerContentStore.fileName}.4242-1-7.bak'),
    )..writeAsBytesSync(record.encode());
    final store = ServerContentStore(folder.path);
    final load = await store.load();
    expect(load.status, ServerContentStatus.valid);
    expect(load.record.entries.single.modId, 'example.one');
    // A load moves nothing: in another launcher it could land on the record
    // a write had just moved in.
    expect(file.existsSync(), isFalse);
    expect(backup.readAsBytesSync(), record.encode());
    await store.update((current) => current);
    expect(file.readAsBytesSync(), record.encode());
    expect(backup.existsSync(), isFalse);
  });

  test('a damaged backup a load reads where it stands fails closed', () async {
    File(
      p.join(folder.path, '${ServerContentStore.fileName}.4242-1-7.bak'),
    ).writeAsStringSync('{');
    final load = await ServerContentStore(folder.path).load();
    expect(load.status, ServerContentStatus.damaged);
    expect(file.existsSync(), isFalse);
  });

  for (final kind in ['folder', 'file']) {
    test('a damaged record set aside as a $kind, with no record beside it, '
        'stays damaged until a repair writes one', () async {
      // A repair moves a link, a folder or an oversized file aside before
      // it writes the record that replaces it; that write failed, as on a
      // full disk, or the launcher stopped there.
      final aside = p.join(
        folder.path,
        'server-content.damaged-20261019T120000000000Z.json',
      );
      kind == 'folder'
          ? Directory(aside).createSync()
          : File(aside).writeAsStringSync('x' * 64);
      final store = ServerContentStore(folder.path);
      final load = await store.load();
      expect(load.status, ServerContentStatus.damaged);
      expect(load.problem, contains('set aside as damaged'));
      await expectRefusedUpdate(store);
      final repaired = await store.repair(
        (salvaged) async => salvaged.withWithheld([withheld]),
      );
      expect(repaired.status, ServerContentStatus.valid);
      expect(repaired.record.withheld, [withheld]);
      expect(
        (await ServerContentStore(folder.path).load()).status,
        ServerContentStatus.valid,
      );
      expect(damagedCopies(), [
        'server-content.damaged-20261019T120000000000Z.json',
      ]);
    });
  }

  test('only a damaged copy this store names keeps a missing record '
      'damaged', () async {
    File(
      p.join(folder.path, 'server-content.damaged-copy.json'),
    ).writeAsStringSync('the player keeps this');
    File(
      p.join(folder.path, 'other.damaged-20261019T120000000000Z.json'),
    ).writeAsStringSync('another store');
    expect(
      (await ServerContentStore(folder.path).load()).status,
      ServerContentStatus.missing,
    );
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
