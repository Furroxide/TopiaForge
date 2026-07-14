import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

class CliFileLimits {
  static const session = 64 * 1024;
  static const manifest = 1024 * 1024;
  static const changelog = 1024 * 1024;
  static const registryEntry = 4 * 1024 * 1024;
  static const metadata = 4 * 1024 * 1024;
  static const uiBundle = 64 * 1024 * 1024;
  static const package = 512 * 1024 * 1024;
  static const maxDirectoryEntries = 10000;
}

Uint8List readBoundedRegularFileSync(
  File file, {
  required int maxBytes,
  bool allowEmpty = false,
  void Function()? afterFirstReadForTesting,
}) {
  if (maxBytes <= 0) throw ArgumentError.value(maxBytes, 'maxBytes');
  final path = file.absolute.path;
  _requireRegularFile(path);
  final resolvedBefore = File(path).resolveSymbolicLinksSync();
  final before = File(path).statSync();
  _requireSize(path, before.size, maxBytes, allowEmpty);

  final bytes = Uint8List(before.size);
  final first = File(path).openSync(mode: FileMode.read);
  try {
    var offset = 0;
    while (offset < bytes.length) {
      final read = first.readIntoSync(bytes, offset, bytes.length);
      if (read <= 0) break;
      offset += read;
    }
    if (offset != bytes.length || first.readByteSync() != -1) {
      throw StateError('File size changed while reading: $path');
    }
  } finally {
    first.closeSync();
  }

  afterFirstReadForTesting?.call();
  _requireUnchanged(path, before, resolvedBefore, maxBytes, allowEmpty);

  // Compare a second bounded pass without allocating a second file-sized
  // buffer. This catches same-size replacements and content races that a
  // before/after stat alone cannot observe.
  final second = File(path).openSync(mode: FileMode.read);
  final chunk = Uint8List(64 * 1024);
  try {
    var offset = 0;
    while (offset < bytes.length) {
      final wanted = bytes.length - offset < chunk.length
          ? bytes.length - offset
          : chunk.length;
      final read = second.readIntoSync(chunk, 0, wanted);
      if (read <= 0) break;
      for (var index = 0; index < read; index += 1) {
        if (chunk[index] != bytes[offset + index]) {
          throw StateError('File content changed while reading: $path');
        }
      }
      offset += read;
    }
    if (offset != bytes.length || second.readByteSync() != -1) {
      throw StateError('File size changed while reading: $path');
    }
  } finally {
    second.closeSync();
  }
  _requireUnchanged(path, before, resolvedBefore, maxBytes, allowEmpty);
  return bytes;
}

String readBoundedTextFileSync(
  File file, {
  required int maxBytes,
  bool allowEmpty = false,
  void Function()? afterFirstReadForTesting,
}) {
  final bytes = readBoundedRegularFileSync(
    file,
    maxBytes: maxBytes,
    allowEmpty: allowEmpty,
    afterFirstReadForTesting: afterFirstReadForTesting,
  );
  try {
    return utf8.decode(bytes, allowMalformed: false);
  } on FormatException {
    throw StateError('File is not valid UTF-8: ${file.absolute.path}');
  }
}

String readBoundedTextFileTailSync(File file, {required int maxBytes}) {
  if (maxBytes <= 0) throw ArgumentError.value(maxBytes, 'maxBytes');
  final path = file.absolute.path;
  _requireRegularFile(path);
  final resolvedBefore = File(path).resolveSymbolicLinksSync();
  final before = File(path).statSync();
  if (before.size < 0) {
    throw StateError('File has an invalid size: $path');
  }
  final length = before.size < maxBytes ? before.size : maxBytes;
  final start = before.size - length;
  final bytes = Uint8List(length);
  final input = File(resolvedBefore).openSync(mode: FileMode.read);
  try {
    input.setPositionSync(start);
    var offset = 0;
    while (offset < bytes.length) {
      final read = input.readIntoSync(bytes, offset, bytes.length);
      if (read <= 0) break;
      offset += read;
    }
    if (offset != bytes.length || input.readByteSync() != -1) {
      throw StateError('File size changed while reading tail: $path');
    }
  } finally {
    input.closeSync();
  }
  _requireUnchanged(path, before, resolvedBefore, before.size, true);

  var firstCompleteCharacter = 0;
  if (start > 0) {
    while (firstCompleteCharacter < bytes.length &&
        bytes[firstCompleteCharacter] & 0xc0 == 0x80) {
      firstCompleteCharacter += 1;
    }
  }
  try {
    return utf8.decode(
      Uint8List.sublistView(bytes, firstCompleteCharacter),
      allowMalformed: false,
    );
  } on FormatException {
    throw StateError('File tail is not valid UTF-8: $path');
  }
}

Map<String, Object?> readBoundedJsonObjectSync(
  File file, {
  required int maxBytes,
  void Function()? afterFirstReadForTesting,
}) {
  final Object? decoded;
  try {
    decoded = jsonDecode(
      readBoundedTextFileSync(
        file,
        maxBytes: maxBytes,
        afterFirstReadForTesting: afterFirstReadForTesting,
      ),
    );
  } on FormatException catch (error) {
    throw StateError('File is not valid JSON: ${file.absolute.path} ($error)');
  }
  if (decoded is! Map) {
    throw StateError('File must contain a JSON object: ${file.absolute.path}');
  }
  return decoded.map((key, value) => MapEntry(key.toString(), value));
}

List<FileSystemEntity> listBoundedDirectorySync(
  Directory directory, {
  int maxEntries = CliFileLimits.maxDirectoryEntries,
}) {
  if (FileSystemEntity.typeSync(directory.path, followLinks: false) !=
      FileSystemEntityType.directory) {
    throw StateError('Expected a real directory: ${directory.absolute.path}');
  }
  final entries = <FileSystemEntity>[];
  for (final entity in directory.listSync(followLinks: false)) {
    entries.add(entity);
    if (entries.length > maxEntries) {
      throw StateError(
        'Directory exceeds the $maxEntries entry limit: ${directory.absolute.path}',
      );
    }
  }
  return entries;
}

void _requireRegularFile(String path) {
  if (FileSystemEntity.typeSync(path, followLinks: false) !=
      FileSystemEntityType.file) {
    throw StateError('Expected a regular file, not a link: $path');
  }
}

void _requireSize(String path, int size, int maxBytes, bool allowEmpty) {
  if ((!allowEmpty && size == 0) || size < 0 || size > maxBytes) {
    throw StateError(
      'File must contain ${allowEmpty ? '0' : '1'} to $maxBytes bytes: $path',
    );
  }
}

void _requireUnchanged(
  String path,
  FileStat before,
  String resolvedBefore,
  int maxBytes,
  bool allowEmpty,
) {
  _requireRegularFile(path);
  final after = File(path).statSync();
  _requireSize(path, after.size, maxBytes, allowEmpty);
  final resolvedAfter = File(path).resolveSymbolicLinksSync();
  if (after.size != before.size ||
      after.modified != before.modified ||
      after.changed != before.changed ||
      resolvedAfter != resolvedBefore) {
    throw StateError('File changed while reading: $path');
  }
}
