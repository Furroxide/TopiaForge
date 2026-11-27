import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:math';
import 'dart:typed_data';

import 'package:crypto/crypto.dart';
import 'package:launcher_data/src/multiplayer/host_package_client.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'multiplayer_network_support.dart';

/// The host-served package client against endpoints on the loopback
/// interface, written as the game's `DescriptorEndpoint` writes, and against
/// a recording connector where nothing may be contacted.
void main() {
  final loopback = InternetAddress.loopbackIPv4;
  late Directory root;
  late File target;

  setUp(() {
    root = Directory.systemTemp.createTempSync('tf-host-pkg-');
    target = File(p.join(root.path, 'download.tmp'));
  });
  tearDown(() => root.deleteSync(recursive: true));

  /// An archive-like body larger than the game's 64 KiB writes.
  final archive = Uint8List.fromList(
    List<int>.generate(200 * 1024 + 17, (index) => (index * 7 + 3) & 0xFF),
  );
  final archiveSha = sha256.convert(archive).toString();

  Future<LoopbackDescriptorHost> host(
    FutureOr<void> Function(Socket socket, String request) respond,
  ) async {
    final started = await LoopbackDescriptorHost.start(respond);
    addTearDown(started.close);
    return started;
  }

  /// Answers as the game does: `TFD1 200 <length>`, the archive in 64 KiB
  /// writes, then the end of the stream.
  Future<void> serveWhole(Socket socket, List<int> body) async {
    socket.add(ascii.encode('TFD1 200 ${body.length}\n'));
    for (var at = 0; at < body.length; at += 65536) {
      socket.add(body.sublist(at, min(body.length, at + 65536)));
      await socket.flush();
    }
    await socket.close();
  }

  Future<JoinHostPackageResult> fetch(
    LoopbackDescriptorHost endpoint, {
    HostPackageClient? client,
    String? sha,
    void Function(int, int)? onProgress,
    CancellationToken? cancellation,
  }) => (client ?? HostPackageClient()).fetchFrom(
    loopback,
    endpoint.port,
    sha ?? archiveSha,
    target,
    allowDirect: false,
    onProgress: onProgress,
    cancellation: cancellation,
  );

  JoinHostPackageProblem problemOf(JoinHostPackageResult result) =>
      (result as JoinHostPackageFailed).problem;

  test('streams an archive to disk with its SHA-256, as asked', () async {
    final endpoint = await host((socket, _) => serveWhole(socket, archive));
    final progress = <(int, int)>[];
    final result = await fetch(
      endpoint,
      onProgress: (received, total) => progress.add((received, total)),
    );
    final written = result as JoinHostPackageDownloaded;
    expect(written.sha256, archiveSha);
    expect(written.length, archive.length);
    expect(target.readAsBytesSync(), archive);
    expect(endpoint.requests, ['TFD1 package $archiveSha\n']);
    expect(progress.last, (archive.length, archive.length));
    for (var index = 1; index < progress.length; index++) {
      expect(progress[index].$1, greaterThan(progress[index - 1].$1));
      expect(progress[index].$2, archive.length);
    }
  });

  test('takes the announced length and nothing after it', () async {
    final endpoint = await host((socket, _) async {
      socket
        ..add(ascii.encode('TFD1 200 ${archive.length}\n'))
        ..add(archive)
        ..add(utf8.encode('more bytes than announced'));
      await socket.flush();
      // The rest is never read: the client closes the connection itself.
    });
    final disconnected = endpoint.disconnected.first;
    final written = await fetch(endpoint) as JoinHostPackageDownloaded;
    expect(written.sha256, archiveSha);
    expect(target.readAsBytesSync(), archive);
    await disconnected.timeout(const Duration(seconds: 10));
  });

  test('a length shorter than the archive keeps only what it says', () async {
    final endpoint = await host((socket, _) async {
      socket
        ..add(ascii.encode('TFD1 200 1000\n'))
        ..add(archive);
      await socket.flush();
      await socket.close();
    });
    final written = await fetch(endpoint) as JoinHostPackageDownloaded;
    // Whether these bytes are the pinned archive is the caller's check.
    expect(written.length, 1000);
    expect(written.sha256, isNot(archiveSha));
    expect(target.readAsBytesSync(), archive.sublist(0, 1000));
  });

  test('statuses without a body name their reason and keep nothing', () async {
    for (final (line, problem) in [
      ('TFD1 404\n', JoinHostPackageProblem.notServed),
      ('TFD1 429\n', JoinHostPackageProblem.busy),
      ('TFD1 503\n', JoinHostPackageProblem.busy),
      ('TFD1 400\n', JoinHostPackageProblem.refused),
      ('TFD1 200\n', JoinHostPackageProblem.refused),
      ('TFD1 999\n', JoinHostPackageProblem.refused),
    ]) {
      final endpoint = await host(
        (socket, _) => LoopbackDescriptorHost.raw(socket, ascii.encode(line)),
      );
      expect(problemOf(await fetch(endpoint)), problem, reason: line);
      expect(target.existsSync(), isFalse, reason: line);
    }
  });

  test('answers that break the protocol are refused', () async {
    for (final bytes in [
      ascii.encode('HTTP/1.1 200 OK\r\n\r\n'),
      ascii.encode('TFD1 200 0\n'),
      ascii.encode('TFD1 200 -1\n'),
      ascii.encode('TFD1 200 0042\n'),
      List<int>.filled(200, 0x41),
      <int>[],
    ]) {
      final endpoint = await host(
        (socket, _) => LoopbackDescriptorHost.raw(socket, bytes),
      );
      expect(
        problemOf(await fetch(endpoint)),
        JoinHostPackageProblem.malformed,
        reason: latin1.decode(bytes.take(20).toList()),
      );
      expect(target.existsSync(), isFalse);
    }
  });

  test('a length past the bound is refused before the body is read', () async {
    final sent = Completer<void>();
    final endpoint = await host((socket, _) async {
      socket
        ..add(
          ascii.encode('TFD1 200 ${ModRegistryFormat.maxPackageBytes + 1}\n'),
        )
        ..add(List<int>.filled(1000, 1));
      await socket.flush();
      sent.complete();
      // Held open: only the status line decides.
    });
    final disconnected = endpoint.disconnected.first;
    expect(problemOf(await fetch(endpoint)), JoinHostPackageProblem.tooLarge);
    expect(target.existsSync(), isFalse);
    await disconnected.timeout(const Duration(seconds: 10));
    // A client with a smaller bound holds to it the same way.
    final small = await host((socket, _) => serveWhole(socket, archive));
    expect(
      problemOf(
        await fetch(small, client: HostPackageClient(maximumBytes: 4096)),
      ),
      JoinHostPackageProblem.tooLarge,
    );
  });

  test('a body that ends short is cut off, and nothing is kept', () async {
    final endpoint = await host((socket, _) async {
      socket
        ..add(ascii.encode('TFD1 200 ${archive.length}\n'))
        ..add(archive.sublist(0, archive.length ~/ 2));
      await socket.flush();
      await socket.close();
    });
    expect(problemOf(await fetch(endpoint)), JoinHostPackageProblem.endedEarly);
    expect(target.existsSync(), isFalse);
  });

  group('deadlines', () {
    test('are 5 s to connect, 15 s of silence and 2 minutes in all', () async {
      final timers = ManualTimers();
      final endpoint = await host((socket, _) => serveWhole(socket, archive));
      await fetch(endpoint, client: HostPackageClient(timer: timers.call));
      expect(timers.timers.first.delay, const Duration(seconds: 5));
      expect(
        timers.timers.map((timer) => timer.delay),
        containsAll([const Duration(seconds: 15), const Duration(minutes: 2)]),
      );
      expect(timers.timers.where((timer) => timer.isActive), isEmpty);
    });

    test('a host that stalls mid-body is given up on', () async {
      final timers = ManualTimers();
      final hold = Completer<void>();
      addTearDown(() => hold.isCompleted ? null : hold.complete());
      final endpoint = await host((socket, _) async {
        socket
          ..add(ascii.encode('TFD1 200 ${archive.length}\n'))
          ..add(archive.sublist(0, 1000));
        await socket.flush();
        await hold.future;
      });
      final disconnected = endpoint.disconnected.first;
      final progress = Completer<void>();
      final result = fetch(
        endpoint,
        client: HostPackageClient(timer: timers.call),
        onProgress: (received, _) {
          if (received == 1000 && !progress.isCompleted) progress.complete();
        },
      );
      await progress.future.timeout(const Duration(seconds: 10));
      await Future<void>.delayed(Duration.zero);
      final stall = timers.timers.lastWhere(
        (timer) => timer.isActive && timer.delay == const Duration(seconds: 15),
      );
      stall.fire();
      expect(problemOf(await result), JoinHostPackageProblem.stalled);
      expect(target.existsSync(), isFalse);
      await disconnected.timeout(const Duration(seconds: 10));
    });

    test('a host that never answers stalls too', () async {
      final timers = ManualTimers();
      final endpoint = await host((_, _) {});
      final accepted = endpoint.accepted.first;
      final result = fetch(
        endpoint,
        client: HostPackageClient(timer: timers.call),
      );
      await accepted;
      await Future<void>.delayed(Duration.zero);
      timers.timers
          .lastWhere(
            (timer) =>
                timer.isActive && timer.delay == const Duration(seconds: 15),
          )
          .fire();
      expect(problemOf(await result), JoinHostPackageProblem.stalled);
      expect(target.existsSync(), isFalse);
    });

    test('a transfer past the two minutes a host allows ends', () async {
      final timers = ManualTimers();
      final hold = Completer<void>();
      addTearDown(() => hold.isCompleted ? null : hold.complete());
      final endpoint = await host((socket, _) async {
        socket.add(ascii.encode('TFD1 200 ${archive.length}\n'));
        await socket.flush();
        await hold.future;
      });
      final accepted = endpoint.accepted.first;
      final result = fetch(
        endpoint,
        client: HostPackageClient(timer: timers.call),
      );
      await accepted;
      await Future<void>.delayed(Duration.zero);
      timers.timers
          .singleWhere((timer) => timer.delay == const Duration(minutes: 2))
          .fire();
      expect(problemOf(await result), JoinHostPackageProblem.timedOut);
      expect(target.existsSync(), isFalse);
    });

    test('a connection that never opens is unreachable', () async {
      final timers = ManualTimers();
      final pending = Completer<ConnectionTask<Socket>>();
      final client = HostPackageClient(
        timer: timers.call,
        connect: (_, _) => pending.future,
      );
      final result = client.fetchFrom(
        lanHost,
        27250,
        archiveSha,
        target,
        allowDirect: false,
      );
      await Future<void>.delayed(const Duration(milliseconds: 10));
      expect(timers.timers.single.delay, const Duration(seconds: 5));
      timers.fireLatest();
      expect(problemOf(await result), JoinHostPackageProblem.unreachable);
      expect(target.existsSync(), isFalse);
    });
  });

  test('cancelling mid-body closes the connection and keeps nothing', () async {
    final hold = Completer<void>();
    addTearDown(() => hold.isCompleted ? null : hold.complete());
    final endpoint = await host((socket, _) async {
      socket
        ..add(ascii.encode('TFD1 200 ${archive.length}\n'))
        ..add(archive.sublist(0, 70000));
      await socket.flush();
      await hold.future;
    });
    final disconnected = endpoint.disconnected.first;
    final cancellation = CancellationToken();
    final result = await fetch(
      endpoint,
      cancellation: cancellation,
      onProgress: (_, _) => cancellation.cancel(),
    );
    expect(problemOf(result), JoinHostPackageProblem.cancelled);
    expect(target.existsSync(), isFalse);
    expect(root.listSync(), isEmpty);
    await disconnected.timeout(const Duration(seconds: 10));
  });

  test('a progress listener that throws does not stop the download', () async {
    final endpoint = await host((socket, _) => serveWhole(socket, archive));
    final result = await fetch(
      endpoint,
      onProgress: (_, _) => throw StateError('listener failed'),
    );
    expect((result as JoinHostPackageDownloaded).sha256, archiveSha);
  });

  test('hostile hosts never make it throw or leave a part', () async {
    final random = Random(20261022);
    var bytes = <int>[];
    final endpoint = await host(
      (socket, _) => LoopbackDescriptorHost.raw(socket, bytes),
    );
    for (var round = 0; round < 40; round++) {
      final head = switch (round % 3) {
        0 => 'TFD1 200 ${random.nextInt(3000) + 1}\n',
        1 => 'TFD1 ${random.nextInt(999)}\n',
        _ => '',
      };
      bytes = [
        ...latin1.encode(head),
        ...List<int>.generate(random.nextInt(2000), (_) => random.nextInt(256)),
      ];
      final result = await fetch(endpoint);
      if (result is JoinHostPackageDownloaded) {
        expect(target.lengthSync(), result.length);
        target.deleteSync();
      } else {
        expect(target.existsSync(), isFalse);
      }
    }
  });
}
