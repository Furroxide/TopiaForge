import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:isolate';

import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'package:launcher_data/src/source_sdk_build_lock.dart';
import 'package:launcher_data/src/bounded_process.dart';

import 'source_sdk_build_ownership_fixture.dart';

void main() {
  late Directory root;
  setUp(() {
    root = Directory.systemTemp.createTempSync('tf-sdk-ownership-');
  });
  // Keep fixture files available for inspection; the test owns only temp data.

  for (final abandon in [false, true]) {
    test('bootstrap ${abandon ? 'abandon before watched' : 'deadline'} closes '
        'owner entered after caller exit', () async {
      final callerExit = ReceivePort();
      final handles = ReceivePort();
      final mailbox = ReceivePort();
      final failure = ReceivePort();
      final ownerExit = ReceivePort();
      await Isolate.spawn(callerBeforeOwnerEntry, [
        root.path,
        handles.sendPort,
        abandon ? const Duration(seconds: 30) : Duration.zero,
        mailbox.sendPort,
        failure.sendPort,
      ], onExit: callerExit.sendPort);
      final handle = await handles.first as List;
      await callerExit.first;
      final owner = Isolate(
        handle[0] as SendPort,
        pauseCapability: handle[1] as Capability,
      );
      owner.addOnExitListener(ownerExit.sendPort);
      owner.resume(handle[1] as Capability);
      final bootstrap = await mailbox.first as SendPort;
      if (abandon) bootstrap.send('abandon');
      final result = await failure.first as List;
      expect(result[0], abandon ? isA<StateError>() : isA<TimeoutException>());
      if (abandon) {
        expect(
          (result[0] as StateError).message,
          'SDK owner bootstrap was abandoned.',
        );
      }
      // VM exit is the liveness assertion: no unstarted port/isolate survives.
      await ownerExit.first;
      expect(Directory(p.join(root.path, '.dart_tool')).existsSync(), isFalse);
      expect(
        await withSourceSdkBuildLock(root, () async => 'reused'),
        'reused',
      );
      callerExit.close();
      handles.close();
      mailbox.close();
      failure.close();
      ownerExit.close();
    });
  }

  test(
    'bootstrap failure reaches a live caller without running its action',
    () async {
      final deadExit = ReceivePort();
      final dead = await Isolate.spawn(
        immediatelyExitingCaller,
        null,
        onExit: deadExit.sendPort,
      );
      await deadExit.first;
      final ownerExit = ReceivePort();
      var ran = false;
      await expectLater(
        withSourceSdkBuildLock(
          root,
          () async {
            ran = true;
          },
          spawnOwner: (arguments, replies) async {
            arguments[1] = dead.controlPort;
            arguments[4] = Duration.zero;
            final owner = await Isolate.spawn(
              sourceSdkOwnerMain,
              arguments,
              paused: true,
              onExit: replies,
            );
            owner.addOnExitListener(ownerExit.sendPort);
            owner.resume(owner.pauseCapability!);
            return owner;
          },
        ),
        throwsA(
          isA<TimeoutException>().having(
            (e) => e.message,
            'bootstrap error',
            'SDK owner bootstrap received no caller acknowledgment.',
          ),
        ),
      );
      await ownerExit.first;
      expect(ran, isFalse);
      expect(Directory(p.join(root.path, '.dart_tool')).existsSync(), isFalse);
      expect(
        await withSourceSdkBuildLock(root, () async => 'reused'),
        'reused',
      );
      deadExit.close();
      ownerExit.close();
    },
  );

  for (final separateProcess in [false, true]) {
    test('source SDK missing-output admission is owned across '
        '${separateProcess ? 'processes' : 'isolates'} and path aliases', () async {
      final repo = Directory(p.join(root.path, 'repo'))..createSync();
      sourceFixture(repo);
      final events = ReceivePort();
      final messages = StreamIterator<dynamic>(events);
      final first = Isolate.spawn(firstBuilder, [
        repo.path,
        root.path,
        events.sendPort,
      ]);
      await first;
      expect(await messages.moveNext(), isTrue);
      final control = messages.current as SendPort;
      expect(await messages.moveNext(), isTrue);
      expect(messages.current, 'first admitted');

      // Both spellings must own the same outputs, even with distinct data roots.
      final alias = Directory(p.join(repo.path, 'alias-parent'))..createSync();
      final aliasPath = p.join(alias.path, '..');
      final waiting = ReceivePort();
      final contender = separateProcess
          ? _startProcess(aliasPath, root.path, waiting.sendPort)
          : _startIsolate(aliasPath, root.path, waiting.sendPort);
      // A native acquisition attempt actually contended. No sleep can substitute
      // for this acknowledgement, even if the second scaffold is descheduled.
      expect(await waiting.first, 'waiting');
      waiting.close();
      control.send('release');
      expect(await messages.moveNext(), isTrue);
      expect(messages.current, 'first stopped');
      expect(await contender, 'second scaffolded');
      expect(File(p.join(root.path, 'overlap')).existsSync(), isFalse);
      final lock = File(
        p.join(root.path, 'second', 'test.second', 'topiaforge.sdk.lock.json'),
      );
      expect(jsonDecode(lock.readAsStringSync())['sdkVersion'], '1.0.0');
      await messages.cancel();
      events.close();
    });
  }

  test(
    'owning resolver Isolate.exit releases checkout for another scaffold',
    () async {
      final repo = Directory(p.join(root.path, 'repo'))..createSync();
      sourceFixture(repo);
      final exited = ReceivePort();
      await Isolate.spawn(exitingResolver, [
        repo.path,
        root.path,
        exited.sendPort,
      ]);
      expect(await exited.first, 'resolver exited under ownership');
      exited.close();
      expect(await secondBuilder(repo.path, root.path), 'second scaffolded');
    },
  );

  test('caller exit retains checkout until its writing child exits', () async {
    final server = await ServerSocket.bind(InternetAddress.loopbackIPv4, 0);
    final connection = server.first;
    final events = ReceivePort();
    final messages = StreamIterator<dynamic>(events);
    final output = File(p.join(root.path, 'TopiaForge.Mods.Testing.deps.json'));
    await Isolate.spawn(callerWithWritingChild, [
      root.path,
      events.sendPort,
      server.port,
      output.path,
    ]);
    expect(await messages.moveNext(), isTrue);
    final control = messages.current as SendPort;
    final child = await connection;
    final childMessages = StreamIterator<String>(
      child
          .cast<List<int>>()
          .transform(utf8.decoder)
          .transform(const LineSplitter()),
    );
    expect(await childMessages.moveNext(), isTrue);
    expect(childMessages.current, 'writing');
    control.send('exit');
    expect(await messages.moveNext(), isTrue);
    expect(messages.current, 'caller exited with active child');
    final waiting = ReceivePort();
    final next = runZoned(
      () => withSourceSdkBuildLock(root, () async {
        expect(output.readAsStringSync(), 'complete');
        return 'checkout reused';
      }),
      zoneValues: {sourceSdkOwnershipObserver: waiting.sendPort},
    );
    expect(await waiting.first, 'waiting');
    expect(output.readAsStringSync(), 'writing');
    child.writeln('finish');
    await child.flush();
    expect(await next, 'checkout reused');
    await childMessages.cancel();
    child.destroy();
    await server.close();
    waiting.close();
    await messages.cancel();
    events.close();
  });

  test('owned build preserves exit output and bounded failure type', () async {
    await withSourceSdkBuildLock(root, () async {
      final result = await runOwnedSourceSdkBuild(
        Platform.resolvedExecutable,
        [
          '--packages=${p.absolute('.dart_tool/package_config.json')}',
          p.absolute('test/source_sdk_build_ownership_fixture.dart'),
          '--failed-child',
        ],
        workingDirectory: Directory.current.path,
        timeout: const Duration(seconds: 10),
        maxStdoutBytes: 1024,
        maxStderrBytes: 1024,
      );
      expect(result.exitCode, 17);
      expect(result.stdout, 'build output');
      expect(result.stderr, 'build error');
      await expectLater(
        runOwnedSourceSdkBuild(
          Platform.resolvedExecutable,
          [
            '--packages=${p.absolute('.dart_tool/package_config.json')}',
            p.absolute('test/source_sdk_build_ownership_fixture.dart'),
            '--stalled-child',
          ],
          workingDirectory: Directory.current.path,
          timeout: const Duration(milliseconds: 100),
          maxStdoutBytes: 1024,
          maxStderrBytes: 1024,
        ),
        throwsA(
          isA<BoundedProcessException>().having(
            (e) => e.failure,
            'failure',
            BoundedProcessFailure.timeout,
          ),
        ),
      );
    });
    expect(
      await withSourceSdkBuildLock(root, () async => 'released after failure'),
      'released after failure',
    );
  });

  test('unrelated checkout ownership proceeds while another is held', () async {
    final other = Directory(p.join(root.path, 'other'))..createSync();
    await withSourceSdkBuildLock(root, () async {
      expect(await withSourceSdkBuildLock(other, () async => 42), 42);
    });
    await expectLater(
      withSourceSdkBuildLock(root, () async {
        throw StateError('original failure');
      }),
      throwsStateError,
    );
    expect(await withSourceSdkBuildLock(root, () async => 7), 7);
  });
}

Future<String> _startProcess(String repo, String root, SendPort waiting) async {
  final process = await Process.start(Platform.resolvedExecutable, [
    '--packages=${p.absolute('.dart_tool/package_config.json')}',
    p.absolute('test/source_sdk_build_ownership_fixture.dart'),
    repo,
    root,
  ]);
  final errors = process.stderr.transform(utf8.decoder).join();
  var result = '';
  await for (final line
      in process.stdout
          .transform(utf8.decoder)
          .transform(const LineSplitter())) {
    if (line == 'waiting') {
      waiting.send(line);
    } else {
      result = line;
    }
  }
  if (await process.exitCode != 0) return await errors;
  await errors;
  return result;
}

Future<String> _startIsolate(String repo, String root, SendPort observer) =>
    Isolate.run(() => secondBuilder(repo, root, observer));
