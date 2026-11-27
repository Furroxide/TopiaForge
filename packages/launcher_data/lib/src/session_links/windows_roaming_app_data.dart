import 'dart:ffi';

/// The current user's roaming application data folder, from the user's own
/// shell-folder setting, or null when Windows cannot say. Windows only.
///
/// A process can point `APPDATA` anywhere for itself, and the shell's known
/// folder follows a redirected `USERPROFILE` as well. The `topiaforge://`
/// registration belongs to the user, not to this process, so this reads the
/// stored setting (`User Shell Folders`, value `AppData`) and expands it for
/// the user's token: from the user's profile, never from this process's
/// variables.
String? windowsRoamingAppDataFolder() {
  final api = _Api();
  final memory = _Memory(api);
  try {
    final stored = _storedSetting(api, memory);
    if (stored == null) return null;
    final folder = _expandForUser(api, memory, stored);
    if (folder == null || !_absolute.hasMatch(folder)) return null;
    return folder;
  } finally {
    memory.free();
  }
}

const _userShellFolders =
    r'Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders';

/// What Windows uses when the setting is missing.
const _windowsDefault = r'%USERPROFILE%\AppData\Roaming';

const _success = 0;
const _fileNotFound = 2;
const _moreData = 234;
const _maximumSettingBytes = 64 * 1024;

/// A drive or UNC path.
final _absolute = RegExp(r'^(?:[A-Za-z]:\\|\\\\[^\\])');

/// `HKEY_CURRENT_USER`, sign-extended as the Windows SDK defines it.
final _currentUser = Pointer<Void>.fromAddress(-0x7FFFFFFF);

/// The stored `AppData` setting, unexpanded; the Windows default when there
/// is none; null when it cannot be read.
String? _storedSetting(_Api api, _Memory memory) {
  // RRF_RT_REG_SZ | RRF_RT_REG_EXPAND_SZ | RRF_NOEXPAND
  const flags = 0x00000002 | 0x00000004 | 0x10000000;
  final subKey = memory.wide(_userShellFolders);
  final name = memory.wide('AppData');
  final size = memory.allocate<Uint32>(4);
  for (var attempt = 0; attempt < 3; attempt++) {
    size.value = 0;
    var status = api.getValue(
      _currentUser,
      subKey,
      name,
      flags,
      nullptr,
      nullptr,
      size,
    );
    if (status == _fileNotFound) return _windowsDefault;
    if (status != _success) return null;
    final capacity = size.value + 2;
    if (capacity > _maximumSettingBytes) return null;
    final data = memory.allocate<Uint16>(capacity);
    size.value = capacity;
    status = api.getValue(
      _currentUser,
      subKey,
      name,
      flags,
      nullptr,
      data.cast(),
      size,
    );
    if (status == _moreData) continue;
    if (status == _fileNotFound) return _windowsDefault;
    if (status != _success) return null;
    return _text(data, size.value ~/ 2);
  }
  return null;
}

/// [stored] with its variables expanded for this process's user, from the
/// user's profile rather than this process's variables.
String? _expandForUser(_Api api, _Memory memory, String stored) {
  // TOKEN_QUERY | TOKEN_IMPERSONATE | TOKEN_DUPLICATE
  const access = 0x0008 | 0x0004 | 0x0002;
  final token = memory.allocate<Pointer<Void>>(sizeOf<IntPtr>());
  if (api.openProcessToken(api.currentProcess(), access, token) == 0) {
    return null;
  }
  try {
    const capacity = 32 * 1024;
    final expanded = memory.allocate<Uint16>(capacity * 2);
    if (api.expandForUser(
          token.value,
          memory.wide(stored),
          expanded,
          capacity,
        ) ==
        0) {
      return null;
    }
    return _text(expanded, capacity);
  } finally {
    api.closeHandle(token.value);
  }
}

/// The UTF-16 text at [units] up to its terminator, within [capacity] units.
String? _text(Pointer<Uint16> units, int capacity) {
  final text = units.asTypedList(capacity);
  final end = text.indexOf(0);
  if (end < 0) return null;
  return String.fromCharCodes(text, 0, end);
}

final class _Api {
  static final _advapi = DynamicLibrary.open('advapi32.dll');
  static final _kernel = DynamicLibrary.open('kernel32.dll');
  static final _userenv = DynamicLibrary.open('userenv.dll');

  final getValue = _advapi
      .lookupFunction<
        Int32 Function(
          Pointer<Void>,
          Pointer<Uint16>,
          Pointer<Uint16>,
          Uint32,
          Pointer<Uint32>,
          Pointer<Void>,
          Pointer<Uint32>,
        ),
        int Function(
          Pointer<Void>,
          Pointer<Uint16>,
          Pointer<Uint16>,
          int,
          Pointer<Uint32>,
          Pointer<Void>,
          Pointer<Uint32>,
        )
      >('RegGetValueW');
  final openProcessToken = _advapi
      .lookupFunction<
        Int32 Function(Pointer<Void>, Uint32, Pointer<Pointer<Void>>),
        int Function(Pointer<Void>, int, Pointer<Pointer<Void>>)
      >('OpenProcessToken');
  final currentProcess = _kernel
      .lookupFunction<Pointer<Void> Function(), Pointer<Void> Function()>(
        'GetCurrentProcess',
      );
  final closeHandle = _kernel
      .lookupFunction<
        Int32 Function(Pointer<Void>),
        int Function(Pointer<Void>)
      >('CloseHandle');
  final expandForUser = _userenv
      .lookupFunction<
        Int32 Function(Pointer<Void>, Pointer<Uint16>, Pointer<Uint16>, Uint32),
        int Function(Pointer<Void>, Pointer<Uint16>, Pointer<Uint16>, int)
      >('ExpandEnvironmentStringsForUserW');
  final processHeap = _kernel
      .lookupFunction<Pointer<Void> Function(), Pointer<Void> Function()>(
        'GetProcessHeap',
      );
  final heapAlloc = _kernel
      .lookupFunction<
        Pointer<Void> Function(Pointer<Void>, Uint32, UintPtr),
        Pointer<Void> Function(Pointer<Void>, int, int)
      >('HeapAlloc');
  final heapFree = _kernel
      .lookupFunction<
        Int32 Function(Pointer<Void>, Uint32, Pointer<Void>),
        int Function(Pointer<Void>, int, Pointer<Void>)
      >('HeapFree');
}

/// Zeroed process-heap blocks for one lookup, freed together.
final class _Memory {
  _Memory(this._api) : _heap = _api.processHeap();

  static const _zeroMemory = 0x00000008; // HEAP_ZERO_MEMORY

  final _Api _api;
  final Pointer<Void> _heap;
  final _blocks = <Pointer<Void>>[];

  Pointer<T> allocate<T extends NativeType>(int bytes) {
    final block = _api.heapAlloc(_heap, _zeroMemory, bytes);
    if (block == nullptr) throw StateError('The process heap is exhausted.');
    _blocks.add(block);
    return block.cast<T>();
  }

  /// [text] as a terminated UTF-16 string.
  Pointer<Uint16> wide(String text) {
    final units = text.codeUnits;
    final block = allocate<Uint16>((units.length + 1) * 2);
    for (var index = 0; index < units.length; index++) {
      block[index] = units[index];
    }
    return block;
  }

  void free() {
    for (final block in _blocks) {
      _api.heapFree(_heap, 0, block);
    }
    _blocks.clear();
  }
}
