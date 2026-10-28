import 'dart:convert';
import 'dart:io';

final class AcceptanceIncrementalLogReader {
  AcceptanceIncrementalLogReader(this.file)
    : _offset = file.existsSync() ? file.lengthSync() : 0;

  final File file;
  int _offset;
  List<int> _pending = const [];
  static const _chunkBytes = 64 * 1024;
  static const _lineBytes = 8 * 1024;

  Future<List<String>> readNewLines() async {
    try {
      if (!file.existsSync()) return const [];
      final length = file.lengthSync();
      if (_offset > length) {
        _offset = 0;
        _pending = const [];
      }
      if (_offset == length) return const [];
      final handle = await file.open();
      List<int> bytes;
      try {
        await handle.setPosition(_offset);
        final remaining = length - _offset;
        bytes = await handle.read(
          remaining < _chunkBytes ? remaining : _chunkBytes,
        );
        _offset = await handle.position();
      } finally {
        await handle.close();
      }
      // Keep incomplete UTF-8 bytes until the newline, including across polls.
      final combined = [..._pending, ...bytes];
      final lines = <String>[];
      var start = 0;
      for (var index = 0; index < combined.length; index++) {
        if (index - start > _lineBytes) {
          throw const FormatException(
            'Acceptance log line exceeds its byte limit.',
          );
        }
        if (combined[index] != 10) continue;
        final end = index > start && combined[index - 1] == 13
            ? index - 1
            : index;
        lines.add(
          utf8.decode(combined.sublist(start, end), allowMalformed: true),
        );
        start = index + 1;
      }
      if (combined.length - start > _lineBytes) {
        throw const FormatException(
          'Acceptance log line exceeds its byte limit.',
        );
      }
      _pending = combined.sublist(start);
      return lines;
    } on FileSystemException {
      return const [];
    }
  }
}
