import 'dart:async';
import 'dart:convert';
import 'dart:io';
import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import '../live_acceptance_models.dart';
import '../live_acceptance_session.dart';
import '../release_strict_json.dart';
import 'native_io.dart';
import 'native_options.dart';

typedef SandboxIsolatedCommand =
    Future<int> Function(
      List<String> arguments,
      AcceptanceIsolationContext context,
    );

final class SandboxFixtureConfigLease {
  SandboxFixtureConfigLease._(this._entries);
  final List<(String, List<int>?, String)> _entries;
  static SandboxFixtureConfigLease install(
    AcceptanceIsolationContext context,
    String challenge,
  ) {
    context.verify();
    final root = p.join(context.managerRoot, 'config');
    requireAcceptanceUnlinkedPath(root);
    Directory(root).createSync(recursive: true);
    final entries = <(String, List<int>?, String)>[];
    try {
      for (final id in [
        'dev.topiaforge.sandbox-acceptance',
        'dev.topiaforge.sandbox-acceptance-native',
      ]) {
        final path = p.join(root, '$id.json');
        requireAcceptanceUnlinkedPath(path);
        final previous = File(path).existsSync() ? readNativeFile(path) : null;
        final bytes = utf8.encode(
          jsonEncode({
            'schemaVersion': 1,
            'value': {'enabled': true, 'challenge': challenge},
          }),
        );
        File(path).writeAsBytesSync(bytes, flush: true);
        entries.add((path, previous, nativeHash(bytes)));
      }
      return SandboxFixtureConfigLease._(entries);
    } on Object {
      SandboxFixtureConfigLease._(entries).restore();
      rethrow;
    }
  }

  void restore() {
    final failures = <String>[];
    for (final entry in _entries.reversed) {
      try {
        if (nativeHash(readNativeFile(entry.$1)) != entry.$3) {
          throw StateError('Fixture configuration changed outside this run.');
        }
        requireAcceptanceUnlinkedPath(entry.$1);
        if (entry.$2 == null) {
          File(entry.$1).deleteSync();
        } else {
          File(entry.$1).writeAsBytesSync(entry.$2!, flush: true);
        }
      } on Object {
        failures.add('Fixture config restoration was incomplete.');
      }
    }
    _entries.clear();
    if (failures.isNotEmpty) throw StateError(failures.join(' '));
  }
}

Future<String> waitForSandboxPackageReceipts(
  AcceptanceIsolationContext context,
  SandboxNativePackageInventory inventory,
  OwnedAcceptanceSession session,
  DateTime startedAt,
  Duration timeout,
) async {
  final deadline = DateTime.now().toUtc().add(timeout);
  while (DateTime.now().toUtc().isBefore(deadline)) {
    context.verify();
    if (await isLaunchProcessAlive(session.originalProcess) != true) {
      throw StateError(
        'Original native game process exited or became unverifiable.',
      );
    }
    final file = p.join(context.managerRoot, 'logs', 'last-run.json');
    if (File(file).existsSync()) {
      final bytes = readNativeFile(file, maximum: 16 * 1024 * 1024);
      decodeReleaseObject(
        bytes,
        maximumBytes: 16 * 1024 * 1024,
        label: 'actual manager last-run',
      );
      final report = LiveAcceptanceLastRun.tryParse(utf8.decode(bytes));
      if (report != null &&
          !report.completedAtUtc.isBefore(startedAt) &&
          report.rootError.isEmpty &&
          report.sessionId.isNotEmpty &&
          inventory.packages.every(
            (package) =>
                report.package(package.id)?.status == 'loaded' &&
                report.package(package.id)?.matchesReceipt(package.receipt) ==
                    true,
          )) {
        return report.sessionId;
      }
    }
    await Future<void>.delayed(const Duration(milliseconds: 100));
  }
  throw StateError(
    'Native package receipt or fresh manager session is unconfirmed.',
  );
}

final class SandboxBrokerCancellation {
  SandboxBrokerCancellation({
    required this.runRoot,
    required this.challenge,
    required this.exit,
    required this.isExited,
    required this.forceKill,
    this.gracePeriod = const Duration(seconds: 20),
    this.exitGrace = const Duration(seconds: 15),
  });
  final String runRoot, challenge;
  final Future<int> exit;
  final bool Function() isExited;
  final void Function() forceKill;
  final Duration gracePeriod, exitGrace;
  Future<void>? _stopping;
  bool forced = false;

  Future<void> request() => _stopping ??= _stop();
  Future<void> _stop() async {
    if (isExited()) return;
    var requestFailed = false;
    try {
      final path = nativeChild(runRoot, 'broker-cancel.json');
      final expected = <String, Object?>{
        'schemaVersion': 1,
        'kind': 'sandbox-broker-cancel-v1',
        'challenge': challenge,
      };
      if (File(path).existsSync()) {
        if (jsonEncode(readNativeDocument(path)) != jsonEncode(expected)) {
          throw StateError('Broker cancellation record is inconsistent.');
        }
      } else {
        writeNativeDocument(path, expected);
      }
    } on Object {
      requestFailed = true;
    }
    try {
      await exit.timeout(gracePeriod);
    } on TimeoutException {
      if (!isExited()) {
        forced = true;
        forceKill();
      }
      await exit.timeout(exitGrace);
    }
    if (forced || requestFailed) {
      throw StateError('Broker cooperative cancellation was unconfirmed.');
    }
  }
}

Future<int> runSandboxBroker({
  required SandboxNativePaths paths,
  required String requestPath,
  required String runRoot,
  required void Function(int) onStarted,
}) async {
  final request = readNativeDocument(requestPath);
  final challenge = request['challenge'];
  if (challenge is! String || !RegExp(r'^[0-9a-f]{64}$').hasMatch(challenge)) {
    throw StateError('Native broker cancellation challenge is invalid.');
  }
  final process = await Process.start(
    paths.brokerPath,
    ['--request', requestPath],
    workingDirectory: p.dirname(paths.brokerPath),
    runInShell: false,
    includeParentEnvironment: false,
    environment: {
      'SystemRoot': readWindowsSystemRoot(),
      'WINDIR': readWindowsSystemRoot(),
      'PATH': p.join(readWindowsSystemRoot(), 'System32'),
    },
  );
  onStarted(process.pid);
  var exited = false;
  final exit = process.exitCode.then((code) {
    exited = true;
    return code;
  });
  final cancellation = SandboxBrokerCancellation(
    runRoot: runRoot,
    challenge: challenge,
    exit: exit,
    isExited: () => exited,
    forceKill: () {
      process.kill();
    },
  );
  var interrupted = false;
  var outputExceeded = false;
  void requestStop() {
    // Keep draining output while the broker's bounded input/fixture finally runs.
    cancellation.request().ignore();
  }

  Future<void> drain(Stream<List<int>> stream, String path) async {
    requireAcceptanceUnlinkedPath(path);
    final file = File(path)..createSync(exclusive: true);
    final sink = file.openWrite();
    var count = 0;
    try {
      await for (final bytes in stream) {
        count += bytes.length;
        if (count > 16 * 1024 * 1024) {
          outputExceeded = true;
          requestStop();
          continue;
        }
        sink.add(bytes);
      }
    } finally {
      await sink.flush();
      await sink.close();
    }
  }

  final output = drain(
    process.stdout,
    nativeChild(runRoot, 'broker-stdout.log'),
  );
  final errors = drain(
    process.stderr,
    nativeChild(runRoot, 'broker-stderr.log'),
  );
  StreamSubscription<ProcessSignal>? interrupt;
  try {
    interrupt = ProcessSignal.sigint.watch().listen((_) {
      interrupted = true;
      requestStop();
    });
    final code = await exit.timeout(
      paths.timeout + const Duration(seconds: 30),
      onTimeout: () async {
        await cancellation.request();
        return 1;
      },
    );
    await Future.wait([output, errors]);
    if (cancellation.forced) {
      throw StateError(
        'Native broker was interrupted or exceeded its output bound.',
      );
    }
    return outputExceeded || interrupted ? 1 : code;
  } finally {
    await interrupt?.cancel();
    if (!exited) await cancellation.request();
    await exit.timeout(const Duration(seconds: 15));
    await Future.wait([output, errors]);
  }
}

/// One overall budget bounds both acknowledgement and package receipt polling.
/// This does not detach an owned launch/cleanup future on a timeout.
Duration sandboxNativeRemainingBudget(Duration total, Duration elapsed) {
  final remaining = total - elapsed;
  if (remaining <= Duration.zero) {
    throw StateError('Native overall run budget expired.');
  }
  const startupCap = Duration(minutes: 2);
  return remaining < startupCap ? remaining : startupCap;
}
