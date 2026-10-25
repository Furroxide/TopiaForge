import 'dart:async';
import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'local_package_cache_fixture.dart';
import 'held_file_support.dart';
import 'multiplayer_join_content_fixtures.dart';

void main() {
  late LocalCacheFixture fixture;
  final archive = TestArchive('example.local', '1.0.0');
  setUp(() async => fixture = await LocalCacheFixture.create());

  test('local install caches verified original under its SHA-256', () async {
    await fixture.put(archive);
    expect(fixture.archives, hasLength(1));
    expect(fixture.cached(archive).readAsBytesSync(), archive.bytes);
    expect(
      p.basename(fixture.archives.single.path),
      '${archive.sha256Hex}.topiaforgemod',
    );
  });

  test(
    'cache keeps verified bytes if the source changes during staging',
    () async {
      final source = archive.writeTo(fixture.root);
      final changed = TestArchive(
        archive.id,
        archive.version,
        payload: 'changed',
      );
      final repository = fixture.open(
        validator: (_) async {
          source.writeAsBytesSync(changed.bytes);
          return const [];
        },
      );
      await repository.installPackage(source.path, fixture.install);
      expect(source.readAsBytesSync(), changed.bytes);
      expect(fixture.cached(archive).readAsBytesSync(), archive.bytes);
      expect(fixture.cached(changed).existsSync(), isFalse);
    },
  );

  test('file URI and local dependency originals are both cached', () async {
    final dependency = TestArchive('example.dependency', '1.0.0');
    final root = TestArchive(
      'example.root',
      '1.0.0',
      manifestChanges: {
        'dependencies': {dependency.id: '*'},
      },
    );
    await fixture.sources([dependency]);
    await fixture.repository.installPackage(
      root.writeTo(fixture.root).uri.toString(),
      fixture.install,
    );
    expect(fixture.archives, hasLength(2));
    expect(fixture.cached(root).readAsBytesSync(), root.bytes);
    expect(fixture.cached(dependency).readAsBytesSync(), dependency.bytes);
  });

  test(
    'identical reinstall reuses the existing archive without rewriting',
    () async {
      await fixture.put(archive);
      final cached = fixture.cached(archive);
      cached.setLastModifiedSync(DateTime.utc(2001));
      final timestamp = cached.lastModifiedSync();
      await fixture.put(archive, using: fixture.open());
      expect(cached.lastModifiedSync(), timestamp);
      expect(cached.readAsBytesSync(), archive.bytes);
    },
  );

  test(
    'wrong-hash regular entry is replaced with the verified bytes',
    () async {
      fixture.cache.createSync(recursive: true);
      fixture.cached(archive).writeAsStringSync('damaged');
      await fixture.put(archive);
      expect(fixture.cached(archive).readAsBytesSync(), archive.bytes);
    },
  );

  test(
    'directory at the cache entry refuses install without deleting it',
    () async {
      final target = Directory(fixture.cached(archive).path)
        ..createSync(recursive: true);
      final sentinel = File(p.join(target.path, 'keep'))
        ..writeAsStringSync('keep');
      await expectLater(fixture.put(archive), throwsA(cacheFailure));
      expect(sentinel.readAsStringSync(), 'keep');
      expect(fixture.receipt(archive).existsSync(), isFalse);
    },
  );

  test(
    'symlink at the cache entry refuses install without touching its target',
    () async {
      fixture.cache.createSync(recursive: true);
      final target = File(p.join(fixture.root.path, 'sentinel'))
        ..writeAsStringSync('keep');
      final link = Link(fixture.cached(archive).path);
      try {
        link.createSync(target.path);
      } on FileSystemException catch (error) {
        markTestSkipped(
          'Symbolic links unavailable: ${error.osError?.message}',
        );
        return;
      }
      addTearDown(() {
        if (link.existsSync()) link.deleteSync();
      });
      await expectLater(fixture.put(archive), throwsA(cacheFailure));
      expect(target.readAsStringSync(), 'keep');
      expect(link.existsSync(), isTrue);
      expect(fixture.receipt(archive).existsSync(), isFalse);
    },
  );

  test('damaged archive is refused before caching', () async {
    final file = archive.writeTo(fixture.root)..writeAsStringSync('not a ZIP');
    await expectLater(
      fixture.repository.installPackage(file.path, fixture.install),
      throwsA(isA<Object>()),
    );
    expect(fixture.archives, isEmpty);
  });

  test('oversized local file is refused before caching', () async {
    final file = archive.writeTo(fixture.root);
    final handle = file.openSync(mode: FileMode.write);
    try {
      handle.truncateSync(512 * 1024 * 1024 + 1);
    } finally {
      handle.closeSync();
    }
    await expectLater(
      fixture.repository.installPackage(file.path, fixture.install),
      throwsA(
        predicate((error) => error.toString().contains('536870912 bytes')),
      ),
    );
    expect(fixture.archives, isEmpty);
  });

  test('incorrect expected hash is refused before caching', () async {
    await expectLater(
      fixture.repository.installPackage(
        archive.writeTo(fixture.root).path,
        fixture.install,
        expectedSha256: '0' * 64,
      ),
      throwsA(predicate((error) => error.toString().contains('SHA-256'))),
    );
    expect(fixture.archives, isEmpty);
  });

  test('metadata rejection is refused before caching', () async {
    final repository = fixture.open(
      validator: (_) async => ['invalid metadata'],
    );
    await expectLater(
      fixture.put(archive, using: repository),
      throwsA(isA<StateError>()),
    );
    expect(fixture.archives, isEmpty);
  });

  test(
    'cache write failure cancels with path-free advice and preserves old install',
    () async {
      await fixture.put(archive);
      final original = fixture.receipt(archive).readAsBytesSync();
      final replacement = TestArchive(
        archive.id,
        archive.version,
        payload: 'new build',
      );
      Directory(fixture.cached(replacement).path).createSync();
      await expectLater(
        fixture.put(replacement),
        throwsA(
          predicate((error) {
            final message = error.toString();
            return cacheFailure.matches(error, {}) &&
                message.contains('reinstall the mod') &&
                !message.contains(fixture.root.path) &&
                !message.contains(replacement.sha256Hex);
          }),
        ),
      );
      expect(fixture.receipt(archive).readAsBytesSync(), original);
      expect(fixture.cached(archive).readAsBytesSync(), archive.bytes);
    },
  );

  test('cache storage failure gives path-free advice', () async {
    File(fixture.cache.path).writeAsStringSync('blocked');
    await expectLater(fixture.put(archive), throwsA(cacheFailure));
    expect(fixture.receipt(archive).existsSync(), isFalse);
  });

  test(
    'a failed commit retains old cache and retires the uncommitted build',
    () async {
      await fixture.put(archive);
      final replacement = TestArchive(
        archive.id,
        archive.version,
        payload: 'new build',
      );
      final failing = fixture.open(
        hook: (_) => throw StateError('injected commit failure'),
      );
      await expectLater(
        fixture.put(replacement, using: failing),
        throwsStateError,
      );
      expect(fixture.cached(archive).readAsBytesSync(), archive.bytes);
      expect(fixture.cached(replacement).existsSync(), isFalse);
      expect(fixture.archives, hasLength(1));
    },
  );

  test(
    'overlapping repositories serialize receipt commits and pruning',
    () async {
      final entered = Completer<void>();
      final release = Completer<void>();
      final blocked = fixture.open(
        hook: (_) async {
          entered.complete();
          await release.future;
        },
      );
      final first = fixture.put(archive, using: blocked);
      await entered.future;
      final next = TestArchive(archive.id, archive.version, payload: 'next');
      final second = fixture.put(next, using: fixture.open());
      release.complete();
      await Future.wait([first, second]);
      expect(fixture.archives, hasLength(1));
      expect(fixture.cached(next).readAsBytesSync(), next.bytes);
    },
  );

  test(
    'a held cache lease refuses safely and can be retried',
    () async {
      final lock = File(p.join(fixture.data.path, 'package-cache.lock'))
        ..writeAsStringSync('');
      final release = await holdInPlace(lock);
      if (release == null) return;
      await expectLater(
        fixture.put(archive),
        throwsA(
          isA<StateError>().having(
            (error) => error.message,
            'message',
            contains('unavailable or in use'),
          ),
        ),
      );
      expect(fixture.receipt(archive).existsSync(), isFalse);
      await release();
      await fixture.put(archive);
      expect(fixture.cached(archive).readAsBytesSync(), archive.bytes);
    },
    skip: !Platform.isWindows ? 'Windows file sharing semantics.' : false,
  );

  // Another TopiaForge process holds the lease the way the repository does;
  // a shared hold also shows that the repository asks for more than a
  // shared lock, and an exclusive one that it refuses rather than waits.
  for (final shared in [false, true]) {
    final mode = shared ? 'shared' : 'exclusive';
    test(
      'a lease another process holds ($mode) refuses until that process ends',
      () async {
        final holder = await fixture.holdLease(shared: shared);
        await expectLater(
          fixture.put(archive),
          throwsA(
            isA<StateError>().having(
              (error) => error.message,
              'message',
              contains('unavailable or in use'),
            ),
          ),
        );
        expect(fixture.receipt(archive).existsSync(), isFalse);
        expect(fixture.archives, isEmpty);
        // The holder ends without letting go, as a crash does, which frees it.
        expect(await holder.end(), 3);
        await fixture.put(archive);
        expect(fixture.cached(archive).readAsBytesSync(), archive.bytes);
      },
    );
  }

  test(
    'incomplete rollback preserves both original archives',
    () async {
      await fixture.put(archive);
      final next = TestArchive(archive.id, archive.version, payload: 'next');
      Future<void> Function()? release;
      final failing = fixture.open(
        hook: (_) async {
          release = await holdInPlace(fixture.receipt(next));
          if (release != null) throw StateError('injected commit failure');
        },
      );
      try {
        await fixture.put(next, using: failing);
        if (release == null) return;
        fail('A held receipt must prevent rollback.');
      } on StateError catch (error) {
        expect(error.message, contains('rollback was incomplete'));
        expect(fixture.cached(archive).readAsBytesSync(), archive.bytes);
        expect(fixture.cached(next).readAsBytesSync(), next.bytes);
      } finally {
        await release?.call();
      }
    },
    skip: !Platform.isWindows ? 'Windows file sharing semantics.' : false,
  );
}

final cacheFailure = isA<StateError>().having(
  (error) => error.message,
  'message',
  contains('install was cancelled'),
);
