import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:archive/archive.dart';
import 'package:path/path.dart' as p;
import 'package:unorm_dart/unorm_dart.dart' as unicode;

part 'safe_zip_validation.dart';

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
  SafeZipArchive._(
    Archive archive,
    this.policy,
    this.label,
    this.allowContainedLinks,
    Set<String> linkNames,
  ) : entries = List.unmodifiable(
        archive.files.map(
          (entry) =>
              SafeZipEntry._(entry, label, linkNames.contains(entry.name)),
        ),
      );

  factory SafeZipArchive.decode(
    List<int> bytes, {
    SafeArchivePolicy policy = SafeArchivePolicy.topiaForgePackage,
    String label = 'Archive',
    bool allowContainedLinks = false,
  }) {
    if (bytes.length > policy.maxArchiveBytes) {
      throw StateError(
        '$label is larger than the ${_byteSizeLabel(policy.maxArchiveBytes)} limit.',
      );
    }
    try {
      final directory = ZipDirectory()..read(InputMemoryStream(bytes));
      final linkNames = _preflight(
        directory,
        bytes.length,
        policy,
        label,
        allowContainedLinks,
      );
      final decoded = ZipDecoder().decodeBytes(bytes);
      _validateEntries(decoded, policy, label, allowContainedLinks, linkNames);
      return SafeZipArchive._(
        decoded,
        policy,
        label,
        allowContainedLinks,
        linkNames,
      );
    } on StateError {
      rethrow;
    } on Object catch (error) {
      throw StateError('$label is not a readable ZIP archive: $error');
    }
  }

  final SafeArchivePolicy policy;
  final String label;
  final bool allowContainedLinks;
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
  void extractTo(Directory target, {bool preserveExecutableMode = false}) {
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
      for (final entry in entries.where((entry) => !entry.isSymbolicLink)) {
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
        if (preserveExecutableMode &&
            !Platform.isWindows &&
            entry.isExecutable) {
          final chmod = Process.runSync('/bin/chmod', ['755', output.path]);
          if (chmod.exitCode != 0) {
            throw StateError(
              '$label could not apply sanitized executable permissions: '
              '${entry.name}',
            );
          }
        }
      }
      for (final entry in entries.where((entry) => entry.isSymbolicLink)) {
        final outputPath = p.joinAll([
          target.path,
          ...p.posix.split(entry.name),
        ]);
        _requireSafeParents(target, File(outputPath).parent, label);
        if (FileSystemEntity.typeSync(outputPath, followLinks: false) !=
            FileSystemEntityType.notFound) {
          throw StateError('$label path collides on disk: ${entry.name}');
        }
        Link(outputPath)
          ..parent.createSync(recursive: true)
          ..createSync(entry.symbolicLinkTarget);
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
  SafeZipEntry._(this._entry, this.label, this._isSymbolicLink)
    : name = portableArchivePath(_entry.name, label: label);

  final ArchiveFile _entry;
  final String label;
  final bool _isSymbolicLink;
  final String name;

  bool get isFile => _entry.isFile;
  bool get isSymbolicLink => _isSymbolicLink;
  int get size => _entry.size;
  bool get isExecutable => (_entry.mode & 0x49) != 0;
  String get symbolicLinkTarget {
    final target =
        _entry.symbolicLink ??
        utf8.decode(_entry.readBytes() ?? const [], allowMalformed: false);
    if (!isSymbolicLink || target.isEmpty) {
      throw StateError('$label entry is not a symbolic link: $name');
    }
    return target;
  }

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
