import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/src/launch_staging_store.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory root;
  late LaunchStagingStore store;
  late File lock;
  setUp(() {
    root = Directory(
      Directory.systemTemp
          .createTempSync('topiaforge-lease-')
          .resolveSymbolicLinksSync(),
    );
    store = LaunchStagingStore(root.path);
    lock = File(
      p.join(
        root.path,
        'BepInEx',
        'TopiaForge',
        'staging',
        'launch-admission.lock',
      ),
    );
    lock.parent.createSync(recursive: true);
  });
  tearDown(() {
    if (root.existsSync()) root.deleteSync(recursive: true);
  });

  test(
    'separate store instances release an idempotent shared admission lease',
    () async {
      final first = await store.acquireLaunchLease();
      expect(first, isNotNull);
      try {
        expect(
          await LaunchStagingStore(root.path).acquireLaunchLease(),
          isNull,
        );
      } finally {
        await first!.release();
      }
      await first.release();
      expect(
        lock.existsSync(),
        isTrue,
        reason: 'Never unlink a shared lock inode.',
      );
      final next = await LaunchStagingStore(root.path).acquireLaunchLease();
      expect(next, isNotNull);
      await next!.release();
    },
  );

  test('native file lock excludes an independently running process', () async {
    final script = File(p.join(root.path, 'hold_lock.dart'))
      ..writeAsStringSync(r'''
import 'dart:async';
import 'dart:convert';
import 'dart:io';
Future<void> main(List<String> args) async {
  final file = await File(args.single).open(mode: FileMode.append);
  try {
    await file.lock();
    stdout.writeln('locked');
    await stdin.transform(utf8.decoder).transform(const LineSplitter()).first;
    await file.unlock();
  } finally { await file.close(); }
}
''');
    final child = await Process.start(Platform.resolvedExecutable, [
      script.path,
      lock.path,
    ]);
    final errors = child.stderr.transform(utf8.decoder).join();
    final lines = StreamIterator(
      child.stdout.transform(utf8.decoder).transform(const LineSplitter()),
    );
    try {
      expect(
        await lines.moveNext().timeout(const Duration(seconds: 15)),
        isTrue,
      );
      expect(lines.current, 'locked');
      expect(
        await store.acquireLaunchLease(),
        isNull,
        reason: 'A separate process owns native admission.',
      );
    } finally {
      child.stdin.writeln('release');
      await child.stdin.close();
      expect(
        await child.exitCode.timeout(const Duration(seconds: 15)),
        0,
        reason: await errors,
      );
      await lines.cancel();
    }
    final after = await store.acquireLaunchLease();
    expect(after, isNotNull);
    await after!.release();
  });

  test(
    'non-file admission leaf is rejected without removing its content',
    () async {
      final directory = Directory(lock.path)..createSync();
      final marker = File(p.join(directory.path, 'keep'))
        ..writeAsStringSync('owned elsewhere');
      await expectLater(
        store.acquireLaunchLease(),
        throwsA(isA<FileSystemException>()),
      );
      expect(marker.readAsStringSync(), 'owned elsewhere');
      marker.deleteSync();
      directory.deleteSync();
      final lease = await store.acquireLaunchLease();
      expect(
        lease,
        isNotNull,
        reason: 'A failed acquire must not retain the in-process key.',
      );
      await lease!.release();
    },
  );
}
