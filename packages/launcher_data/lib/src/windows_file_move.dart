import 'dart:ffi';

import 'package:path/path.dart' as p;

/// Moves the file at [from] to [to] unless something is already at [to], in
/// one step, and says whether it moved. Windows only.
///
/// `MoveFileExW` without `MOVEFILE_REPLACE_EXISTING` refuses to replace what
/// is at [to] as part of the move itself, so nothing that lands there between
/// a caller's last look and this move is ever replaced. `File.rename` always
/// replaces. When the file did not move, the caller looks at both names to
/// tell why: [to] is there, [from] is gone, or [from] is held.
bool moveFileWithoutReplacingOnWindows(String from, String to) {
  final api = _MoveApi.instance;
  final source = api.wide(_extendedLength(from));
  try {
    final target = api.wide(_extendedLength(to));
    try {
      return api.move(source, target, _writeThrough) != 0;
    } finally {
      api.free(target);
    }
  } finally {
    api.free(source);
  }
}

/// MOVEFILE_WRITE_THROUGH: the move is on the disk before the call returns.
const _writeThrough = 0x00000008;

const _extendedPrefix = r'\\?\';
const _extendedNetworkPrefix = r'\\?\UNC\';

/// [path] as an extended-length path, so a long one moves as `File.rename`
/// would move it.
String _extendedLength(String path) {
  if (path.startsWith(_extendedPrefix)) return path;
  final full = p.normalize(p.absolute(path));
  return full.startsWith(r'\\')
      ? '$_extendedNetworkPrefix${full.substring(2)}'
      : '$_extendedPrefix$full';
}

final class _MoveApi {
  _MoveApi._();

  static final instance = _MoveApi._();
  static final _kernel = DynamicLibrary.open('kernel32.dll');

  /// HEAP_ZERO_MEMORY, which also leaves each string's terminator in place.
  static const _zeroMemory = 0x00000008;

  final move = _kernel
      .lookupFunction<
        Int32 Function(Pointer<Uint16>, Pointer<Uint16>, Uint32),
        int Function(Pointer<Uint16>, Pointer<Uint16>, int)
      >('MoveFileExW');
  final _processHeap = _kernel
      .lookupFunction<Pointer<Void> Function(), Pointer<Void> Function()>(
        'GetProcessHeap',
      );
  final _heapAlloc = _kernel
      .lookupFunction<
        Pointer<Void> Function(Pointer<Void>, Uint32, UintPtr),
        Pointer<Void> Function(Pointer<Void>, int, int)
      >('HeapAlloc');
  final _heapFree = _kernel
      .lookupFunction<
        Int32 Function(Pointer<Void>, Uint32, Pointer<Void>),
        int Function(Pointer<Void>, int, Pointer<Void>)
      >('HeapFree');

  /// [text] as a terminated UTF-16 string on the process heap.
  Pointer<Uint16> wide(String text) {
    final units = text.codeUnits;
    final block = _heapAlloc(
      _processHeap(),
      _zeroMemory,
      (units.length + 1) * 2,
    );
    if (block == nullptr) throw StateError('The process heap is exhausted.');
    final wide = block.cast<Uint16>();
    wide.asTypedList(units.length).setAll(0, units);
    return wide;
  }

  void free(Pointer<Uint16> block) {
    _heapFree(_processHeap(), 0, block.cast());
  }
}
