import 'dart:async';
import 'dart:io';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'multiplayer_join_content_fixtures.dart';
import 'multiplayer_join_rig.dart';
import 'secure_http_loopback.dart';

// Closing the launcher during a join's downloads must not wait out the
// transfer: the close cancels the downloads as it begins, a cancelled
// download deletes its temporary file before it returns, and the close then
// waits only for that. Installs, launches and other work are still waited
// for, as `launcher_repository_close_test.dart` holds.
void main() {
  test('a close during a join download stops it at once, keeps no partial '
      'file, and the join ends as cancelled', () async {
    final rig = await JoinRig.create();
    final archive = TestArchive(
      'held.download',
      '1.0.0',
      // Large enough that half of it passes a progress report.
      payload: noisyPayload(1024 * 1024),
    );
    final halfSent = Completer<void>();
    final release = Completer<void>();
    final source = await LoopbackHttps.start((request) async {
      final bytes = archive.bytes;
      request.response.contentLength = bytes.length;
      request.response.add(bytes.sublist(0, bytes.length ~/ 2));
      await request.response.flush();
      if (!halfSent.isCompleted) halfSent.complete();
      // The rest never comes while the test runs, as from a source that
      // stalls mid-transfer.
      await release.future;
      request.response.add(bytes.sublist(bytes.length ~/ 2));
      await request.response.close();
    });
    addTearDown(() async {
      if (!release.isCompleted) release.complete();
      await source.close();
    });
    final server = await verifiedServer([archive.package]);
    final plan = planJoin(server, {
      archive.sha256Hex: archive.offeredAt(
        'https://packages.example/${archive.fileName}',
        size: archive.bytes.length,
      ),
    });
    var join = consentedJoin(server, plan);
    expect(join.stage, JoinStage.downloading);
    var received = 0;
    final flowing = Completer<void>();
    final downloading = source.run(
      () => rig.repository.downloadJoinContent(
        rig.install,
        join,
        onProgress: (progress) {
          received = progress.bytesDone;
          if (!flowing.isCompleted) flowing.complete();
        },
      ),
    );
    await halfSent.future;
    await flowing.future;
    final cache = Directory(p.join(rig.dataRoot, 'package-cache'));
    expect(_partialFiles(cache), isNotEmpty, reason: 'the transfer began');

    final watch = Stopwatch()..start();
    await rig.repository.dispose().timeout(const Duration(seconds: 10));
    watch.stop();

    expect(
      watch.elapsed,
      lessThan(const Duration(seconds: 3)),
      reason: 'The close cancels the download instead of waiting for it.',
    );
    expect(received, lessThan(archive.bytes.length));
    final answer = await downloading;
    expect(answer, isA<JoinCancelRequested>());
    join = JoinSession.reduce(join, answer!);
    expect(join.isEnded, isTrue);
    expect(join.outcome!.wasCancelled, isTrue);
    expect(_partialFiles(cache), isEmpty, reason: 'no partial file is kept');
    expect(cachedArchive(rig.dataRoot, archive).existsSync(), isFalse);
  });

  test('a download the caller cancels still answers null, as before', () async {
    final rig = await JoinRig.create();
    final archive = TestArchive('held.cancel', '1.0.0');
    final release = Completer<void>();
    final started = Completer<void>();
    final source = await LoopbackHttps.start((request) async {
      request.response.contentLength = archive.bytes.length;
      request.response.add(archive.bytes.sublist(0, 16));
      await request.response.flush();
      if (!started.isCompleted) started.complete();
      await release.future;
    });
    addTearDown(() async {
      if (!release.isCompleted) release.complete();
      await source.close();
    });
    final server = await verifiedServer([archive.package]);
    final plan = planJoin(server, {
      archive.sha256Hex: archive.offeredAt(
        'https://packages.example/${archive.fileName}',
      ),
    });
    final token = CancellationToken();
    final downloading = source.run(
      () => rig.repository.downloadJoinContent(
        rig.install,
        consentedJoin(server, plan),
        cancellation: token,
      ),
    );
    await started.future;
    token.cancel();
    expect(await downloading, isNull);
  });
}

List<String> _partialFiles(Directory cache) => !cache.existsSync()
    ? const []
    : [
        for (final entity in cache.listSync())
          if (entity.path.endsWith('.tmp')) p.basename(entity.path),
      ];
