import 'dart:async';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'multiplayer_join_content_fixtures.dart';
import 'secure_http_loopback.dart';

/// A join's downloading stage: each package from the source the plan names,
/// through `secure_http.dart` from a loopback server, bounded, checked by
/// SHA-256 as it streams and by manifest before it is kept.
void main() {
  late Directory root;
  late String dataRoot;
  late GameInstall install;
  late LocalLauncherRepository repository;

  setUp(() async {
    root = Directory(
      Directory.systemTemp
          .createTempSync('tf-join-dl-')
          .resolveSymbolicLinksSync(),
    );
    addTearDown(() => root.deleteSync(recursive: true));
    dataRoot = p.join(root.path, 'data');
    repository = LocalLauncherRepository(
      dataRoot: dataRoot,
      repositoryRoot: root.path,
      packageMetadataValidator: (_) async => const [],
      gameRunningProbe: (_) async => false,
    );
    addTearDown(repository.dispose);
    install = await repository.selectGameDirectory(createTestGame(root).path);
  });

  final sandbox = TestArchive('example.sandbox', '1.2.0', name: 'Sandbox');
  final tools = TestArchive('example.tools', '2.0.0', name: 'Builder Tools');

  Future<LoopbackHttps> serve(
    FutureOr<void> Function(HttpRequest request) handle,
  ) async {
    final https = await LoopbackHttps.start(handle);
    addTearDown(https.close);
    return https;
  }

  /// Serves each archive at `/<file name>`.
  Future<LoopbackHttps> serveArchives(List<TestArchive> archives) =>
      serve((request) async {
        final archive = archives.firstWhere(
          (item) => request.uri.path == '/${item.fileName}',
        );
        request.response.contentLength = archive.bytes.length;
        request.response.add(archive.bytes);
        await request.response.close();
      });

  String urlOf(TestArchive archive) =>
      'https://packages.example/${archive.fileName}';

  Future<(JoinSessionState, JoinPlan)> joinNeeding(
    List<TestArchive> archives, {
    Map<String, JoinPackageResolution> resolutions = const {},
  }) async {
    final server = await verifiedServer([
      for (final archive in archives) archive.package,
    ]);
    final plan = planJoin(server, {
      for (final archive in archives)
        archive.sha256Hex:
            resolutions[archive.id] ??
            archive.offeredAt(urlOf(archive), size: archive.bytes.length),
    });
    return (consentedJoin(server, plan), plan);
  }

  List<String> cacheLeftovers() {
    final cache = Directory(p.join(dataRoot, 'package-cache'));
    if (!cache.existsSync()) return const [];
    return [for (final entity in cache.listSync()) p.basename(entity.path)];
  }

  test('downloads each package with one total of progress', () async {
    final https = await serveArchives([sandbox, tools]);
    final (state, _) = await joinNeeding([sandbox, tools]);
    expect(state.stage, JoinStage.downloading);
    final progress = <JoinDownloadProgressed>[];
    final event = await https.run(
      () => repository.downloadJoinContent(
        install,
        state,
        onProgress: progress.add,
      ),
    );
    expect(event, isA<JoinDownloadsCompleted>());
    final total = sandbox.bytes.length + tools.bytes.length;
    expect(progress.last.bytesDone, total);
    expect(progress.last.bytesTotal, total);
    for (var index = 1; index < progress.length; index++) {
      expect(
        progress[index].bytesDone,
        greaterThanOrEqualTo(progress[index - 1].bytesDone),
      );
    }
    // The reducer takes every report.
    final reduced = progress.fold(state, JoinSession.reduce);
    expect(reduced.progress!.bytesDone, total);
    for (final archive in [sandbox, tools]) {
      expect(cachedArchive(dataRoot, archive).readAsBytesSync(), archive.bytes);
    }
    expect(cacheLeftovers(), hasLength(2));
  });

  test('a verified archive in the cache is not fetched again', () async {
    final first = await serveArchives([sandbox]);
    final (state, _) = await joinNeeding([sandbox]);
    await first.run(() => repository.downloadJoinContent(install, state));
    final again = CountingHttpOverrides();
    final event = await HttpOverrides.runWithHttpOverrides(
      () => repository.downloadJoinContent(install, state),
      again,
    );
    expect(event, isA<JoinDownloadsCompleted>());
    expect(again.clientsCreated, 0);
  });

  test('cancelling mid-body keeps nothing of it', () async {
    final hold = Completer<void>();
    addTearDown(() {
      if (!hold.isCompleted) hold.complete();
    });
    // Large enough that progress is reported before the body ends.
    final big = TestArchive(
      'example.big',
      '1.0.0',
      payload: noisyPayload(900 * 1024),
    );
    expect(big.bytes.length, greaterThan(600 * 1024));
    final https = await serve((request) async {
      request.response.contentLength = big.bytes.length;
      request.response.add(big.bytes.sublist(0, 400 * 1024));
      await request.response.flush();
      await hold.future;
    });
    final (state, _) = await joinNeeding([big]);
    final cancellation = CancellationToken();
    final event = await https.run(
      () => repository.downloadJoinContent(
        install,
        state,
        cancellation: cancellation,
        onProgress: (_) => cancellation.cancel(),
      ),
    );
    expect(event, isNull);
    expect(cacheLeftovers(), isEmpty);
    // The join ended at the cancel request, before the download returned.
    expect(
      JoinSession.reduce(
        state,
        const JoinCancelRequested(),
      ).outcome!.wasCancelled,
      isTrue,
    );
  });

  test('another archive than the pinned one is an integrity failure', () async {
    final impostor = TestArchive('example.sandbox', '1.2.0', payload: 'evil');
    final https = await serve((request) async {
      request.response.add(impostor.bytes);
      await request.response.close();
    });
    final (state, _) = await joinNeeding(
      [sandbox],
      resolutions: {'example.sandbox': sandbox.offeredAt(urlOf(sandbox))},
    );
    final event = await https.run(
      () => repository.downloadJoinContent(install, state),
    );
    final failure = event as JoinFailed;
    expect(failure.code, JoinFailureCode.integrityFailed);
    expect(failure.detail, contains('Sandbox 1.2.0'));
    expect(failure.detail, contains('Test Source'));
    expect(cacheLeftovers(), isEmpty);
    final ended = JoinSession.reduce(state, failure);
    expect(ended.outcome!.failure!.code, JoinFailureCode.integrityFailed);
  });

  group('oversize', () {
    test('against the stated size is an integrity failure', () async {
      final https = await serve((request) async {
        request.response.contentLength = sandbox.bytes.length;
        request.response.add(sandbox.bytes);
        await request.response.close();
      });
      final (state, _) = await joinNeeding(
        [sandbox],
        resolutions: {
          'example.sandbox': sandbox.offeredAt(
            urlOf(sandbox),
            size: sandbox.bytes.length - 1,
          ),
        },
      );
      final event = await https.run(
        () => repository.downloadJoinContent(install, state),
      );
      expect((event as JoinFailed).code, JoinFailureCode.integrityFailed);
      expect(event.detail, contains('not the size the source lists'));
      expect(cacheLeftovers(), isEmpty);
    });

    test('against the cap is refused before the body is read', () async {
      var bodyBytesSent = 0;
      final https = await serve((request) async {
        final socket = await request.response.detachSocket(writeHeaders: false);
        socket.add(
          'HTTP/1.1 200 OK\r\ncontent-length: '
                  '${ModRegistryFormat.maxPackageBytes + 1}\r\n\r\n'
              .codeUnits,
        );
        socket.add(List.filled(1000, 1));
        bodyBytesSent += 1000;
        await socket.flush();
        await socket.close();
      });
      final (state, _) = await joinNeeding(
        [sandbox],
        resolutions: {'example.sandbox': sandbox.offeredAt(urlOf(sandbox))},
      );
      final event = await https.run(
        () => repository.downloadJoinContent(install, state),
      );
      expect((event as JoinFailed).code, JoinFailureCode.downloadFailed);
      expect(event.detail, contains('larger than a mod may be'));
      expect(bodyBytesSent, 1000);
      expect(cacheLeftovers(), isEmpty);
    });
  });

  test('a truncated body is a download failure to retry', () async {
    final https = await serve((request) async {
      final socket = await request.response.detachSocket(writeHeaders: false);
      socket.add(
        'HTTP/1.1 200 OK\r\ncontent-length: ${sandbox.bytes.length}\r\n\r\n'
            .codeUnits,
      );
      socket.add(sandbox.bytes.sublist(0, sandbox.bytes.length ~/ 2));
      await socket.flush();
      await socket.close();
    });
    final (state, _) = await joinNeeding([sandbox]);
    final event = await https.run(
      () => repository.downloadJoinContent(install, state),
    );
    expect((event as JoinFailed).code, JoinFailureCode.downloadFailed);
    expect(event.detail, contains('join again'));
    expect(cacheLeftovers(), isEmpty);
  });

  test('an archive whose manifest is not the listed one is refused', () async {
    // The same id and version, built with another manifest: the source's
    // listing, and so the plan's trusted manifest, says something else.
    final relabelled = TestArchive(
      'example.sandbox',
      '1.2.0',
      name: 'Sandbox',
      manifestChanges: const {'description': 'Not what the source lists.'},
    );
    final https = await serveArchives([relabelled]);
    final server = await verifiedServer([relabelled.package]);
    final plan = planJoin(server, {
      relabelled.sha256Hex: JoinPackageResolution.available(
        manifest: sandbox.manifest,
        sourceId: 'test.source',
        sourceName: 'Test Source',
        downloadUrl: urlOf(relabelled),
      ),
    });
    final event = await https.run(
      () =>
          repository.downloadJoinContent(install, consentedJoin(server, plan)),
    );
    expect((event as JoinFailed).code, JoinFailureCode.manifestMismatch);
    expect(event.detail, contains('does not match its listing'));
    expect(cacheLeftovers(), isEmpty);
  });

  test('a redirect to another HTTPS host is followed', () async {
    final https = await serve((request) async {
      if (request.uri.path == '/${sandbox.fileName}') {
        await request.response.redirect(
          Uri.parse('https://mirror.example/mirrored.topiaforgemod'),
        );
        return;
      }
      request.response.add(sandbox.bytes);
      await request.response.close();
    });
    final (state, _) = await joinNeeding([sandbox]);
    final event = await https.run(
      () => repository.downloadJoinContent(install, state),
    );
    expect(event, isA<JoinDownloadsCompleted>());
    expect(cachedArchive(dataRoot, sandbox).existsSync(), isTrue);
  });

  test('a redirect to plain HTTP is refused', () async {
    final https = await serve((request) async {
      await request.response.redirect(
        Uri.parse('http://packages.example/${sandbox.fileName}'),
      );
    });
    final (state, _) = await joinNeeding([sandbox]);
    final event = await https.run(
      () => repository.downloadJoinContent(install, state),
    );
    expect((event as JoinFailed).code, JoinFailureCode.downloadFailed);
    expect(cacheLeftovers(), isEmpty);
  });

  test('an error status names the status and what to do', () async {
    final https = await serve((request) async {
      request.response.statusCode = HttpStatus.serviceUnavailable;
      await request.response.close();
    });
    final (state, _) = await joinNeeding([sandbox]);
    final event = await https.run(
      () => repository.downloadJoinContent(install, state),
    );
    final failure = event as JoinFailed;
    expect(failure.code, JoinFailureCode.downloadFailed);
    expect(failure.detail, contains('HTTP 503'));
    expect(failure.detail, contains('Try joining again later'));
  });

  test('a local source is copied under the same checks', () async {
    final folder = Directory(p.join(root.path, 'dist'))..createSync();
    final file = tools.writeTo(folder);
    final (state, _) = await joinNeeding(
      [tools],
      resolutions: {
        'example.tools': tools.offeredAt(file.uri.toString(), size: 7),
      },
    );
    final refused = await repository.downloadJoinContent(install, state);
    expect((refused as JoinFailed).code, JoinFailureCode.integrityFailed);
    final (fine, _) = await joinNeeding(
      [tools],
      resolutions: {'example.tools': tools.offeredAt(file.uri.toString())},
    );
    expect(
      await repository.downloadJoinContent(install, fine),
      isA<JoinDownloadsCompleted>(),
    );
  });

  test('packages only the host serves are never downloaded', () async {
    final hosted = TestArchive('example.hosted', '1.0.0');
    final server = await verifiedServer([
      hosted.package,
      sandbox.package,
    ], hostServesPackages: true);
    final plan = planJoin(server, {
      hosted.sha256Hex: JoinPackageResolution.hostOffered(),
      sandbox.sha256Hex: sandbox.offeredAt(urlOf(sandbox)),
    }, developerMode: true);
    expect(plan.hasUnverifiedPackages, isTrue);
    final state = consentedJoin(server, plan, onLocalNetwork: true);
    expect(state.stage, JoinStage.downloading);
    final overrides = CountingHttpOverrides();
    final event = await HttpOverrides.runWithHttpOverrides(
      () => repository.downloadJoinContent(install, state),
      overrides,
    );
    final failure = event as JoinFailed;
    expect(failure.code, JoinFailureCode.hostDownloadUnsupported);
    expect(failure.detail, contains('example.hosted 1.0.0'));
    expect(failure.detail, contains('Ask the host'));
    expect(overrides.clientsCreated, 0);
    expect(cacheLeftovers(), isEmpty);
    expect(
      JoinSession.reduce(state, failure).outcome!.failure!.code,
      JoinFailureCode.hostDownloadUnsupported,
    );
  });

  test('failures never name the source address', () async {
    final https = await serve((request) async {
      request.response.statusCode = HttpStatus.forbidden;
      await request.response.close();
    });
    final (state, _) = await joinNeeding([sandbox]);
    final event = await https.run(
      () => repository.downloadJoinContent(install, state),
    );
    final detail = (event as JoinFailed).detail;
    expect(detail, isNot(contains('packages.example')));
    expect(detail, isNot(contains('127.0.0.1')));
    final log = File(p.join(dataRoot, 'logs', 'launcher.log'));
    expect(log.readAsStringSync(), isNot(contains('packages.example')));
  });

  test('other stages are not answered', () async {
    final (state, _) = await joinNeeding([sandbox]);
    final installing = JoinSession.reduce(
      state,
      const JoinDownloadsCompleted(),
    );
    expect(await repository.downloadJoinContent(install, installing), isNull);
  });
}
