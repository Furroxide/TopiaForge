part of 'launch_process_control.dart';

// Native ABI/argument rules are documented by Microsoft for CreateProcessW,
// PROCESS_INFORMATION, STARTUPINFOW and Parsing C command-line arguments.
Future<LaunchProcessReceipt> _createWindowsProcess(
  String executable,
  List<String> arguments,
  String directory,
  Map<String, String> overrides,
) async {
  final command = [
    '"$executable"',
    ...arguments.map(_quoteWindowsArgument),
  ].join(' ');
  // CreateProcessW includes the terminating null in its 32,767 character limit.
  if (command.length >= 32767) {
    throw ArgumentError('The Windows process command line is too long.');
  }
  final api = _WindowsCreationApi();
  final memory = _WindowsCreationMemory();
  _WindowsOwnedProcess? process;
  Pointer<Void> thread = nullptr;
  var admitted = false;
  try {
    final application = memory.text(executable);
    final commandLine = memory.text(command);
    final currentDirectory = memory.text(directory);
    final environment = _windowsEnvironment(memory, api, overrides);
    final startup = memory
        .allocate(sizeOf<_WindowsStartupInfo>())
        .cast<_WindowsStartupInfo>();
    startup.ref.cb = sizeOf<_WindowsStartupInfo>();
    final information = memory
        .allocate(sizeOf<_WindowsProcessInformation>())
        .cast<_WindowsProcessInformation>();
    // Detached, a new process group, Unicode environment, and suspended startup.
    // No inheritable handles, console pipes, job objects, or shell are created.
    if (api.create(
          application,
          commandLine,
          nullptr,
          nullptr,
          0,
          0x00000008 | 0x00000200 | 0x00000400 | 0x00000004,
          environment.cast(),
          currentDirectory,
          startup,
          information,
        ) ==
        0) {
      throw ProcessException(
        executable,
        const [],
        'Windows process creation failed.',
        memory.api.lastError(),
      );
    }
    thread = information.ref.thread;
    process = _WindowsOwnedProcess.created(
      information.ref.processId,
      information.ref.process,
    );
    final identity = await process.identity();
    if (!_validIdentity(identity) ||
        !_sameImage(identity.executablePath, executable)) {
      throw StateError(
        'The newly created executable identity could not be verified.',
      );
    }
    // One suspension is ours. Any different count is a failed launch admission.
    if (api.resume(thread) != 1) {
      throw StateError(
        'The verified process primary thread could not be resumed.',
      );
    }
    admitted = true;
    return LaunchProcessReceipt(pid: identity.pid, identity: identity);
  } finally {
    try {
      if (!admitted && process != null) {
        // Retire only the original created process object, even on identity failure.
        await process.stop();
      }
    } finally {
      if (thread != nullptr) memory.api.closeHandle(thread);
      process?.close();
      memory.close();
    }
  }
}

String _quoteWindowsArgument(String argument) {
  final result = StringBuffer('"');
  var slashes = 0;
  for (final unit in argument.codeUnits) {
    if (unit == 92) {
      slashes++;
      continue;
    }
    result.write('\\' * (unit == 34 ? slashes * 2 + 1 : slashes));
    result.writeCharCode(unit);
    slashes = 0;
  }
  // Backslashes immediately before the closing quote must also be doubled.
  result.write('\\' * (slashes * 2));
  result.write('"');
  return result.toString();
}

Pointer<Uint16> _windowsEnvironment(
  _WindowsCreationMemory memory,
  _WindowsCreationApi api,
  Map<String, String> overrides,
) {
  final entries =
      <
        ({
          String key,
          String value,
          Pointer<Uint16> name,
          bool override,
          int order,
        })
      >[];
  for (final source in [
    (values: Platform.environment, override: false),
    (values: overrides, override: true),
  ]) {
    for (final entry in source.values.entries) {
      entries.add((
        key: entry.key,
        value: entry.value,
        name: memory.text(entry.key),
        override: source.override,
        order: entries.length,
      ));
    }
  }
  int compare(Pointer<Uint16> a, Pointer<Uint16> b) {
    final result = api.compare(a, -1, b, -1, 1);
    if (result == 0) {
      throw StateError('The Windows environment could not be sorted.');
    }
    return result - 2;
  }

  entries.sort((a, b) {
    final order = compare(a.name, b.name);
    if (order != 0) return order;
    // Explicit overrides win over an inherited key with different casing.
    if (a.override != b.override) return a.override ? 1 : -1;
    return a.order.compareTo(b.order);
  });
  final values = <String>[];
  for (var index = 0; index < entries.length; index++) {
    final entry = entries[index];
    if (index + 1 < entries.length &&
        compare(entry.name, entries[index + 1].name) == 0) {
      continue;
    }
    values.add('${entry.key}=${entry.value}');
  }
  // text() adds the final null, including the second null of an empty block.
  return memory.text('${values.join('\u0000')}\u0000');
}

final class _WindowsCreationMemory {
  final api = _WindowsProcessApi();
  late final heap = api.processHeap();
  final allocations = <Pointer<Void>>[];
  Pointer<Void> allocate(int bytes) {
    final memory = api.allocate(heap, 8, bytes);
    if (memory == nullptr) {
      throw StateError('Process creation allocation failed.');
    }
    allocations.add(memory);
    return memory;
  }

  Pointer<Uint16> text(String value) {
    final memory = allocate((value.length + 1) * 2).cast<Uint16>();
    memory.asTypedList(value.length).setAll(0, value.codeUnits);
    return memory;
  }

  void close() {
    for (final memory in allocations.reversed) {
      api.free(heap, 0, memory);
    }
    allocations.clear();
  }
}
