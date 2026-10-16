part of 'launch_process_control.dart';

/// Uses the primary process token, matching CreateProcessW even if a caller
/// thread is impersonating. No profile is loaded, created or changed.
WindowsAcceptanceIdentity readWindowsAcceptanceIdentity() {
  if (!Platform.isWindows) {
    throw UnsupportedError(
      'Isolated acceptance requires a verified Windows QA session.',
    );
  }
  final api = _WindowsIsolationApi();
  return _readWindowsAcceptanceIdentity(api.currentProcess());
}

WindowsAcceptanceIdentity _readWindowsAcceptanceIdentity(
  Pointer<Void> process,
) {
  final api = _WindowsIsolationApi();
  final memory = _WindowsCreationMemory();
  Pointer<Void> token = nullptr;
  try {
    final tokenOut = memory.allocate(sizeOf<IntPtr>()).cast<Pointer<Void>>();
    // SHGetKnownFolderPath requires QUERY + IMPERSONATE for an explicit token.
    if (api.openToken(process, 0x0008 | 0x0004, tokenOut) == 0) {
      throw StateError('Acceptance cannot verify the primary process token.');
    }
    token = tokenOut.value;
    Pointer<Void> info(int kind, int minimum) {
      final size = memory.allocate(4).cast<Uint32>();
      api.tokenInfo(token, kind, nullptr, 0, size);
      if (size.value < minimum || size.value > 65536) {
        throw StateError('Acceptance token information has an invalid bound.');
      }
      final capacity = size.value;
      final data = memory.allocate(capacity);
      if (api.tokenInfo(token, kind, data, capacity, size) == 0 ||
          size.value < minimum ||
          size.value > capacity) {
        throw StateError('Acceptance token information could not be verified.');
      }
      return data;
    }

    final user = info(1, sizeOf<IntPtr>()).cast<Pointer<Void>>();
    final sidOut = memory.allocate(sizeOf<IntPtr>()).cast<Pointer<Uint16>>();
    late final String sid;
    try {
      if (api.sidText(user.value, sidOut) == 0) {
        throw StateError('Acceptance user identity could not be verified.');
      }
      sid = _readIsolationText(sidOut.value, 256);
    } finally {
      if (sidOut.value != nullptr) api.localFree(sidOut.value.cast());
    }
    final statistics = info(10, 56).cast<Uint32>();
    final logon =
        '${statistics[3].toRadixString(16).padLeft(8, '0')}'
        '${statistics[2].toRadixString(16).padLeft(8, '0')}';
    final session = info(12, 4).cast<Uint32>().value;
    String folder(int a, int b, int c, List<int> tail) {
      final guid = memory.allocate(16).cast<Uint8>();
      guid.cast<Uint32>().value = a;
      (guid + 4).cast<Uint16>().value = b;
      (guid + 6).cast<Uint16>().value = c;
      (guid + 8).asTypedList(8).setAll(0, tail);
      final path = memory.allocate(sizeOf<IntPtr>()).cast<Pointer<Uint16>>();
      try {
        // No KF_FLAG_CREATE: a probe must not materialize a profile folder.
        if (api.folder(guid.cast(), 0, token, path) != 0) {
          throw StateError('Acceptance OS known folder could not be verified.');
        }
        return p.normalize(_readIsolationText(path.value, 32767));
      } finally {
        if (path.value != nullptr) api.coTaskFree(path.value.cast());
      }
    }

    // Microsoft KNOWNFOLDERID: Profile and LocalAppDataLow.
    return WindowsAcceptanceIdentity(
      userSid: sid,
      logonId: logon,
      sessionId: session,
      userProfile: folder(0x5e6c858f, 0x0e22, 0x4760, [
        0x9a,
        0xfe,
        0xea,
        0x33,
        0x17,
        0xb6,
        0x71,
        0x73,
      ]),
      localAppDataLow: folder(0xa520a1a4, 0x1780, 0x4ff6, [
        0xbd,
        0x18,
        0x16,
        0x73,
        0x43,
        0xc5,
        0xaf,
        0x16,
      ]),
    );
  } finally {
    if (token != nullptr) memory.api.closeHandle(token);
    memory.close();
  }
}

String _readIsolationText(Pointer<Uint16> value, int maximum) {
  if (value == nullptr) throw StateError('Acceptance native text is absent.');
  for (var length = 0; length <= maximum; length++) {
    if (value[length] == 0) {
      if (length == 0) throw StateError('Acceptance native text is empty.');
      return String.fromCharCodes(value.asTypedList(length));
    }
  }
  throw StateError('Acceptance native text exceeds its limit.');
}

final class _WindowsIsolationApi {
  static final kernel = DynamicLibrary.open('kernel32.dll');
  static final security = DynamicLibrary.open('advapi32.dll');
  static final shell = DynamicLibrary.open('shell32.dll');
  static final ole = DynamicLibrary.open('ole32.dll');
  final currentProcess = kernel
      .lookupFunction<Pointer<Void> Function(), Pointer<Void> Function()>(
        'GetCurrentProcess',
      );
  final openToken = security
      .lookupFunction<
        Int32 Function(Pointer<Void>, Uint32, Pointer<Pointer<Void>>),
        int Function(Pointer<Void>, int, Pointer<Pointer<Void>>)
      >('OpenProcessToken');
  final tokenInfo = security
      .lookupFunction<
        Int32 Function(
          Pointer<Void>,
          Int32,
          Pointer<Void>,
          Uint32,
          Pointer<Uint32>,
        ),
        int Function(Pointer<Void>, int, Pointer<Void>, int, Pointer<Uint32>)
      >('GetTokenInformation');
  final sidText = security
      .lookupFunction<
        Int32 Function(Pointer<Void>, Pointer<Pointer<Uint16>>),
        int Function(Pointer<Void>, Pointer<Pointer<Uint16>>)
      >('ConvertSidToStringSidW');
  final localFree = kernel
      .lookupFunction<
        Pointer<Void> Function(Pointer<Void>),
        Pointer<Void> Function(Pointer<Void>)
      >('LocalFree');
  final folder = shell
      .lookupFunction<
        Int32 Function(
          Pointer<Void>,
          Uint32,
          Pointer<Void>,
          Pointer<Pointer<Uint16>>,
        ),
        int Function(
          Pointer<Void>,
          int,
          Pointer<Void>,
          Pointer<Pointer<Uint16>>,
        )
      >('SHGetKnownFolderPath');
  final coTaskFree = ole
      .lookupFunction<
        Void Function(Pointer<Void>),
        void Function(Pointer<Void>)
      >('CoTaskMemFree');
}

/// Reads the installed Windows directory from the OS, not SystemRoot overrides.
String readWindowsSystemRoot() {
  if (!Platform.isWindows) {
    throw UnsupportedError('Windows acceptance is required.');
  }
  final memory = _WindowsCreationMemory();
  try {
    final buffer = memory.allocate(32768 * 2).cast<Uint16>();
    final query = _WindowsIsolationApi.kernel
        .lookupFunction<
          Uint32 Function(Pointer<Uint16>, Uint32),
          int Function(Pointer<Uint16>, int)
        >('GetWindowsDirectoryW');
    final length = query(buffer, 32768);
    if (length == 0 || length >= 32768) {
      throw StateError('Windows system root is unavailable.');
    }
    return String.fromCharCodes(buffer.asTypedList(length));
  } finally {
    memory.close();
  }
}
