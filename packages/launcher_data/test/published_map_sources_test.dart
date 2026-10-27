import 'dart:convert';
import 'dart:io';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'multiplayer_join_rig.dart';

void main() {
  late Directory root;
  late PublishedMapSourceStore store;
  final first = PublishedMapSource(
    map: 'example',
    scene: 'courtyard',
    name: 'Courtyard',
  );
  setUp(() {
    root = Directory.systemTemp.createTempSync('saved-maps-');
    store = PublishedMapSourceStore(root.path);
  });
  tearDown(() => root.deleteSync(recursive: true));
  test(
    'saved sources round trip, upsert by exact map/scene and survive restart',
    () async {
      expect(await store.read(), isEmpty);
      await store.save(first);
      await store.save(
        PublishedMapSource(
          map: first.map,
          scene: first.scene,
          name: 'New name',
        ),
      );
      await store.save(
        PublishedMapSource(
          map: 'Example',
          scene: first.scene,
          name: 'Different map',
        ),
      );
      await store.save(
        PublishedMapSource(
          map: first.map,
          scene: 'workshop',
          name: 'Other scene',
        ),
      );
      final restored = await PublishedMapSourceStore(root.path).read();
      expect(restored, hasLength(3));
      expect(restored.singleWhere((s) => s.key == first.key).name, 'New name');
      expect(await store.remove(first.map, first.scene), hasLength(2));
      expect((await store.read()).map((s) => s.map), contains('Example'));
      expect(
        Directory(root.path).listSync().where((e) => e.path.endsWith('.tmp')),
        isEmpty,
      );
    },
  );
  test('malformed or oversized list is never overwritten by an edit', () async {
    final file = File(p.join(root.path, PublishedMapSourceStore.fileName));
    for (final bytes in [
      utf8.encode('{bad'),
      List.filled(PublishedMapSources.maximumBytes + 1, 32),
    ]) {
      file.writeAsBytesSync(bytes);
      await expectLater(store.save(first), throwsFormatException);
      expect(file.readAsBytesSync(), bytes);
    }
  });
  test(
    'another editor receives a busy refusal, without losing the first write',
    () async {
      final pending = store.save(first);
      await expectLater(
        PublishedMapSourceStore(
          root.path,
        ).save(PublishedMapSource(map: 'other', scene: 'scene', name: 'Other')),
        throwsStateError,
      );
      await pending;
      expect((await store.read()).single.map, first.map);
    },
  );
  test('atomic recovery restores the sole complete backup', () async {
    final file = File(p.join(root.path, PublishedMapSourceStore.fileName));
    File(
      '${file.path}.1-1.bak',
    ).writeAsBytesSync(PublishedMapSources.write([first]));
    expect((await store.read()).single.map, first.map);
    expect(file.existsSync(), isTrue);
  });
  test('linked source folders refuse without changing the target', () async {
    final target = Directory(p.join(root.path, 'target'))..createSync();
    final link = Link(p.join(root.path, 'linked'));
    final file = File(p.join(target.path, PublishedMapSourceStore.fileName))
      ..writeAsBytesSync(PublishedMapSources.write([first]));
    final before = file.readAsBytesSync();
    try {
      await link.create(target.path);
    } on FileSystemException {
      markTestSkipped('OS link creation is unavailable.');
      return;
    }
    try {
      await expectLater(
        PublishedMapSourceStore(link.path).read(),
        throwsStateError,
      );
      await expectLater(
        PublishedMapSourceStore(
          link.path,
        ).save(PublishedMapSource(map: 'other', scene: 'scene', name: 'Other')),
        throwsStateError,
      );
      expect(file.readAsBytesSync(), before);
    } finally {
      await link.delete();
    }
  });
  test(
    'projection reaches process admission and removal preserves downloaded maps',
    () async {
      var starts = 0;
      late JoinRig rig;
      rig = await JoinRig.create(
        withRuntime: true,
        creator: (request) async {
          starts++;
          final projection = File(
            p.join(
              rig.game.path,
              'BepInEx',
              'TopiaForge',
              PublishedMapSourceStore.fileName,
            ),
          );
          expect(
            PublishedMapSources.read(projection.readAsBytesSync()),
            hasLength(starts == 1 ? 1 : 0),
          );
          return LaunchProcessReceipt(pid: 4200 + starts);
        },
        liveness: (_) async => false,
      );
      await rig.repository.savePublishedMapSource(first);
      final profile = (await rig.repository.loadSnapshot()).profiles.first;
      final launched = await rig.repository.launch(
        rig.install,
        profile,
        selectionOverride: const LaunchSelection.mainMenu(),
      );
      expect(launched.started, isTrue, reason: launched.message);
      final cached = File(
        p.join(
          rig.game.path,
          'BepInEx',
          'TopiaForge',
          'cache',
          'maps',
          '${'a' * 64}.map',
        ),
      );
      cached.parent.createSync(recursive: true);
      cached.writeAsStringSync('map bytes');
      await rig.repository.removePublishedMapSource(first.map, first.scene);
      final again = await rig.repository.launch(
        rig.install,
        profile,
        selectionOverride: const LaunchSelection.mainMenu(),
      );
      expect(again.started, isTrue, reason: again.message);
      expect(cached.readAsStringSync(), 'map bytes');
      expect(starts, 2);
    },
  );
  test(
    'unreadable list prevents process start and does not disclose paths',
    () async {
      var starts = 0;
      final rig = await JoinRig.create(
        withRuntime: true,
        creator: (_) async {
          starts++;
          return const LaunchProcessReceipt(pid: 4200);
        },
      );
      final file = File(p.join(rig.dataRoot, PublishedMapSourceStore.fileName));
      file.parent.createSync(recursive: true);
      file.writeAsStringSync('{bad');
      final profile = (await rig.repository.loadSnapshot()).profiles.first;
      final result = await rig.repository.launch(
        rig.install,
        profile,
        selectionOverride: const LaunchSelection.mainMenu(),
      );
      expect(result.started, isFalse);
      expect(starts, 0);
      expect(result.message, isNot(contains(rig.dataRoot)));
      expect(result.message, contains('saved map list'));
    },
  );
}
