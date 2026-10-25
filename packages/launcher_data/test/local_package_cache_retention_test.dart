import 'dart:convert';
import 'dart:io';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'local_package_cache_fixture.dart';
import 'held_file_support.dart';
import 'multiplayer_join_content_fixtures.dart';
import 'secure_http_loopback.dart';

void main() {
  late LocalCacheFixture fixture;
  final archive = TestArchive('example.local', '1.0.0');
  setUp(() async => fixture = await LocalCacheFixture.create());

  test(
    'repeated local builds leave only the current archive across restarts',
    () async {
      for (var build = 0; build < 5; build++) {
        final next = TestArchive(
          archive.id,
          archive.version,
          payload: 'build $build',
        );
        await fixture.put(next, using: fixture.open());
        expect(fixture.archives, hasLength(1));
        expect(fixture.cached(next).readAsBytesSync(), next.bytes);
      }
    },
  );

  test('uninstall retires the local archive', () async {
    await fixture.put(archive);
    await fixture.repository.uninstallMod(fixture.install, archive.id);
    expect(fixture.archives, isEmpty);
  });

  test(
    'older installed versions and disabled mods retain their archives',
    () async {
      await fixture.put(archive);
      final next = TestArchive(archive.id, '2.0.0');
      await fixture.put(next);
      await fixture.repository.setModEnabled(
        fixture.install,
        archive.id,
        false,
      );
      expect(fixture.cached(archive).existsSync(), isTrue);
      expect(fixture.cached(next).existsSync(), isTrue);
      await fixture.repository.uninstallMod(fixture.install, archive.id);
      expect(fixture.archives, isEmpty);
    },
  );

  test(
    'another receipt referencing a superseded archive prevents pruning',
    () async {
      await fixture.put(archive);
      final other = TestArchive('example.other', '1.0.0');
      await fixture.put(other);
      final receipt =
          jsonDecode(fixture.receipt(other).readAsStringSync()) as Map;
      receipt['sourceSha256'] = archive.sha256Hex;
      fixture.receipt(other).writeAsStringSync(jsonEncode(receipt));
      final replacement = TestArchive(
        archive.id,
        archive.version,
        payload: 'next',
      );
      await fixture.put(replacement);
      expect(fixture.cached(archive).readAsBytesSync(), archive.bytes);
      await fixture.repository.uninstallMod(fixture.install, other.id);
      expect(fixture.cached(archive).existsSync(), isFalse);
    },
  );

  test(
    'another game sharing the cache retains its original until uninstall',
    () async {
      await fixture.put(archive);
      final secondRoot = Directory(p.join(fixture.root.path, 'second'))
        ..createSync();
      final second = await fixture.repository.selectGameDirectory(
        createTestGame(secondRoot).path,
      );
      await fixture.open().installPackage(
        archive.writeTo(fixture.root).path,
        second,
      );
      final replacement = TestArchive(
        archive.id,
        archive.version,
        payload: 'next',
      );
      await fixture.put(replacement);
      expect(fixture.cached(archive).existsSync(), isTrue);
      await fixture.open().uninstallMod(second, archive.id);
      expect(fixture.cached(archive).existsSync(), isFalse);
      expect(fixture.cached(replacement).existsSync(), isTrue);
    },
  );

  test(
    'unreadable receipt retains archives until a later successful sweep',
    () async {
      await fixture.put(archive);
      final other = TestArchive('example.other', '1.0.0');
      await fixture.put(other);
      fixture.receipt(other).writeAsStringSync('{broken');
      await fixture.repository.uninstallMod(fixture.install, archive.id);
      expect(fixture.cached(archive).existsSync(), isTrue);
      await fixture.repository.uninstallMod(fixture.install, other.id);
      expect(fixture.archives, isEmpty);
    },
  );

  test(
    'failed retirement keeps the successful install and retries later',
    () async {
      await fixture.put(archive);
      fixture.cached(archive).deleteSync();
      final blocked = Directory(fixture.cached(archive).path)..createSync();
      final next = TestArchive(archive.id, archive.version, payload: 'next');
      await fixture.put(next);
      expect(fixture.cached(next).readAsBytesSync(), next.bytes);
      expect(
        fixture.log,
        contains('Unused local mod archives could not be removed.'),
      );
      final warning = fixture.log
          .split('\n')
          .firstWhere((line) => line.contains('Unused local'));
      expect(warning, isNot(contains(fixture.root.path)));
      blocked.deleteSync();
      fixture.cached(archive).writeAsBytesSync(archive.bytes);
      await fixture.put(next);
      expect(fixture.cached(archive).existsSync(), isFalse);
    },
  );

  test(
    'pre-existing cache entries are never adopted for local retirement',
    () async {
      fixture.cache.createSync(recursive: true);
      fixture.cached(archive).writeAsBytesSync(archive.bytes);
      await fixture.put(archive);
      await fixture.repository.uninstallMod(fixture.install, archive.id);
      expect(fixture.cached(archive).readAsBytesSync(), archive.bytes);
    },
  );

  test(
    'Windows held archive cannot fail a replacement or uninstall',
    () async {
      await fixture.put(archive);
      final release = await holdInPlace(fixture.cached(archive));
      if (release == null) return;
      final next = TestArchive(archive.id, archive.version, payload: 'next');
      await fixture.put(next);
      expect(fixture.cached(archive).readAsBytesSync(), archive.bytes);
      expect(fixture.cached(next).readAsBytesSync(), next.bytes);
      await fixture.repository.uninstallMod(fixture.install, archive.id);
      expect(fixture.receipt(archive).existsSync(), isFalse);
      expect(fixture.cached(archive).existsSync(), isTrue);
      await release();
      await fixture.repository.uninstallMod(fixture.install, archive.id);
      expect(fixture.archives, isEmpty);
    },
    skip: !Platform.isWindows ? 'Windows file sharing semantics.' : false,
  );

  test('HTTPS-installed entries survive replacement and uninstall', () async {
    final https = await LoopbackHttps.start((request) async {
      request.response.add(archive.bytes);
      await request.response.close();
    });
    addTearDown(https.close);
    await https.run(
      () => fixture.repository.installPackage(
        'https://packages.example/${archive.fileName}',
        fixture.install,
        expectedSha256: archive.sha256Hex,
      ),
    );
    final next = TestArchive(
      archive.id,
      archive.version,
      payload: 'local next',
    );
    await fixture.put(next);
    expect(fixture.cached(archive).readAsBytesSync(), archive.bytes);
    await fixture.repository.uninstallMod(fixture.install, archive.id);
    expect(fixture.cached(archive).existsSync(), isTrue);
    expect(fixture.cached(next).existsSync(), isFalse);
  });

  test(
    'HTTPS cache reuse relinquishes earlier local ownership permanently',
    () async {
      await fixture.put(archive);
      final counting = CountingHttpOverrides();
      await HttpOverrides.runWithHttpOverrides(
        () => fixture.repository.installPackage(
          'https://packages.example/${archive.fileName}',
          fixture.install,
          expectedSha256: archive.sha256Hex,
        ),
        counting,
      );
      expect(counting.clientsCreated, 0);
      await fixture.put(archive);
      await fixture.repository.uninstallMod(fixture.install, archive.id);
      expect(fixture.cached(archive).readAsBytesSync(), archive.bytes);
    },
  );

  for (final reuse in [false, true]) {
    test(
      'join archive survives local uninstall (cache reuse: $reuse)',
      () async {
        final source = archive.writeTo(fixture.root);
        if (reuse) await fixture.put(archive);
        final server = await verifiedServer([archive.package]);
        final plan = planJoin(server, {
          archive.sha256Hex: archive.offeredAt(source.uri.toString()),
        });
        final downloaded = await fixture.repository.downloadJoinContent(
          fixture.install,
          consentedJoin(server, plan),
        );
        expect(downloaded, isA<JoinDownloadsCompleted>());
        await fixture.put(archive);
        await fixture.repository.uninstallMod(fixture.install, archive.id);
        expect(fixture.cached(archive).readAsBytesSync(), archive.bytes);
      },
    );
  }

  test('join cache retention failure returns path-free retry advice', () async {
    await fixture.put(archive);
    final ownership = File(
      p.join(fixture.cache.path, 'local-installs', '${archive.sha256Hex}.json'),
    );
    ownership.deleteSync();
    Directory(ownership.path).createSync();
    final server = await verifiedServer([archive.package]);
    final plan = planJoin(server, {
      archive.sha256Hex: archive.offeredAt(
        archive.writeTo(fixture.root).uri.toString(),
      ),
    });
    final result = await fixture.repository.downloadJoinContent(
      fixture.install,
      consentedJoin(server, plan),
    );
    expect(result, isA<JoinFailed>());
    final failure = result as JoinFailed;
    expect(failure.code, JoinFailureCode.downloadFailed);
    expect(failure.detail, contains('then join again'));
    expect(failure.detail, isNot(contains(fixture.root.path)));
    expect(fixture.cached(archive).readAsBytesSync(), archive.bytes);
  });
}
