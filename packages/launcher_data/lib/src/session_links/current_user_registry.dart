import 'dart:ffi';

/// String values in the current user's registry hive.
///
/// Every path is relative to `HKEY_CURRENT_USER`. No implementation opens
/// another hive, so nothing built on this can reach `HKEY_LOCAL_MACHINE`.
abstract interface class CurrentUserRegistry {
  /// The string value [name] of [keyPath], or the key's default value when
  /// [name] is null. Null when the key or value is missing or is not a
  /// string.
  String? readString(String keyPath, {String? name});

  /// Creates [keyPath] when it is missing and sets its string value [name],
  /// or its default value when [name] is null, to [value].
  void writeString(String keyPath, String value, {String? name});

  /// Deletes [keyPath] with everything under it. A missing key is not an
  /// error.
  void deleteTree(String keyPath);
}

/// A registry call that Windows refused.
final class CurrentUserRegistryException implements Exception {
  const CurrentUserRegistryException(this.operation, this.status);

  final String operation;

  /// The Win32 error code.
  final int status;

  @override
  String toString() =>
      'The registry refused to $operation (Windows error $status).';
}

/// [CurrentUserRegistry] over the Win32 registry functions. Windows only.
final class WindowsCurrentUserRegistry implements CurrentUserRegistry {
  WindowsCurrentUserRegistry();

  static const _success = 0;
  static const _fileNotFound = 2;
  static const _moreData = 234;
  static const _unsupportedType = 1630;
  static const _restrictToString = 0x00000002; // RRF_RT_REG_SZ
  static const _queryAndSetValue =
      0x0001 | 0x0002; // KEY_QUERY_VALUE | KEY_SET_VALUE
  static const _stringType = 1; // REG_SZ
  static const _maximumValueBytes = 64 * 1024;

  /// `HKEY_CURRENT_USER`: `(HKEY)(ULONG_PTR)(LONG)0x80000001`, sign-extended
  /// as the Windows SDK defines it.
  static final _currentUser = Pointer<Void>.fromAddress(-0x7FFFFFFF);

  late final _api = _RegistryApi();

  @override
  String? readString(String keyPath, {String? name}) {
    _requireKeyPath(keyPath);
    final memory = _NativeMemory(_api);
    try {
      final subKey = memory.wide(keyPath);
      final valueName = name == null ? nullptr : memory.wide(name);
      final size = memory.allocate<Uint32>(4);
      for (var attempt = 0; attempt < 3; attempt++) {
        size.value = 0;
        var status = _api.getValue(
          _currentUser,
          subKey,
          valueName,
          _restrictToString,
          nullptr,
          nullptr,
          size,
        );
        if (status == _fileNotFound || status == _unsupportedType) return null;
        if (status != _success) {
          throw CurrentUserRegistryException('read $keyPath', status);
        }
        final capacity = size.value + 2;
        if (capacity > _maximumValueBytes) {
          throw CurrentUserRegistryException('read $keyPath', _moreData);
        }
        final data = memory.allocate<Uint16>(capacity);
        size.value = capacity;
        status = _api.getValue(
          _currentUser,
          subKey,
          valueName,
          _restrictToString,
          nullptr,
          data.cast(),
          size,
        );
        if (status == _moreData) continue; // It grew between the two calls.
        if (status == _fileNotFound || status == _unsupportedType) return null;
        if (status != _success) {
          throw CurrentUserRegistryException('read $keyPath', status);
        }
        var units = size.value ~/ 2;
        while (units > 0 && data[units - 1] == 0) {
          units--;
        }
        return String.fromCharCodes(data.asTypedList(units));
      }
      throw CurrentUserRegistryException('read $keyPath', _moreData);
    } finally {
      memory.free();
    }
  }

  @override
  void writeString(String keyPath, String value, {String? name}) {
    _requireKeyPath(keyPath);
    final memory = _NativeMemory(_api);
    try {
      final handle = memory.allocate<Pointer<Void>>(sizeOf<IntPtr>());
      var status = _api.createKey(
        _currentUser,
        memory.wide(keyPath),
        0,
        nullptr,
        0,
        _queryAndSetValue,
        nullptr,
        handle,
        nullptr,
      );
      if (status != _success) {
        throw CurrentUserRegistryException('create $keyPath', status);
      }
      final key = handle.value;
      try {
        final data = memory.wide(value);
        status = _api.setValue(
          key,
          name == null ? nullptr : memory.wide(name),
          0,
          _stringType,
          data.cast(),
          (value.length + 1) * 2,
        );
        if (status != _success) {
          throw CurrentUserRegistryException('write $keyPath', status);
        }
      } finally {
        _api.closeKey(key);
      }
    } finally {
      memory.free();
    }
  }

  @override
  void deleteTree(String keyPath) {
    _requireKeyPath(keyPath);
    final memory = _NativeMemory(_api);
    try {
      final status = _api.deleteTree(_currentUser, memory.wide(keyPath));
      if (status != _success && status != _fileNotFound) {
        throw CurrentUserRegistryException('delete $keyPath', status);
      }
    } finally {
      memory.free();
    }
  }

  /// An empty path would name the whole hive, so every call needs a real
  /// key below it.
  static void _requireKeyPath(String keyPath) {
    if (keyPath.isEmpty ||
        keyPath.split(r'\').any((segment) => segment.isEmpty)) {
      throw ArgumentError.value(keyPath, 'keyPath', 'Not a registry key path.');
    }
  }
}

final class _RegistryApi {
  static final _advapi = DynamicLibrary.open('advapi32.dll');
  static final _kernel = DynamicLibrary.open('kernel32.dll');

  final createKey = _advapi
      .lookupFunction<
        Int32 Function(
          Pointer<Void>,
          Pointer<Uint16>,
          Uint32,
          Pointer<Uint16>,
          Uint32,
          Uint32,
          Pointer<Void>,
          Pointer<Pointer<Void>>,
          Pointer<Uint32>,
        ),
        int Function(
          Pointer<Void>,
          Pointer<Uint16>,
          int,
          Pointer<Uint16>,
          int,
          int,
          Pointer<Void>,
          Pointer<Pointer<Void>>,
          Pointer<Uint32>,
        )
      >('RegCreateKeyExW');
  final setValue = _advapi
      .lookupFunction<
        Int32 Function(
          Pointer<Void>,
          Pointer<Uint16>,
          Uint32,
          Uint32,
          Pointer<Uint8>,
          Uint32,
        ),
        int Function(
          Pointer<Void>,
          Pointer<Uint16>,
          int,
          int,
          Pointer<Uint8>,
          int,
        )
      >('RegSetValueExW');
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
  final deleteTree = _advapi
      .lookupFunction<
        Int32 Function(Pointer<Void>, Pointer<Uint16>),
        int Function(Pointer<Void>, Pointer<Uint16>)
      >('RegDeleteTreeW');
  final closeKey = _advapi
      .lookupFunction<
        Int32 Function(Pointer<Void>),
        int Function(Pointer<Void>)
      >('RegCloseKey');
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

/// Zeroed process-heap blocks for one registry call, freed together.
final class _NativeMemory {
  _NativeMemory(this._api) : _heap = _api.processHeap();

  static const _zeroMemory = 0x00000008; // HEAP_ZERO_MEMORY

  final _RegistryApi _api;
  final Pointer<Void> _heap;
  final _blocks = <Pointer<Void>>[];

  Pointer<T> allocate<T extends NativeType>(int bytes) {
    final block = _api.heapAlloc(_heap, _zeroMemory, bytes);
    if (block == nullptr) throw StateError('The process heap is exhausted.');
    _blocks.add(block);
    return block.cast<T>();
  }

  /// [text] as a terminated UTF-16 string. A NUL inside would cut it short,
  /// so it is refused.
  Pointer<Uint16> wide(String text) {
    if (text.contains('\u0000')) {
      throw ArgumentError.value(text, 'text', 'Contains a NUL character.');
    }
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
