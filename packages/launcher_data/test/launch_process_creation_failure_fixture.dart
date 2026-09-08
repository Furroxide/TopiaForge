import 'dart:async';
import 'dart:ffi';
import 'dart:io';
import 'package:launcher_data/src/launch_process_control.dart';
import 'package:path/path.dart' as p;

enum NativeCreationFault { none, times, resume }

final class CreationProcessCalls extends WindowsProcessApi {
  CreationProcessCalls(this.fault);
  final NativeCreationFault fault;
  final activeAllocations = <int>{};
  final closedHandles = <int>[];
  final events = <String>[];
  final errors = <String>[];
  var allocations = 0;
  var frees = 0;
  var opens = 0;
  var timesCalls = 0;
  var imageCalls = 0;
  var terminations = 0;
  Pointer<Void> createdProcess = nullptr;

  @override
  Pointer<Void> Function(int access, int inherit, int processId)
  get openProcess => _openProcess;
  Pointer<Void> _openProcess(int access, int inherit, int processId) {
    opens++;
    return super.openProcess(access, inherit, processId);
  }

  @override
  Pointer<Void> Function(Pointer<Void> heap, int flags, int bytes)
  get allocate => _allocate;
  Pointer<Void> _allocate(Pointer<Void> heap, int flags, int bytes) {
    final value = super.allocate(heap, flags, bytes);
    if (value != nullptr) {
      allocations++;
      if (!activeAllocations.add(value.address)) {
        errors.add('allocation reused');
      }
    }
    return value;
  }

  @override
  int Function(Pointer<Void> heap, int flags, Pointer<Void> pointer) get free =>
      _free;
  int _free(Pointer<Void> heap, int flags, Pointer<Void> pointer) {
    if (!activeAllocations.remove(pointer.address)) {
      errors.add('free of unowned buffer');
      return 0;
    }
    final result = super.free(heap, flags, pointer);
    if (result == 0) {
      activeAllocations.add(pointer.address);
      errors.add('native free failed');
    } else {
      frees++;
    }
    return result;
  }

  @override
  int Function(Pointer<Void> handle) get closeHandle => _closeHandle;
  int _closeHandle(Pointer<Void> handle) {
    if (closedHandles.contains(handle.address)) {
      errors.add('double close');
      return 0;
    }
    closedHandles.add(handle.address);
    events.add('close:${handle.address}');
    final result = super.closeHandle(handle);
    if (result == 0) errors.add('native close failed');
    return result;
  }

  @override
  int Function(Pointer<Void> handle, int timeout) get wait => _wait;
  int _wait(Pointer<Void> handle, int timeout) {
    final result = super.wait(handle, timeout);
    if (handle != createdProcess) errors.add('wait on different process');
    events.add('wait:$result');
    return result;
  }

  @override
  int Function(Pointer<Void> handle, int code) get terminate => _terminate;
  int _terminate(Pointer<Void> handle, int code) {
    terminations++;
    events.add('terminate');
    if (handle != createdProcess) {
      errors.add('termination of different process refused by fixture');
      return 0;
    }
    return super.terminate(handle, code);
  }

  @override
  int Function(
    Pointer<Void> handle,
    Pointer<Uint32> creation,
    Pointer<Uint32> exit,
    Pointer<Uint32> kernel,
    Pointer<Uint32> user,
  )
  get times => _times;
  int _times(
    Pointer<Void> handle,
    Pointer<Uint32> creation,
    Pointer<Uint32> exit,
    Pointer<Uint32> kernel,
    Pointer<Uint32> user,
  ) {
    timesCalls++;
    events.add('times');
    if (handle != createdProcess) {
      errors.add('identity of different process');
    }
    if (fault == NativeCreationFault.times) return 0;
    return super.times(handle, creation, exit, kernel, user);
  }

  @override
  int Function(
    Pointer<Void> handle,
    int flags,
    Pointer<Uint16> image,
    Pointer<Uint32> length,
  )
  get imageName => _imageName;
  int _imageName(
    Pointer<Void> handle,
    int flags,
    Pointer<Uint16> image,
    Pointer<Uint32> length,
  ) {
    imageCalls++;
    if (handle != createdProcess) errors.add('image of different process');
    return super.imageName(handle, flags, image, length);
  }
}

final class CreationCalls extends WindowsCreationApi {
  CreationCalls(this.processCalls);
  final CreationProcessCalls processCalls;
  final native = WindowsProcessApi();
  Pointer<Void> process = nullptr;
  Pointer<Void> thread = nullptr;
  Pointer<Void> monitor = nullptr;
  int? processId;
  var creations = 0;
  var resumes = 0;
  int? inheritHandles;
  int? creationFlags;

  final _currentProcess = DynamicLibrary.open('kernel32.dll')
      .lookupFunction<Pointer<Void> Function(), Pointer<Void> Function()>(
        'GetCurrentProcess',
      );
  final _duplicate = DynamicLibrary.open('kernel32.dll')
      .lookupFunction<
        Int32 Function(
          Pointer<Void>,
          Pointer<Void>,
          Pointer<Void>,
          Pointer<Pointer<Void>>,
          Uint32,
          Int32,
          Uint32,
        ),
        int Function(
          Pointer<Void>,
          Pointer<Void>,
          Pointer<Void>,
          Pointer<Pointer<Void>>,
          int,
          int,
          int,
        )
      >('DuplicateHandle');

  @override
  int Function(
    Pointer<Uint16> application,
    Pointer<Uint16> command,
    Pointer<Void> processSecurity,
    Pointer<Void> threadSecurity,
    int inherit,
    int flags,
    Pointer<Void> environment,
    Pointer<Uint16> directory,
    Pointer<WindowsStartupInfo> startup,
    Pointer<WindowsProcessInformation> information,
  )
  get create => _create;
  int _create(
    Pointer<Uint16> application,
    Pointer<Uint16> command,
    Pointer<Void> processSecurity,
    Pointer<Void> threadSecurity,
    int inherit,
    int flags,
    Pointer<Void> environment,
    Pointer<Uint16> directory,
    Pointer<WindowsStartupInfo> startup,
    Pointer<WindowsProcessInformation> information,
  ) {
    // Every pointer passed to CreateProcessW must belong to the injected arena.
    for (final address in [
      application.address,
      command.address,
      environment.address,
      directory.address,
      startup.address,
      information.address,
    ]) {
      if (!processCalls.activeAllocations.contains(address)) {
        processCalls.errors.add('creation buffer bypassed scoped allocator');
      }
    }
    // Allocate monitor storage before real creation. This independent test-owned
    // duplicate permits cleanup even if a regression closes the original too soon.
    final heap = native.processHeap();
    final slot = native.allocate(heap, 8, sizeOf<Pointer<Void>>());
    if (slot == nullptr) throw StateError('Monitor allocation failed.');
    try {
      final result = super.create(
        application,
        command,
        processSecurity,
        threadSecurity,
        inherit,
        flags,
        environment,
        directory,
        startup,
        information,
      );
      if (result == 0) return result;
      creations++;
      inheritHandles = inherit;
      creationFlags = flags;
      process = information.ref.process;
      thread = information.ref.thread;
      processId = information.ref.processId;
      processCalls.createdProcess = process;
      final self = _currentProcess();
      if (_duplicate(self, process, self, slot.cast(), 0, 0, 2) == 0) {
        processCalls.errors.add('monitor duplication failed');
        // Keep ownership even if the fixture itself cannot establish its monitor.
        native.terminate(process, 1);
        native.wait(process, 5000);
      } else {
        monitor = slot.cast<Pointer<Void>>().value;
      }
      return result;
    } finally {
      native.free(heap, 0, slot);
    }
  }

  @override
  int Function(Pointer<Void> handle) get resume => _resume;
  int _resume(Pointer<Void> handle) {
    resumes++;
    processCalls.events.add('resume');
    if (handle != thread) {
      processCalls.errors.add('resume of different thread refused by fixture');
      return 0xffffffff;
    }
    // Microsoft's DWORD failure is UINT_MAX; do not actually resume this child.
    if (processCalls.fault == NativeCreationFault.resume) return 0xffffffff;
    return super.resume(handle);
  }

  bool get monitoredExited =>
      monitor != nullptr && native.wait(monitor, 0) == 0;

  void dispose() {
    if (monitor != nullptr && native.wait(monitor, 0) != 0) {
      native.terminate(monitor, 1);
      if (native.wait(monitor, 5000) != 0) {
        throw StateError('Owned child exit is uncertain; retain its fixture.');
      }
    }
    // These are test cleanup fallbacks only. Assertions run before this method.
    for (final handle in [thread, process]) {
      if (handle != nullptr &&
          !processCalls.closedHandles.contains(handle.address)) {
        native.closeHandle(handle);
      }
    }
    for (final address in processCalls.activeAllocations.toList()) {
      native.free(native.processHeap(), 0, Pointer<Void>.fromAddress(address));
    }
    if (monitor != nullptr) native.closeHandle(monitor);
    monitor = nullptr;
  }
}

final class NativeFailureFixture {
  NativeFailureFixture(this.root, this.calls);
  final Directory root;
  final CreationCalls calls;
  File get script => File(p.join(root.path, 'harmless child.dart'));
  File get marker => File(p.join(root.path, 'created.marker'));
  File get stop => File(p.join(root.path, 'created.stop'));
  File get siblingMarker => File(p.join(root.path, 'sibling.marker'));
  File get siblingStop => File(p.join(root.path, 'sibling.stop'));
  Process? sibling;
  Future<int>? siblingExit;
  bool siblingExited = false;
  final drains = <Future<void>>[];
  String get executable {
    final vm = p.join(p.dirname(Platform.resolvedExecutable), 'dartvm.exe');
    return File(vm).existsSync() ? vm : Platform.resolvedExecutable;
  }

  static Future<NativeFailureFixture> create(NativeCreationFault fault) async {
    final temp = await Directory.systemTemp.createTemp(
      'topiaforge native fault ',
    );
    final root = Directory(await temp.resolveSymbolicLinks());
    final fixture = NativeFailureFixture(
      root,
      CreationCalls(CreationProcessCalls(fault)),
    );
    await fixture.script.writeAsString(r'''
import 'dart:async'; import 'dart:io';
Future<void> main(List<String> args) async {
  await File(args[0]).writeAsString('$pid');
  final elapsed = Stopwatch()..start();
  while (!File(args[1]).existsSync() && elapsed.elapsed < const Duration(seconds: 30)) {
    if (File('${args[1]}.ping').existsSync() && !File('${args[1]}.pong').existsSync()) {
      await File('${args[1]}.pong').writeAsString('alive');
    }
    await Future<void>.delayed(const Duration(milliseconds: 20));
  }
}
''');
    return fixture;
  }

  Future<void> startSibling() async {
    final child = await Process.start(executable, [
      script.path,
      siblingMarker.path,
      siblingStop.path,
    ], workingDirectory: root.path);
    sibling = child;
    drains.addAll([child.stdout.drain<void>(), child.stderr.drain<void>()]);
    siblingExit = child.exitCode.then((value) {
      siblingExited = true;
      return value;
    });
    await waitForFile(siblingMarker);
  }

  Future<void> proveSiblingResponsive() async {
    await File('${siblingStop.path}.ping').writeAsString('probe');
    await waitForFile(File('${siblingStop.path}.pong'));
  }

  Future<LaunchProcessReceipt> start() =>
      WindowsLaunchProcessCreator(
        processApi: calls.processCalls,
        creationApi: calls,
      ).start(
        executable: executable,
        arguments: [script.path, marker.path, stop.path],
        workingDirectory: root.path,
        environment: const {},
        inheritParentEnvironment: false,
      );

  Future<void> dispose() async {
    try {
      if (sibling != null) {
        await siblingStop.writeAsString('stop');
        await sibling!.stdin.close();
        await siblingExit!.timeout(const Duration(seconds: 8));
        await Future.wait(drains);
      }
    } finally {
      calls.dispose();
    }
    await root.delete(recursive: true);
  }
}

Future<void> waitForFile(File file) async {
  final elapsed = Stopwatch()..start();
  while (!await file.exists()) {
    if (elapsed.elapsed >= const Duration(seconds: 10)) {
      throw TimeoutException('Harmless child did not write its marker.');
    }
    await Future<void>.delayed(const Duration(milliseconds: 20));
  }
}
