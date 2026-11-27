import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  test('peer requests satisfy V5 and keep local transport private', () async {
    for (var count = 2; count <= 8; count++) {
      final session = DevPeerSession(
        const DevPeerOverlay(0, 'unused-game', 'unused-data'),
        'a' * 32,
        '.',
      );
      try {
        final host = session.requestFor(count);
        expect(host.transport.port, 0);
        expect(host.transport.allowsDirect, isFalse);
        expect(host.host!.voice, isFalse);
        expect(host.host!.servePackages, isFalse);
        expect(host.host!.maxPlayers, count);
        expect(host.devInstance!.count, count);
        final join = session.requestFor(
          count,
          target: LaunchDirectTarget(
            host: '127.0.0.1',
            port: 12345,
            serverId: 'a' * 64,
            descriptorSha256: 'b' * 64,
            packageDigest: 'c' * 64,
          ),
        );
        expect(join.role, LaunchSessionRole.join);
        expect(join.transport.allowsDirect, isFalse);
        expect(join.serviceOrigin, isNull);
      } finally {
        await session.close();
      }
    }
  });
  late Directory temporary;
  late Directory source;
  late DevPeerOverlays overlays;
  setUp(() {
    temporary = Directory(
      Directory.systemTemp
          .createTempSync('tf-dev-peers-')
          .resolveSymbolicLinksSync(),
    );
    source = Directory(p.join(temporary.path, 'source'))..createSync();
    for (final name in ['Robotopia.exe', 'UnityPlayer.dll']) {
      File(p.join(source.path, name)).writeAsStringSync('small fake game file');
    }
    for (final name in ['Robotopia_Data', 'MonoBleedingEdge']) {
      Directory(p.join(source.path, name)).createSync();
      File(p.join(source.path, name, 'sentinel')).writeAsStringSync('shared');
    }
    overlays = DevPeerOverlays(p.join(temporary.path, 'developer'));
  });
  tearDown(() {
    // This test owns every real folder below temporary. Links are unlinked
    // first, even when a failed assertion left an overlay behind.
    void remove(Directory directory) {
      for (final entry in directory.listSync(followLinks: false)) {
        if (FileSystemEntity.typeSync(entry.path, followLinks: false) ==
            FileSystemEntityType.link) {
          Link(entry.path).deleteSync();
        } else if (entry is Directory) {
          remove(entry);
        } else {
          entry.deleteSync();
        }
      }
      directory.deleteSync();
    }

    remove(temporary);
  });

  test(
    'peer layout copies executable bytes and links shared data',
    () async {
      final peers = await overlays.create(source.path, 2);
      expect(peers.map((peer) => peer.index), [0, 1]);
      expect(peers.map((peer) => peer.gameRoot).toSet(), hasLength(2));
      for (final peer in peers) {
        expect(
          File(p.join(peer.gameRoot, 'Robotopia.exe')).readAsStringSync(),
          'small fake game file',
        );
        expect(Directory(peer.launcherRoot).existsSync(), isTrue);
        for (final name in ['Robotopia_Data', 'MonoBleedingEdge']) {
          expect(
            FileSystemEntity.typeSync(
              p.join(peer.gameRoot, name),
              followLinks: false,
            ),
            FileSystemEntityType.link,
          );
          expect(
            Directory(p.join(peer.gameRoot, name)).resolveSymbolicLinksSync(),
            p.join(source.path, name),
          );
        }
        final runtime = Directory(p.join(peer.gameRoot, 'BepInEx'))
          ..createSync();
        File(p.join(runtime.path, 'owned')).writeAsStringSync('private');
      }
      expect(Directory(p.join(source.path, 'BepInEx')).existsSync(), isFalse);
      overlays.cleanup(overlays.runRoot(peers.first));
      expect(
        File(
          p.join(source.path, 'Robotopia_Data', 'sentinel'),
        ).readAsStringSync(),
        'shared',
      );
    },
    skip: Platform.isMacOS
        ? 'APFS clone layout is covered by the macOS integration check.'
        : false,
  );

  test(
    'cleanup removes a junction without traversing its sentinel target',
    () async {
      final peers = await overlays.create(source.path, 2);
      final sentinel = Directory(p.join(temporary.path, 'sentinel'))
        ..createSync();
      final contents = File(p.join(sentinel.path, 'keep'))
        ..writeAsStringSync('untouched');
      await createDevDirectoryLink(
        p.join(peers.first.gameRoot, 'borrowed'),
        sentinel.path,
      );
      overlays.cleanup(overlays.runRoot(peers.first));
      expect(contents.readAsStringSync(), 'untouched');
      expect(Directory(overlays.runRoot(peers.first)).existsSync(), isFalse);
    },
    skip: Platform.isMacOS,
  );

  test(
    'cleanup refuses outside roots, root itself and a junction alias',
    () async {
      final peers = await overlays.create(source.path, 2);
      final run = overlays.runRoot(peers.first);
      final outside = Directory(p.join(temporary.path, 'outside'))
        ..createSync();
      final sentinel = File(p.join(outside.path, 'keep'))
        ..writeAsStringSync('untouched');
      for (final path in [outside.path, overlays.root.path, temporary.path]) {
        expect(() => overlays.cleanup(path), throwsStateError);
      }
      final alias = p.join(overlays.root.path, 'alias');
      await createDevDirectoryLink(alias, run);
      expect(() => overlays.cleanup(alias), throwsStateError);
      Link(alias).deleteSync();
      expect(sentinel.readAsStringSync(), 'untouched');
      overlays.cleanup(run);
    },
    skip: Platform.isMacOS,
  );

  test('invalid peer counts have no filesystem effects', () async {
    for (final count in [0, 1, 9, 100]) {
      await expectLater(
        overlays.create(source.path, count),
        throwsArgumentError,
      );
    }
    expect(overlays.root.existsSync(), isFalse);
  });

  test(
    'eight quoted overlays preserve shared data and cancelled setup creates nothing',
    () async {
      final token = CancellationToken()..cancel();
      await expectLater(
        overlays.create(source.path, 8, cancellation: token),
        throwsA(isA<OperationCancelledException>()),
      );
      expect(overlays.root.existsSync(), isFalse);
      source = source.renameSync(p.join(temporary.path, "source ['& café]"));
      overlays = DevPeerOverlays(p.join(temporary.path, "developer ['& café]"));
      final peers = await overlays.create(source.path, 8);
      expect(peers.map((peer) => peer.gameRoot).toSet(), hasLength(8));
      expect(peers.map((peer) => peer.launcherRoot).toSet(), hasLength(8));
      overlays.cleanup(overlays.runRoot(peers.first));
      expect(
        File(
          p.join(source.path, 'Robotopia_Data', 'sentinel'),
        ).readAsStringSync(),
        'shared',
      );
    },
    skip: Platform.isMacOS,
  );

  test('partial setup failure removes its owned run', () async {
    File(p.join(source.path, 'UnityPlayer.dll')).deleteSync();
    await expectLater(overlays.create(source.path, 2), throwsStateError);
    expect(overlays.root.listSync(), isEmpty);
    expect(
      File(
        p.join(source.path, 'Robotopia_Data', 'sentinel'),
      ).readAsStringSync(),
      'shared',
    );
  }, skip: Platform.isMacOS);
}
