part of 'launch_process_control.dart';

// Linux pidfd_open(2), pidfd_send_signal(2), poll(2) and proc_pid_stat(5).
// No PID-based kill fallback: an unavailable pidfd makes ownership unknown.
final class _LinuxOwnedProcess implements _OwnedProcess {
  _LinuxOwnedProcess(this.processId) {
    descriptor = api.open(processId, 0);
    if (descriptor < 0) {
      if (api.error().value == 3) throw _ProcessAbsent();
      throw StateError('A PID file descriptor could not be opened.');
    }
  }
  final int processId;
  final api = _LinuxProcessApi();
  late final int descriptor;
  bool closed = false;

  bool get exited {
    final memory = api.allocate(1, 8);
    if (memory == nullptr) throw StateError('Process poll allocation failed.');
    try {
      memory.cast<Int32>().value = descriptor;
      final shorts = memory.cast<Int16>();
      shorts[2] = 1;
      final result = api.poll(memory, 1, 0);
      if (result < 0) {
        throw StateError('The held process status could not be read.');
      }
      return result > 0 && (shorts[3] & 0x11) != 0;
    } finally {
      api.free(memory);
    }
  }

  @override
  Future<LaunchProcessIdentity> identity() async {
    if (exited) throw _ProcessAbsent();
    try {
      final stat = await _boundedProcText('/proc/$processId/stat', 65536);
      final close = stat.lastIndexOf(')');
      final fields = stat.substring(close + 2).trim().split(RegExp(r'\s+'));
      if (close < 1 || fields.length < 20 || !stat.startsWith('$processId (')) {
        throw StateError('Invalid process generation data.');
      }
      final ticks = int.parse(fields[19]);
      final hertz = api.sysconf(2); // Linux _SC_CLK_TCK.
      final boot = await _boundedProcText(
        '/proc/sys/kernel/random/boot_id',
        128,
      );
      final system = await _boundedProcText('/proc/stat', 1024 * 1024);
      final bootSeconds = int.parse(
        system
            .split('\n')
            .singleWhere((line) => line.startsWith('btime '))
            .substring(6)
            .trim(),
      );
      if (ticks < 0 ||
          hertz <= 0 ||
          !RegExp(r'^[0-9a-f-]{36}$').hasMatch(boot.trim())) {
        throw StateError('Invalid process generation data.');
      }
      final image = await Link('/proc/$processId/exe').target();
      if (exited) throw _ProcessAbsent();
      return LaunchProcessIdentity(
        pid: processId,
        executablePath: image,
        startTimeUtc: _linuxProcessEpochs.startTimeUtc(
          bootId: boot.trim(),
          bootSeconds: bootSeconds,
          ticks: ticks,
          hertz: hertz,
        ),
        nativeStartToken: 'linux:${boot.trim()}:$ticks',
      );
    } on FileSystemException {
      if (exited) throw _ProcessAbsent();
      rethrow;
    }
  }

  @override
  Future<bool> stop() async {
    if (exited) return false;
    if (api.signal(descriptor, 15, nullptr, 0) != 0) {
      if (api.error().value == 3) return false;
      throw StateError('The verified process could not be stopped.');
    }
    final elapsed = Stopwatch()..start();
    while (!exited) {
      if (elapsed.elapsed >= const Duration(seconds: 5)) {
        throw StateError(
          'The verified process did not exit before the restart timeout.',
        );
      }
      await Future<void>.delayed(const Duration(milliseconds: 50));
    }
    return true;
  }

  @override
  void close() {
    if (closed) return;
    closed = true;
    api.close(descriptor);
  }
}

Future<String> _boundedProcText(String path, int limit) async {
  final bytes = <int>[];
  await for (final chunk in File(path).openRead()) {
    if (chunk.length > limit - bytes.length) {
      throw StateError('Process metadata exceeds its bound.');
    }
    bytes.addAll(chunk);
  }
  return String.fromCharCodes(bytes);
}

final class _LinuxProcessApi {
  static final library = DynamicLibrary.open('libc.so.6');
  final open = library
      .lookupFunction<Int32 Function(Int32, Uint32), int Function(int, int)>(
        'pidfd_open',
      );
  final signal = library
      .lookupFunction<
        Int32 Function(Int32, Int32, Pointer<Void>, Uint32),
        int Function(int, int, Pointer<Void>, int)
      >('pidfd_send_signal');
  final poll = library
      .lookupFunction<
        Int32 Function(Pointer<Void>, UintPtr, Int32),
        int Function(Pointer<Void>, int, int)
      >('poll');
  final close = library
      .lookupFunction<Int32 Function(Int32), int Function(int)>('close');
  final error = library
      .lookupFunction<Pointer<Int32> Function(), Pointer<Int32> Function()>(
        '__errno_location',
      );
  final sysconf = library
      .lookupFunction<Long Function(Int32), int Function(int)>('sysconf');
  final allocate = library
      .lookupFunction<
        Pointer<Void> Function(UintPtr, UintPtr),
        Pointer<Void> Function(int, int)
      >('calloc');
  final free = library
      .lookupFunction<
        Void Function(Pointer<Void>),
        void Function(Pointer<Void>)
      >('free');
}

final _linuxProcessEpochs = LinuxProcessStartEpochCache();

/// Process-local conversion of kernel birth ticks into a stable display time.
/// /proc/stat emits getboottime64(), a wall-clock epoch that can move when
/// CLOCK_REALTIME is corrected. Capture it once per boot, never per read.
/// Kernel source: https://github.com/torvalds/linux/blob/v6.16/fs/proc/stat.c
/// Timekeeping: https://www.kernel.org/doc/html/latest/core-api/timekeeping.html
/// The native boot-ID/ticks token and strict four-field comparison stay intact.
final class LinuxProcessStartEpochCache {
  final Map<String, int> _bootEpochs = {};

  DateTime startTimeUtc({
    required String bootId,
    required int bootSeconds,
    required int ticks,
    required int hertz,
  }) {
    final epoch = _bootEpochs.putIfAbsent(bootId, () => bootSeconds);
    return DateTime.fromMicrosecondsSinceEpoch(
      epoch * 1000000 + ticks * 1000000 ~/ hertz,
      isUtc: true,
    );
  }
}
