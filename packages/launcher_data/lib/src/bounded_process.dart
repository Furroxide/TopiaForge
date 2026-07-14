import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

const int _defaultOutputLimitBytes = 1024 * 1024;

/// Why [runBoundedProcess] stopped a child process before normal completion.
enum BoundedProcessFailure {
  timeout,
  stdoutLimitExceeded,
  stderrLimitExceeded,
  outputReadFailed,
  terminationFailed,
}

/// The result of a process that completed within its configured bounds.
final class BoundedProcessResult {
  const BoundedProcessResult({
    required this.exitCode,
    required this.stdout,
    required this.stderr,
  });

  final int exitCode;
  final String stdout;
  final String stderr;
}

/// Thrown when a process exceeds a time or output bound.
///
/// [stdout] and [stderr] contain at most the configured number of bytes after
/// UTF-8 decoding. [exitCode] is populated when the process exited after the
/// termination request.
final class BoundedProcessException implements Exception {
  const BoundedProcessException({
    required this.failure,
    required this.stdout,
    required this.stderr,
    this.exitCode,
  });

  final BoundedProcessFailure failure;
  final int? exitCode;
  final String stdout;
  final String stderr;

  @override
  String toString() => switch (failure) {
    BoundedProcessFailure.timeout => 'Process exceeded its time limit.',
    BoundedProcessFailure.stdoutLimitExceeded =>
      'Process exceeded its standard-output limit.',
    BoundedProcessFailure.stderrLimitExceeded =>
      'Process exceeded its standard-error limit.',
    BoundedProcessFailure.outputReadFailed =>
      'Process output could not be read.',
    BoundedProcessFailure.terminationFailed =>
      'Process exceeded a bound and could not be terminated.',
  };
}

/// Runs a child process while bounding its execution time and captured output.
///
/// Output is decoded as UTF-8 with malformed byte sequences replaced. If the
/// process exceeds a bound it is terminated, then force-killed if it does not
/// exit within [gracefulTerminationTimeout].
Future<BoundedProcessResult> runBoundedProcess(
  String executable,
  List<String> arguments, {
  String? workingDirectory,
  Map<String, String>? environment,
  bool includeParentEnvironment = true,
  bool runInShell = false,
  Duration timeout = const Duration(seconds: 30),
  int maxStdoutBytes = _defaultOutputLimitBytes,
  int maxStderrBytes = _defaultOutputLimitBytes,
  Duration gracefulTerminationTimeout = const Duration(seconds: 1),
  Duration forcedTerminationTimeout = const Duration(seconds: 2),
}) async {
  _validateBounds(
    timeout: timeout,
    maxStdoutBytes: maxStdoutBytes,
    maxStderrBytes: maxStderrBytes,
    gracefulTerminationTimeout: gracefulTerminationTimeout,
    forcedTerminationTimeout: forcedTerminationTimeout,
  );

  final startedAt = Stopwatch()..start();
  final process = await Process.start(
    executable,
    arguments,
    workingDirectory: workingDirectory,
    environment: environment,
    includeParentEnvironment: includeParentEnvironment,
    runInShell: runInShell,
    mode: ProcessStartMode.normal,
  );
  final exitFuture = process.exitCode;
  final stdout = _BoundedOutput(maxStdoutBytes);
  final stderr = _BoundedOutput(maxStderrBytes);
  final failure = Completer<BoundedProcessFailure>();

  void fail(BoundedProcessFailure reason) {
    if (!failure.isCompleted) {
      failure.complete(reason);
    }
  }

  final stdoutDone = Completer<void>();
  final stderrDone = Completer<void>();
  final stdoutSubscription = process.stdout.listen(
    (chunk) {
      if (!stdout.add(chunk)) {
        fail(BoundedProcessFailure.stdoutLimitExceeded);
      }
    },
    onError: (Object _, StackTrace _) {
      stdoutDone.complete();
      fail(BoundedProcessFailure.outputReadFailed);
    },
    onDone: stdoutDone.complete,
    cancelOnError: true,
  );
  final stderrSubscription = process.stderr.listen(
    (chunk) {
      if (!stderr.add(chunk)) {
        fail(BoundedProcessFailure.stderrLimitExceeded);
      }
    },
    onError: (Object _, StackTrace _) {
      stderrDone.complete();
      fail(BoundedProcessFailure.outputReadFailed);
    },
    onDone: stderrDone.complete,
    cancelOnError: true,
  );

  final remaining = timeout - startedAt.elapsed;
  Timer? timeoutTimer;
  if (remaining <= Duration.zero) {
    fail(BoundedProcessFailure.timeout);
  } else {
    timeoutTimer = Timer(remaining, () => fail(BoundedProcessFailure.timeout));
  }

  final completed = Future.wait<Object?>([
    exitFuture,
    stdoutDone.future,
    stderrDone.future,
  ]);
  final outcome = await Future.any<Object>([
    completed.then<Object>((values) => _ProcessCompleted(values.first! as int)),
    failure.future.then<Object>(_ProcessFailed.new),
  ]);
  timeoutTimer?.cancel();

  if (outcome case _ProcessCompleted(:final exitCode)) {
    return BoundedProcessResult(
      exitCode: exitCode,
      stdout: stdout.text,
      stderr: stderr.text,
    );
  }

  final failed = outcome as _ProcessFailed;
  final exitCode = await _terminateProcess(
    process,
    exitFuture,
    gracefulTerminationTimeout: gracefulTerminationTimeout,
    forcedTerminationTimeout: forcedTerminationTimeout,
  );
  await Future.wait<void>([
    stdoutSubscription.cancel(),
    stderrSubscription.cancel(),
  ]);
  throw BoundedProcessException(
    failure: exitCode == null
        ? BoundedProcessFailure.terminationFailed
        : failed.failure,
    exitCode: exitCode,
    stdout: stdout.text,
    stderr: stderr.text,
  );
}

void _validateBounds({
  required Duration timeout,
  required int maxStdoutBytes,
  required int maxStderrBytes,
  required Duration gracefulTerminationTimeout,
  required Duration forcedTerminationTimeout,
}) {
  if (timeout <= Duration.zero) {
    throw ArgumentError.value(timeout, 'timeout', 'must be positive');
  }
  if (maxStdoutBytes < 0) {
    throw ArgumentError.value(
      maxStdoutBytes,
      'maxStdoutBytes',
      'must not be negative',
    );
  }
  if (maxStderrBytes < 0) {
    throw ArgumentError.value(
      maxStderrBytes,
      'maxStderrBytes',
      'must not be negative',
    );
  }
  if (gracefulTerminationTimeout <= Duration.zero) {
    throw ArgumentError.value(
      gracefulTerminationTimeout,
      'gracefulTerminationTimeout',
      'must be positive',
    );
  }
  if (forcedTerminationTimeout <= Duration.zero) {
    throw ArgumentError.value(
      forcedTerminationTimeout,
      'forcedTerminationTimeout',
      'must be positive',
    );
  }
}

Future<int?> _terminateProcess(
  Process process,
  Future<int> exitFuture, {
  required Duration gracefulTerminationTimeout,
  required Duration forcedTerminationTimeout,
}) async {
  process.kill();
  final gracefulExit = await _waitForExit(
    exitFuture,
    gracefulTerminationTimeout,
  );
  if (gracefulExit != null) {
    return gracefulExit;
  }

  if (Platform.isWindows) {
    process.kill();
  } else {
    process.kill(ProcessSignal.sigkill);
  }
  return _waitForExit(exitFuture, forcedTerminationTimeout);
}

Future<int?> _waitForExit(Future<int> exitFuture, Duration timeout) async {
  try {
    return await exitFuture.timeout(timeout);
  } on TimeoutException {
    return null;
  }
}

final class _BoundedOutput {
  _BoundedOutput(this.limit);

  final int limit;
  final BytesBuilder _bytes = BytesBuilder(copy: false);
  bool _overflowed = false;

  bool add(List<int> chunk) {
    if (_overflowed) {
      return true;
    }
    final remaining = limit - _bytes.length;
    if (chunk.length <= remaining) {
      _bytes.add(chunk);
      return true;
    }
    if (remaining > 0) {
      _bytes.add(chunk.sublist(0, remaining));
    }
    _overflowed = true;
    return false;
  }

  String get text => utf8.decode(_bytes.toBytes(), allowMalformed: true);
}

final class _ProcessCompleted {
  const _ProcessCompleted(this.exitCode);

  final int exitCode;
}

final class _ProcessFailed {
  const _ProcessFailed(this.failure);

  final BoundedProcessFailure failure;
}
