import 'dart:io';
import 'dart:typed_data';

import 'package:archive/archive.dart';
import 'package:path/path.dart' as p;
import 'package:unorm_dart/unorm_dart.dart' as unicode;

/// Resource and portability limits applied before ZIP content is consumed.
class SafeArchivePolicy {
  const SafeArchivePolicy({
    required this.maxArchiveBytes,
    required this.maxEntries,
    required this.maxEntryBytes,
    required this.maxExpandedBytes,
    this.maxPathCharacters = 1024,
  });

  /// Shared policy for `.topiaforgemod` and VPM package readers.
  static const topiaForgePackage = SafeArchivePolicy(
    maxArchiveBytes: 512 * 1024 * 1024,
    maxEntries: 8192,
    maxEntryBytes: 1024 * 1024 * 1024,
    maxExpandedBytes: 2 * 1024 * 1024 * 1024,
  );

  final int maxArchiveBytes;
  final int maxEntries;
  final int maxEntryBytes;
  final int maxExpandedBytes;
  final int maxPathCharacters;
}

/// A validated ZIP whose entries can only be read through bounded methods.
class SafeZipArchive {
  SafeZipArchive._(Archive archive, this.policy, this.label)
    : entries = List.unmodifiable(
        archive.files.map((entry) => SafeZipEntry._(entry, label)),
      );

  factory SafeZipArchive.decode(
    List<int> bytes, {
    SafeArchivePolicy policy = SafeArchivePolicy.topiaForgePackage,
    String label = 'Archive',
  }) {
    if (bytes.length > policy.maxArchiveBytes) {
      throw StateError(
        '$label is larger than the ${_byteSizeLabel(policy.maxArchiveBytes)} limit.',
      );
    }
    try {
      final directory = ZipDirectory()..read(InputMemoryStream(bytes));
      _preflight(directory, bytes.length, policy, label);
      final decoded = ZipDecoder().decodeBytes(bytes);
      _validateEntries(decoded, policy, label);
      return SafeZipArchive._(decoded, policy, label);
    } on StateError {
      rethrow;
    } on Object catch (error) {
      throw StateError('$label is not a readable ZIP archive: $error');
    }
  }

  final SafeArchivePolicy policy;
  final String label;
  final List<SafeZipEntry> entries;

  SafeZipEntry? entryNamed(String name, {bool caseSensitive = true}) {
    final normalized = portableArchivePath(name, label: label);
    final wanted = caseSensitive
        ? normalized
        : portableArchiveCollisionKey(normalized, label: label);
    for (final entry in entries) {
      final candidate = caseSensitive
          ? entry.name
          : portableArchiveCollisionKey(entry.name, label: label);
      if (candidate == wanted) {
        return entry;
      }
    }
    return null;
  }

  /// Extracts into a new/empty directory without following archive links.
  void extractTo(Directory target) {
    final targetType = FileSystemEntity.typeSync(
      target.path,
      followLinks: false,
    );
    if (targetType == FileSystemEntityType.link) {
      throw StateError('$label target cannot be a symbolic link.');
    }
    if (targetType != FileSystemEntityType.notFound &&
        targetType != FileSystemEntityType.directory) {
      throw StateError('$label target must be a directory.');
    }
    target.createSync(recursive: true);
    try {
      for (final entry in entries) {
        final outputPath = p.joinAll([
          target.path,
          ...p.posix.split(entry.name),
        ]);
        _requireSafeParents(target, File(outputPath).parent, label);
        final outputType = FileSystemEntity.typeSync(
          outputPath,
          followLinks: false,
        );
        if (!entry.isFile) {
          if (outputType == FileSystemEntityType.notFound) {
            Directory(outputPath).createSync(recursive: true);
          } else if (outputType != FileSystemEntityType.directory) {
            throw StateError('$label path collides on disk: ${entry.name}');
          }
          continue;
        }
        if (outputType != FileSystemEntityType.notFound) {
          throw StateError('$label path collides on disk: ${entry.name}');
        }
        final output = File(outputPath);
        output.parent.createSync(recursive: true);
        entry._writeTo(output, maxBytes: policy.maxEntryBytes);
      }
    } on Object {
      if (target.existsSync()) {
        target.deleteSync(recursive: true);
      }
      rethrow;
    }
  }
}

/// An entry whose normalized name and declared size have already been checked.
class SafeZipEntry {
  SafeZipEntry._(this._entry, String label)
    : name = portableArchivePath(_entry.name, label: label);

  final ArchiveFile _entry;
  final String name;

  bool get isFile => _entry.isFile;
  int get size => _entry.size;

  List<int> readBytes({required int maxBytes, String label = 'Archive entry'}) {
    if (!isFile || size < 0 || size > maxBytes) {
      throw StateError('$label exceeds its ${_byteSizeLabel(maxBytes)} limit.');
    }
    final output = OutputMemoryStream(size: size.clamp(1, maxBytes));
    final bounded = _BoundedSafeArchiveOutput(
      output,
      maxBytes: maxBytes,
      entryName: name,
      label: label,
    );
    _entry.writeContent(bounded, freeMemory: false);
    if (bounded.length != size) {
      throw StateError(
        '$label expanded to ${bounded.length} bytes but declared $size: $name.',
      );
    }
    return output.getBytes();
  }

  void _writeTo(
    File output, {
    required int maxBytes,
    String label = 'Archive entry',
  }) {
    final outputType = FileSystemEntity.typeSync(
      output.path,
      followLinks: false,
    );
    if (!isFile || outputType != FileSystemEntityType.notFound) {
      throw StateError('$label output must be a new regular file: $name');
    }
    final stream = _BoundedSafeArchiveOutput(
      OutputFileStream(output.path),
      maxBytes: maxBytes,
      entryName: name,
      label: label,
    );
    try {
      _entry.writeContent(stream, freeMemory: false);
      stream.closeSync();
      if (stream.length != size) {
        throw StateError(
          '$label expanded to ${stream.length} bytes but declared $size: $name.',
        );
      }
    } on Object {
      stream.closeSync();
      if (output.existsSync()) {
        output.deleteSync();
      }
      rethrow;
    }
  }
}

/// Normalizes a ZIP name and rejects paths unsafe on supported platforms.
String portableArchivePath(String rawPath, {String label = 'Archive'}) {
  var portable = rawPath.replaceAll('\\', '/');
  while (portable.endsWith('/')) {
    portable = portable.substring(0, portable.length - 1);
  }
  final parts = portable.split('/');
  if (portable.isEmpty ||
      portable.length > 1024 ||
      portable.startsWith('/') ||
      RegExp(r'^[A-Za-z]:').hasMatch(portable) ||
      parts.any(_unsafeSegment)) {
    throw StateError(
      '$label contains an unsafe or non-portable path: $rawPath',
    );
  }
  final normalized = p.posix.normalize(portable);
  if (normalized == '.' || normalized.isEmpty) {
    throw StateError('$label contains an empty path.');
  }
  return normalized;
}

/// Produces a Unicode-normalized, practical full-case-folded path key.
///
/// This is intentionally stricter than host filesystem comparison so one
/// package cannot install two names that alias on a supported target.
String portableArchiveCollisionKey(String rawPath, {String label = 'Archive'}) {
  final normalized = portableArchivePath(rawPath, label: label);
  var folded = unicode
      .nfkc(normalized)
      .replaceAll('\u0130', 'i\u0307')
      .toLowerCase();
  folded = folded
      .replaceAll('\u00df', 'ss')
      .replaceAll('\u03c2', '\u03c3')
      .replaceAll('\u017f', 's')
      .replaceAll('\u0587', '\u0565\u0582');
  return unicode.nfkc(folded);
}

void _preflight(
  ZipDirectory directory,
  int archiveBytes,
  SafeArchivePolicy policy,
  String label,
) {
  if (directory.filePosition < 0 ||
      directory.numberOfThisDisk != 0 ||
      directory.diskWithTheStartOfTheCentralDirectory != 0 ||
      directory.totalCentralDirectoryEntriesOnThisDisk !=
          directory.totalCentralDirectoryEntries ||
      directory.fileHeaders.length != directory.totalCentralDirectoryEntries) {
    throw StateError('$label has an invalid or unsupported ZIP directory.');
  }
  if (directory.fileHeaders.length > policy.maxEntries) {
    throw StateError('$label contains more than ${policy.maxEntries} entries.');
  }
  var expanded = 0;
  for (final header in directory.fileHeaders) {
    final local = header.file;
    if (local == null || local.filename != header.filename) {
      throw StateError('$label has mismatched local and central ZIP headers.');
    }
    if (header.diskNumberStart != 0) {
      throw StateError('Multi-disk $label archives are not supported.');
    }
    if ((header.generalPurposeBitFlag & 1) != 0 || (local.flags & 1) != 0) {
      throw StateError('Encrypted $label entries are not supported.');
    }
    if (header.compressionMethod != 0 && header.compressionMethod != 8) {
      throw StateError(
        '$label uses unsupported ZIP compression method '
        '${header.compressionMethod}.',
      );
    }
    if (header.compressedSize < 0 || header.compressedSize > archiveBytes) {
      throw StateError('$label entry has an invalid compressed size.');
    }
    final size = header.uncompressedSize;
    if (size < 0 || size > policy.maxEntryBytes) {
      throw StateError(
        '$label entry exceeds the '
        '${_byteSizeLabel(policy.maxEntryBytes)} expanded-file limit: '
        '${header.filename}.',
      );
    }
    if (expanded > policy.maxExpandedBytes - size) {
      throw StateError(
        '$label exceeds the '
        '${_byteSizeLabel(policy.maxExpandedBytes)} expanded-size limit.',
      );
    }
    expanded += size;
    _requireRegularZipType(
      header.externalFileAttributes >> 16,
      header.filename,
      label,
    );
  }
}

void _validateEntries(Archive archive, SafeArchivePolicy policy, String label) {
  if (archive.files.length > policy.maxEntries) {
    throw StateError('$label contains too many entries.');
  }
  final paths = <String, bool>{};
  var expanded = 0;
  for (final entry in archive.files) {
    if (entry.isSymbolicLink) {
      throw StateError('$label contains a symbolic link: ${entry.name}');
    }
    _requireRegularZipType(entry.mode, entry.name, label);
    final normalized = portableArchivePath(entry.name, label: label);
    if (normalized.length > policy.maxPathCharacters) {
      throw StateError('$label path is too long: ${entry.name}');
    }
    if (entry.size < 0 || entry.size > policy.maxEntryBytes) {
      throw StateError('$label entry is too large: ${entry.name}');
    }
    if (expanded > policy.maxExpandedBytes - entry.size) {
      throw StateError('$label expanded-size limit was exceeded.');
    }
    expanded += entry.size;
    final key = portableArchiveCollisionKey(normalized, label: label);
    if (paths.containsKey(key)) {
      throw StateError('$label contains duplicate path: $normalized');
    }
    var parent = p.posix.dirname(key);
    while (parent != '.') {
      if (paths[parent] == true) {
        throw StateError('$label path collides with a file: $normalized');
      }
      parent = p.posix.dirname(parent);
    }
    if (entry.isFile && paths.keys.any((path) => path.startsWith('$key/'))) {
      throw StateError('$label path collides with a directory: $normalized');
    }
    paths[key] = entry.isFile;
  }
}

void _requireRegularZipType(int mode, String name, String label) {
  final type = mode & 0xf000;
  if (type == 0xa000) {
    throw StateError('$label contains a symbolic link: $name');
  }
  if (type != 0 && type != 0x4000 && type != 0x8000) {
    throw StateError('$label contains an unsupported file type: $name');
  }
  if ((mode & 0xe00) != 0) {
    throw StateError('$label contains setuid/setgid/sticky permissions: $name');
  }
}

bool _unsafeSegment(String segment) {
  if (segment.isEmpty ||
      segment == '.' ||
      segment == '..' ||
      segment.contains(':') ||
      segment.endsWith(' ') ||
      segment.endsWith('.') ||
      segment.codeUnits.any((unit) => unit < 0x20 || unit == 0x7f) ||
      segment.contains(
        RegExp(r'[\u200b-\u200f\u202a-\u202e\u2060-\u206f\ufeff\ufe00-\ufe0f]'),
      )) {
    return true;
  }
  return _windowsDeviceNames.contains(segment.split('.').first.toLowerCase());
}

void _requireSafeParents(Directory root, Directory parent, String label) {
  var current = parent;
  while (p.isWithin(root.path, current.path) && current.path != root.path) {
    final type = FileSystemEntity.typeSync(current.path, followLinks: false);
    if (type == FileSystemEntityType.link) {
      throw StateError('$label extraction path contains a symbolic link.');
    }
    if (type != FileSystemEntityType.notFound &&
        type != FileSystemEntityType.directory) {
      throw StateError('$label extraction path contains a non-directory.');
    }
    current = current.parent;
  }
}

String _byteSizeLabel(int bytes) {
  if (bytes % (1024 * 1024 * 1024) == 0) {
    return '${bytes ~/ (1024 * 1024 * 1024)} GB';
  }
  if (bytes % (1024 * 1024) == 0) {
    return '${bytes ~/ (1024 * 1024)} MB';
  }
  return '$bytes-byte';
}

const _windowsDeviceNames = {
  'con',
  'prn',
  'aux',
  'nul',
  'com1',
  'com2',
  'com3',
  'com4',
  'com5',
  'com6',
  'com7',
  'com8',
  'com9',
  'lpt1',
  'lpt2',
  'lpt3',
  'lpt4',
  'lpt5',
  'lpt6',
  'lpt7',
  'lpt8',
  'lpt9',
};

class _BoundedSafeArchiveOutput extends OutputStream {
  _BoundedSafeArchiveOutput(
    this._output, {
    required this.maxBytes,
    required this.entryName,
    required this.label,
  }) : super(byteOrder: _output.byteOrder);

  final OutputStream _output;
  final int maxBytes;
  final String entryName;
  final String label;

  @override
  int get length => _output.length;

  void _reserve(int count) {
    if (count < 0 || length > maxBytes - count) {
      throw StateError('$label exceeds its expanded limit: $entryName');
    }
  }

  @override
  void clear() => _output.clear();
  @override
  Future<void> close() => _output.close();
  @override
  void closeSync() => _output.closeSync();
  @override
  void flush() => _output.flush();
  @override
  bool get isOpen => _output.isOpen;
  @override
  Uint8List subset(int start, [int? end]) => _output.subset(start, end);
  @override
  void writeByte(int value) {
    _reserve(1);
    _output.writeByte(value);
  }

  @override
  void writeBytes(List<int> bytes, {int? length}) {
    final count = length ?? bytes.length;
    _reserve(count);
    _output.writeBytes(bytes, length: count);
  }

  @override
  void writeStream(InputStream stream) {
    _reserve(stream.length);
    _output.writeStream(stream);
  }
}
