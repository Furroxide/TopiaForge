import 'dart:io';

import 'package:launcher_data/src/bounded_process.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory temporaryDirectory;

  setUp(() async {
    temporaryDirectory = await Directory.systemTemp.createTemp(
      'robotopia-bounded-process-test-',
    );
  });

  tearDown(() async {
    if (await temporaryDirectory.exists()) {
      await temporaryDirectory.delete(recursive: true);
    }
  });

  test('returns exit code and both output streams', () async {
    final script = await _writeScript(temporaryDirectory, '''
import 'dart:io';

void main() {
  stdout.write('ready');
  stderr.write('warning');
  exitCode = 7;
}
''');

    final result = await runBoundedProcess(Platform.resolvedExecutable, [
      script.path,
    ], timeout: const Duration(seconds: 5));

    expect(result.exitCode, 7);
    expect(result.stdout, 'ready');
    expect(result.stderr, 'warning');
  });

  test('terminates a process that exceeds its output budget', () async {
    final script = await _writeScript(temporaryDirectory, '''
import 'dart:async';
import 'dart:io';

Future<void> main() async {
  stdout.write(List.filled(4096, 'x').join());
  await stdout.flush();
  await Future<void>.delayed(const Duration(seconds: 30));
}
''');
    final stopwatch = Stopwatch()..start();

    final error = await _captureFailure(
      runBoundedProcess(
        Platform.resolvedExecutable,
        [script.path],
        timeout: const Duration(seconds: 5),
        maxStdoutBytes: 64,
      ),
    );

    expect(error.failure, BoundedProcessFailure.stdoutLimitExceeded);
    expect(error.stdout, 'x' * 64);
    expect(error.stderr, isEmpty);
    expect(error.exitCode, isNotNull);
    expect(stopwatch.elapsed, lessThan(const Duration(seconds: 5)));
  });

  test('terminates a process that exceeds its total timeout', () async {
    final script = await _writeScript(temporaryDirectory, '''
import 'dart:async';

Future<void> main() async {
  await Future<void>.delayed(const Duration(seconds: 30));
}
''');
    final stopwatch = Stopwatch()..start();

    final error = await _captureFailure(
      runBoundedProcess(Platform.resolvedExecutable, [
        script.path,
      ], timeout: const Duration(milliseconds: 250)),
    );

    expect(error.failure, BoundedProcessFailure.timeout);
    expect(error.exitCode, isNotNull);
    expect(stopwatch.elapsed, lessThan(const Duration(seconds: 5)));
  });
}

Future<File> _writeScript(Directory directory, String contents) async {
  final script = File(p.join(directory.path, 'child.dart'));
  await script.writeAsString(contents);
  return script;
}

Future<BoundedProcessException> _captureFailure(
  Future<BoundedProcessResult> operation,
) async {
  try {
    await operation;
  } on BoundedProcessException catch (error) {
    return error;
  }
  throw TestFailure('Expected BoundedProcessException.');
}
