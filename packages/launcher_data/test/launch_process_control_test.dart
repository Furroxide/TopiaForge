import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'package:launcher_data/src/launch_process_control.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  test(
    'read proves an owned child image and exact native generation',
    () async {
      final owned = await _child();
      try {
        final identity = await readLaunchProcessIdentity(
          owned.process.pid,
          owned.executable,
        );
        expect(identity, isNotNull);
        expect(identity!.pid, owned.process.pid);
        expect(identity.startTimeUtc.isUtc, isTrue);
        expect(identity.nativeStartToken, isNotEmpty);
        expect(await isLaunchProcessAlive(identity), isTrue);
      } finally {
        await owned.dispose();
      }
    },
    skip: !Platform.isWindows && !Platform.isLinux,
  );
  test('reader refuses a different executable and unsafe PIDs', () async {
    expect(
      await readLaunchProcessIdentity(
        pid,
        '${Platform.resolvedExecutable}.other',
      ),
      isNull,
    );
    expect(
      await readLaunchProcessIdentity(0, Platform.resolvedExecutable),
      isNull,
    );
    expect(
      await readLaunchProcessIdentity(-1, Platform.resolvedExecutable),
      isNull,
    );
    expect(
      await readLaunchProcessIdentity(2147483648, Platform.resolvedExecutable),
      isNull,
    );
  });
  test(
    'stop never terminates another same-image child or stale generation',
    () async {
      final first = await _child();
      final second = await _child();
      try {
        final firstId = (await readLaunchProcessIdentity(
          first.process.pid,
          first.executable,
        ))!;
        final secondId = (await readLaunchProcessIdentity(
          second.process.pid,
          second.executable,
        ))!;
        final stale = LaunchProcessIdentity(
          pid: firstId.pid,
          executablePath: firstId.executablePath,
          startTimeUtc: firstId.startTimeUtc.subtract(
            const Duration(seconds: 1),
          ),
          nativeStartToken: '${firstId.nativeStartToken}-stale',
        );
        expect(await isLaunchProcessAlive(stale), isFalse);
        await expectLater(stopLaunchProcess(stale), throwsStateError);
        expect(await isLaunchProcessAlive(firstId), isTrue);
        expect(await isLaunchProcessAlive(secondId), isTrue);
        for (final changed in [
          LaunchProcessIdentity(
            pid: firstId.pid,
            executablePath: firstId.executablePath,
            startTimeUtc: firstId.startTimeUtc,
            nativeStartToken: '${firstId.nativeStartToken}-other',
          ),
          LaunchProcessIdentity(
            pid: firstId.pid,
            executablePath: firstId.executablePath,
            startTimeUtc: firstId.startTimeUtc.subtract(
              const Duration(microseconds: 1),
            ),
            nativeStartToken: firstId.nativeStartToken,
          ),
          LaunchProcessIdentity(
            pid: firstId.pid,
            executablePath: '${firstId.executablePath}.other',
            startTimeUtc: firstId.startTimeUtc,
            nativeStartToken: firstId.nativeStartToken,
          ),
        ]) {
          await expectLater(stopLaunchProcess(changed), throwsStateError);
          expect(await isLaunchProcessAlive(firstId), isTrue);
        }
        expect(await stopLaunchProcess(firstId), isTrue);
        await first.process.exitCode.timeout(const Duration(seconds: 8));
        expect(await isLaunchProcessAlive(firstId), isFalse);
        expect(await stopLaunchProcess(firstId), isFalse);
        expect(await isLaunchProcessAlive(secondId), isTrue);
      } finally {
        await first.dispose();
        await second.dispose();
      }
    },
    skip: !Platform.isWindows && !Platform.isLinux,
  );
  test(
    'stop rejects missing native ownership and never stops its own process',
    () async {
      final unverified = LaunchProcessIdentity(
        pid: pid,
        startTimeUtc: DateTime.now().toUtc(),
        executablePath: Platform.resolvedExecutable,
      );
      await expectLater(stopLaunchProcess(unverified), throwsStateError);
    },
  );
}

Future<_Child> _child() async {
  final directory = await Directory.systemTemp.createTemp(
    'topiaforge process ownership ',
  );
  final script = File('${directory.path}/owned child.dart');
  await script.writeAsString(
    "import 'dart:io'; void main() { stdout.writeln('READY'); stdin.listen((_) {}); }",
  );
  // Dart 3.12's frontend re-execs dartvm on Linux, changing /proc/<pid>/exe.
  // Start the VM itself so the expected image is exactly the owned child image.
  final vmName = Platform.isWindows ? 'dartvm.exe' : 'dartvm';
  final vm = File('${File(Platform.resolvedExecutable).parent.path}/$vmName');
  final executable = await vm.exists() ? vm.path : Platform.resolvedExecutable;
  final process = await Process.start(executable, [script.path]);
  final stderr = process.stderr.drain<void>();
  await process.stdout
      .transform(utf8.decoder)
      .transform(const LineSplitter())
      .firstWhere((line) => line == 'READY')
      .timeout(const Duration(seconds: 15));
  return _Child(directory, process, stderr, executable);
}

class _Child {
  _Child(this.directory, this.process, this.stderr, this.executable);
  final Directory directory;
  final Process process;
  final Future<void> stderr;
  final String executable;
  Future<void> dispose() async {
    await process.stdin.close();
    await process.exitCode.timeout(const Duration(seconds: 10));
    await stderr;
    await directory.delete(recursive: true);
  }
}
