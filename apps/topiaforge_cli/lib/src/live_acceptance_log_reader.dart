import 'dart:convert';
import 'dart:io';

final class AcceptanceIncrementalLogReader {
  AcceptanceIncrementalLogReader(this.file)
    : _offset = file.existsSync() ? file.lengthSync() : 0;

  final File file;
  int _offset;
  String _pending = '';

  Future<List<String>> readNewLines() async {
    try {
      if (!file.existsSync()) return const [];
      final length = file.lengthSync();
      if (_offset > length) {
        _offset = 0;
        _pending = '';
      }
      if (_offset == length) return const [];
      final handle = await file.open();
      List<int> bytes;
      try {
        await handle.setPosition(_offset);
        bytes = await handle.read(length - _offset);
        _offset = await handle.position();
      } finally {
        await handle.close();
      }
      final combined = _pending + utf8.decode(bytes, allowMalformed: true);
      final lines = combined.split('\n');
      _pending = lines.removeLast();
      return lines
          .map(
            (line) =>
                line.endsWith('\r') ? line.substring(0, line.length - 1) : line,
          )
          .toList();
    } on FileSystemException {
      return const [];
    }
  }
}
