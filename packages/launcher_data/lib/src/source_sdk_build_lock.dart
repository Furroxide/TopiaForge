import 'dart:async';
import 'dart:convert';
import 'dart:ffi';
import 'dart:io';
import 'dart:isolate';

import 'package:path/path.dart' as p;

import 'bounded_process.dart';

// Internal observation seam for ownership regressions. Never sent to SDK code.
final sourceSdkOwnershipObserver = Object();
final _sourceSdkOwner = Object();

typedef SourceSdkOwnerSpawner =
    Future<Isolate> Function(List<Object?> arguments, SendPort replies);

Future<Isolate> _spawnSdkOwner(List<Object?> arguments, SendPort replies) =>
    Isolate.spawn(sourceSdkOwnerMain, arguments, onExit: replies);

/// The caller never owns native resources or starts SDK children. A separate
/// isolate retains ownership through caller exit and every started child's exit.
Future<T> withSourceSdkBuildLock<T>(
  Directory repository,
  Future<T> Function() action, {
  SourceSdkOwnerSpawner spawnOwner = _spawnSdkOwner,
}) async {
  final replies = ReceivePort();
  final messages = StreamIterator<dynamic>(replies);
  SendPort? owner;
  try {
    await spawnOwner([
      repository.resolveSymbolicLinksSync(),
      Isolate.current.controlPort,
      replies.sendPort,
      Zone.current[sourceSdkOwnershipObserver] as SendPort?,
      const Duration(seconds: 30),
    ], replies.sendPort);
    if (!await messages.moveNext()) throw StateError('SDK owner stopped.');
    final response = messages.current;
    if (response is! SendPort) {
      if (response is List) {
        Error.throwWithStackTrace(
          response[0] as Object,
          StackTrace.fromString(response[1] as String),
        );
      }
      throw StateError('SDK owner stopped before acquisition.');
    }
    owner = response;
    return await runZoned(action, zoneValues: {_sourceSdkOwner: owner});
  } finally {
    if (owner != null) {
      owner.send('release');
      // Acknowledge detachment, not child termination. A failed termination
      // still propagates promptly while the owner keeps the checkout protected.
      await messages.moveNext();
    }
    await messages.cancel();
    replies.close();
  }
}

/// SDK builds execute in the native-resource owner, so caller-isolate exit
/// cannot discard process handles, output draining, bounds or exit observation.
Future<BoundedProcessResult> runOwnedSourceSdkBuild(
  String executable,
  List<String> arguments, {
  required String workingDirectory,
  required Duration timeout,
  required int maxStdoutBytes,
  required int maxStderrBytes,
}) async {
  final owner = Zone.current[_sourceSdkOwner] as SendPort;
  final reply = ReceivePort();
  try {
    owner.send([
      reply.sendPort,
      executable,
      arguments,
      workingDirectory,
      timeout,
      maxStdoutBytes,
      maxStderrBytes,
    ]);
    final result = await reply.first as List;
    if (result[0] == true) return result[1] as BoundedProcessResult;
    Error.throwWithStackTrace(
      result[1] as Object,
      StackTrace.fromString(result[2] as String),
    );
  } finally {
    reply.close();
  }
}

// Internal entrypoint/spawn seam also permits deterministic scheduling of the
// pre-claim handshake in regressions. It is not exported by launcher_data.
Future<void> sourceSdkOwnerMain(List<Object?> args) async {
  final messages = ReceivePort();
  final caller = Isolate(args[1] as SendPort);
  final reply = args[2] as SendPort;
  final observer = args[3] as SendPort?;
  final release = Completer<void>();
  var detached = false;
  var active = 0;
  var started = false;
  var granted = false;
  var bootstrapClosed = false;
  Timer? bootstrapTimer;
  void failBootstrap(Object error) {
    if (started || bootstrapClosed) return;
    bootstrapClosed = true;
    bootstrapTimer?.cancel();
    caller.removeOnExitListener(messages.sendPort);
    reply.send([error, StackTrace.current.toString()]);
    messages.close();
  }

  void drained() {
    if (detached && active == 0 && !release.isCompleted) release.complete();
  }

  Future<void> build(List request) async {
    final resultPort = request[0] as SendPort;
    Future<int>? childExit;
    try {
      final result = await runBoundedProcess(
        request[1] as String,
        (request[2] as List).cast<String>(),
        workingDirectory: request[3] as String,
        timeout: request[4] as Duration,
        maxStdoutBytes: request[5] as int,
        maxStderrBytes: request[6] as int,
        observeExit: (exit) {
          childExit = exit;
        },
      );
      resultPort.send([true, result]);
    } catch (error, stack) {
      resultPort.send([false, error, stack.toString()]);
    } finally {
      // Includes terminationFailed: never infer process death from a timeout.
      if (childExit != null) await childExit;
      active--;
      drained();
    }
  }

  Future<void> own() async {
    try {
      await _withNativeLock(
        Directory(args[0] as String),
        () async {
          if (detached) return;
          granted = true;
          observer?.send('owned');
          reply.send(messages.sendPort);
          await release.future;
        },
        () {
          observer?.send('waiting');
        },
      );
      observer?.send('released');
    } catch (error, stack) {
      reply.send([error, stack.toString()]);
    } finally {
      caller.removeOnExitListener(messages.sendPort);
      messages.close();
    }
  }

  messages.listen((message) {
    if (bootstrapClosed) return;
    if (message == 'watched' && !started) {
      started = true;
      bootstrapTimer?.cancel();
      unawaited(own());
    } else if (message == 'release' || message == 'abandon') {
      detached = true;
      if (!started) {
        failBootstrap(StateError('SDK owner bootstrap was abandoned.'));
        return;
      }
      if (message == 'release') reply.send('detached');
      drained();
    } else if (message is List && granted && !detached) {
      active++;
      unawaited(build(message));
    }
  });
  final bootstrapLimit = args[4] as Duration;
  bootstrapTimer = Timer(bootstrapLimit, () {
    failBootstrap(
      TimeoutException(
        'SDK owner bootstrap received no caller acknowledgment.',
        bootstrapLimit,
      ),
    );
  });
  // Optional test observer sees the real bootstrap mailbox before monitoring.
  if (args.length > 5) (args[5] as SendPort).send(messages.sendPort);
  try {
    caller.addOnExitListener(messages.sendPort, response: 'abandon');
    // Control messages from this sender are processed in order. No lock is
    // acquired, and no caller action runs, before exit monitoring is installed.
    caller.ping(messages.sendPort, response: 'watched');
  } catch (error) {
    failBootstrap(error);
  }
}

Future<T> _withNativeLock<T>(
  Directory repository,
  Future<T> Function() action,
  void Function() waiting,
) async {
  final root = repository;
  if (!Platform.isWindows) return _withDirectoryFlock(root, action, waiting);

  final storage = Directory(p.join(root.path, '.dart_tool'));
  _requireOrdinary(storage.path, FileSystemEntityType.directory);
  storage.createSync();
  final file = File(p.join(storage.path, 'topiaforge-source-sdk.lock'));
  _requireOrdinary(file.path, FileSystemEntityType.file);
  final handle = await file.open(mode: FileMode.append);
  try {
    // Windows locks belong to handles, including within one process. POSIX
    // RandomAccessFile locks instead belong to processes and admit isolates.
    try {
      await handle.lock(FileLock.exclusive);
    } on FileSystemException catch (error) {
      if (error.osError?.errorCode != 33) rethrow;
      waiting();
      await handle.lock(FileLock.blockingExclusive);
    }
    return await action();
  } finally {
    await handle.close();
  }
}

void _requireOrdinary(String path, FileSystemEntityType expected) {
  final type = FileSystemEntity.typeSync(path, followLinks: false);
  if (type != FileSystemEntityType.notFound && type != expected) {
    throw StateError('SDK build lock storage must not be linked.');
  }
}

// flock locks belong to open file descriptions, so independent opens contend
// across isolates as well as processes. Lock the existing directory inode:
// no removable lock file, stale marker or spelling-based identity is needed.
final _libc = DynamicLibrary.process();
final _calloc = _libc
    .lookupFunction<
      Pointer<Uint8> Function(IntPtr, IntPtr),
      Pointer<Uint8> Function(int, int)
    >('calloc');
final _free = _libc
    .lookupFunction<
      Void Function(Pointer<Uint8>),
      void Function(Pointer<Uint8>)
    >('free');
final _open = _libc
    .lookupFunction<
      Int32 Function(Pointer<Uint8>, Int32),
      int Function(Pointer<Uint8>, int)
    >('open');
final _close = _libc.lookupFunction<Int32 Function(Int32), int Function(int)>(
  'close',
);
final _flock = _libc
    .lookupFunction<Int32 Function(Int32, Int32), int Function(int, int)>(
      'flock',
    );
final _errno = _libc
    .lookupFunction<Pointer<Int32> Function(), Pointer<Int32> Function()>(
      Platform.isMacOS ? '__error' : '__errno_location',
    );

Future<T> _withDirectoryFlock<T>(
  Directory root,
  Future<T> Function() action,
  void Function() waiting,
) async {
  final bytes = utf8.encode(root.path);
  final path = _calloc(bytes.length + 1, 1);
  if (path == nullptr) throw StateError('Could not allocate SDK lock path.');
  late int fd;
  try {
    path.asTypedList(bytes.length).setAll(0, bytes);
    // O_RDONLY | O_CLOEXEC: builds must not inherit the ownership descriptor.
    fd = _open(path, Platform.isMacOS ? 0x1000000 : 0x80000);
  } finally {
    _free(path);
  }
  if (fd < 0) throw FileSystemException('Could not open SDK build lock');
  var announced = false;
  try {
    while (_flock(fd, 2 | 4) != 0) {
      // LOCK_EX | LOCK_NB
      final error = _errno().value;
      if (error != (Platform.isMacOS ? 35 : 11) && error != 4) {
        throw FileSystemException('Could not acquire SDK build lock');
      }
      if (!announced) {
        waiting();
        announced = true;
      }
      await Future<void>.delayed(const Duration(milliseconds: 25));
    }
    return await action();
  } finally {
    _close(fd);
  }
}
