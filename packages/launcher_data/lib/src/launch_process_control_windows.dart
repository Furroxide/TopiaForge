part of 'launch_process_control.dart';

// Win32 ABI contracts: OpenProcess, GetProcessTimes, QueryFullProcessImageNameW,
// TerminateProcess and WaitForSingleObject on learn.microsoft.com/windows/win32/api.
// Query and termination use the same held handle, not a second PID lookup.
final class _WindowsOwnedProcess implements _OwnedProcess {
  _WindowsOwnedProcess(this.processId, bool terminate)
    : api = WindowsProcessApi() {
    handle = api.openProcess(
      0x100000 | 0x1000 | (terminate ? 1 : 0),
      0,
      processId,
    );
    if (handle == nullptr) {
      if (api.lastError() == 87) throw _ProcessAbsent();
      throw StateError('The process handle could not be verified.');
    }
  }
  // Ownership comes directly from PROCESS_INFORMATION; this path never opens a PID.
  _WindowsOwnedProcess.created(this.processId, this.handle, this.api);

  final int processId;
  final WindowsProcessApi api;
  late final Pointer<Void> handle;
  bool closed = false;

  bool get exited {
    final status = api.wait(handle, 0);
    if (status == 0) return true;
    if (status == 258) return false;
    throw StateError('The held process status could not be read.');
  }

  @override
  Future<LaunchProcessIdentity> identity() async {
    if (exited) throw _ProcessAbsent();
    final heap = api.processHeap();
    final memory = api.allocate(heap, 8, 64 + 32768 * 2);
    if (memory == nullptr) {
      throw StateError('Process identity allocation failed.');
    }
    try {
      final times = memory.cast<Uint32>();
      final length = (memory.cast<Uint8>() + 32).cast<Uint32>();
      final image = (memory.cast<Uint8>() + 64).cast<Uint16>();
      length.value = 32768;
      if (api.times(handle, times, times + 2, times + 4, times + 6) == 0 ||
          api.imageName(handle, 0, image, length) == 0) {
        throw StateError('The held process identity could not be read.');
      }
      if (length.value < 1 || length.value > 32768) {
        throw StateError('Invalid process image length.');
      }
      final creation = (times[1] << 32) | times[0];
      final path = String.fromCharCodes(image.asTypedList(length.value));
      if (exited) throw _ProcessAbsent();
      return LaunchProcessIdentity(
        pid: processId,
        executablePath: path,
        startTimeUtc: DateTime.fromMicrosecondsSinceEpoch(
          (creation - 116444736000000000) ~/ 10,
          isUtc: true,
        ),
        nativeStartToken: 'windows:$creation',
      );
    } finally {
      api.free(heap, 0, memory);
    }
  }

  @override
  Future<bool> stop() async {
    if (exited) return false;
    if (api.terminate(handle, 1) == 0) {
      if (exited) return false;
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
    api.closeHandle(handle);
  }
}

/// Source-internal native call adapter; each creator owns its instance.
class WindowsProcessApi {
  static final library = DynamicLibrary.open('kernel32.dll');
  final openProcess = library
      .lookupFunction<
        Pointer<Void> Function(Uint32, Int32, Uint32),
        Pointer<Void> Function(int, int, int)
      >('OpenProcess');
  final closeHandle = library
      .lookupFunction<
        Int32 Function(Pointer<Void>),
        int Function(Pointer<Void>)
      >('CloseHandle');
  final lastError = library.lookupFunction<Uint32 Function(), int Function()>(
    'GetLastError',
  );
  final wait = library
      .lookupFunction<
        Uint32 Function(Pointer<Void>, Uint32),
        int Function(Pointer<Void>, int)
      >('WaitForSingleObject');
  final terminate = library
      .lookupFunction<
        Int32 Function(Pointer<Void>, Uint32),
        int Function(Pointer<Void>, int)
      >('TerminateProcess');
  final times = library
      .lookupFunction<
        Int32 Function(
          Pointer<Void>,
          Pointer<Uint32>,
          Pointer<Uint32>,
          Pointer<Uint32>,
          Pointer<Uint32>,
        ),
        int Function(
          Pointer<Void>,
          Pointer<Uint32>,
          Pointer<Uint32>,
          Pointer<Uint32>,
          Pointer<Uint32>,
        )
      >('GetProcessTimes');
  final imageName = library
      .lookupFunction<
        Int32 Function(Pointer<Void>, Uint32, Pointer<Uint16>, Pointer<Uint32>),
        int Function(Pointer<Void>, int, Pointer<Uint16>, Pointer<Uint32>)
      >('QueryFullProcessImageNameW');
  final processHeap = library
      .lookupFunction<Pointer<Void> Function(), Pointer<Void> Function()>(
        'GetProcessHeap',
      );
  final allocate = library
      .lookupFunction<
        Pointer<Void> Function(Pointer<Void>, Uint32, UintPtr),
        Pointer<Void> Function(Pointer<Void>, int, int)
      >('HeapAlloc');
  final free = library
      .lookupFunction<
        Int32 Function(Pointer<Void>, Uint32, Pointer<Void>),
        int Function(Pointer<Void>, int, Pointer<Void>)
      >('HeapFree');
}
