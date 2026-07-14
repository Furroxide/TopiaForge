import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:path/path.dart' as p;

import 'bounded_file_reader.dart';
import 'release_archive_metadata.dart';

/// Enforces the package boundary before native archive tools can observe the
/// staged tree, and performs extraction without trusting tool-specific link or
/// traversal behavior.
class ReleaseArchivePolicy {
  const ReleaseArchivePolicy();

  void validateSourceLinks(
    Directory source, {
    required bool allowContainedLinks,
  }) {
    if (FileSystemEntity.typeSync(source.path, followLinks: false) !=
        FileSystemEntityType.directory) {
      throw StateError(
        'Release source must be a real directory: ${source.path}',
      );
    }
    for (final entity in source.listSync(recursive: true, followLinks: false)) {
      if (entity is! Link) {
        continue;
      }
      if (!allowContainedLinks) {
        throw StateError(
          'Release zips may not contain symlinks: '
          '${p.relative(entity.path, from: source.path)}',
        );
      }
      validateLink(entity, source);
    }
  }

  void validateLink(Link link, Directory sourceRoot) {
    final target = link.targetSync();
    if (target.isEmpty ||
        target.contains('\u0000') ||
        p.isAbsolute(target) ||
        RegExp(r'^[A-Za-z]:[/\\]').hasMatch(target)) {
      throw StateError(
        'Release link has an unsafe target: ${link.path} -> $target',
      );
    }
    final lexicalRoot = p.normalize(sourceRoot.absolute.path);
    final lexicalTarget = p.normalize(
      p.join(p.dirname(link.absolute.path), target),
    );
    if (lexicalTarget != lexicalRoot &&
        !p.isWithin(lexicalRoot, lexicalTarget)) {
      throw StateError(
        'Release link escapes the staged tree: ${link.path} -> $target',
      );
    }
    try {
      final physicalRoot = p.normalize(sourceRoot.resolveSymbolicLinksSync());
      final physicalTarget = p.normalize(link.resolveSymbolicLinksSync());
      if (physicalTarget != physicalRoot &&
          !p.isWithin(physicalRoot, physicalTarget)) {
        throw StateError(
          'Release link resolves outside the staged tree: ${link.path} -> $target',
        );
      }
    } on FileSystemException catch (error) {
      throw StateError(
        'Release link is dangling or unreadable: ${link.path} (${error.message})',
      );
    }
  }

  void writeDartZip(
    Directory source,
    File destination, {
    required bool allowContainedLinks,
  }) {
    final archive = Archive();
    final entities = source.listSync(recursive: true, followLinks: false)
      ..sort((left, right) => left.path.compareTo(right.path));
    if (entities.length > ReleaseZipMetadataPolicy.maxEntries) {
      throw StateError('Release source has too many archive entries.');
    }
    final names = <String>{};
    var totalBytes = 0;
    for (final entity in entities) {
      final relative = p.relative(entity.path, from: source.path);
      final name = p.posix.joinAll(p.split(relative));
      _rejectUnsafeArchiveName(name);
      if (!names.add(_archiveKey(_normalizedArchiveName(name)))) {
        throw StateError(
          'Release source contains a case-fold collision: $name',
        );
      }
      if (entity is Directory) {
        // Parent directories are recreated from file and link paths. Omitting
        // explicit zero-byte directory entries also avoids invalid deflate
        // streams produced for them by archive 4.0.9 and accepted only by its
        // own decoder (Info-ZIP correctly rejects those streams).
        continue;
      } else if (entity is Link) {
        if (!allowContainedLinks) {
          throw StateError('Release zips may not contain symlinks: $relative');
        }
        final targetBytes = utf8.encode(entity.targetSync());
        if (targetBytes.length >
            ReleaseZipMetadataPolicy.maxSymlinkTargetBytes) {
          throw StateError('Release symlink target is too large: $relative');
        }
        archive.addFile(
          ArchiveFile.string(name, entity.targetSync())..mode = 0xa1ff,
        );
      } else if (entity is File) {
        final size = entity.lengthSync();
        if (size > ReleaseZipMetadataPolicy.maxUncompressedEntryBytes) {
          throw StateError(
            'Release source file exceeds its size limit: $relative',
          );
        }
        totalBytes += size;
        if (totalBytes > ReleaseZipMetadataPolicy.maxTotalUncompressedBytes) {
          throw StateError('Release source exceeds the total size limit.');
        }
        archive.addFile(
          ArchiveFile.bytes(
            name,
            readBoundedRegularFileSync(
              entity,
              maxBytes: ReleaseZipMetadataPolicy.maxUncompressedEntryBytes,
            ),
          )..mode = _mode(entity.statSync(), 0x1a4),
        );
      }
    }
    final encoded = markZipEntriesAsUnix(
      ZipEncoder().encode(archive, modified: _reproducibleZipTimestamp),
    );
    if (encoded.length > ReleaseZipMetadataPolicy.maxCompressedBytes) {
      throw StateError('Release zip exceeds the compressed size limit.');
    }
    _writeAtomically(destination, encoded);
  }

  void _writeAtomically(File destination, List<int> bytes) {
    destination.parent.createSync(recursive: true);
    final temporary = File(
      '${destination.path}.tmp-$pid-${DateTime.now().microsecondsSinceEpoch}',
    );
    try {
      temporary.writeAsBytesSync(bytes, flush: true);
      temporary.renameSync(destination.path);
    } finally {
      if (temporary.existsSync()) {
        temporary.deleteSync();
      }
    }
  }

  void extractDartZip(
    File archiveFile,
    Directory destination, {
    required bool allowContainedLinks,
  }) {
    final compressedLength = archiveFile.lengthSync();
    if (compressedLength > ReleaseZipMetadataPolicy.maxCompressedBytes) {
      throw StateError(
        'Release zip exceeds the compressed size limit '
        '(${ReleaseZipMetadataPolicy.maxCompressedBytes} bytes).',
      );
    }
    final compressedBytes = readBoundedRegularFileSync(
      archiveFile,
      maxBytes: ReleaseZipMetadataPolicy.maxCompressedBytes,
    );
    _zipMetadataPolicy.validateForExtraction(compressedBytes);
    final archive = ZipDecoder().decodeBytes(compressedBytes);
    final destinationRoot = p.normalize(destination.absolute.path);
    final validatedEntries = <MapEntry<ArchiveFile, String>>[];
    final symlinkNames = <String>{};
    final entriesByName = <String, ArchiveFile>{};
    for (final entry in archive.files) {
      _rejectUnsafeArchiveName(entry.name);
      final normalizedName = _normalizedArchiveName(entry.name);
      final entryKey = _archiveKey(normalizedName);
      final previous = entriesByName[entryKey];
      if (previous != null) {
        throw StateError(
          'Zip contains conflicting entries: ${previous.name} and ${entry.name}',
        );
      }
      entriesByName[entryKey] = entry;
      if (entry.isSymbolicLink && !allowContainedLinks) {
        throw StateError(
          'Release zips may not contain symlinks: ${entry.name}',
        );
      }
      if (entry.isSymbolicLink) {
        final target = entry.symbolicLink;
        if (target == null || target.isEmpty) {
          throw StateError('Zip symlink has no target: ${entry.name}');
        }
        _rejectEscapingArchiveLink(normalizedName, target);
        symlinkNames.add(_archiveKey(normalizedName));
      }
      final output = p.normalize(p.join(destinationRoot, entry.name));
      if (!p.isWithin(destinationRoot, output) && output != destinationRoot) {
        throw StateError('Zip entry escapes the target: ${entry.name}');
      }
      validatedEntries.add(MapEntry(entry, output));
    }
    _rejectEntriesNestedUnderLinks(archive.files, symlinkNames);
    _rejectEntriesNestedUnderFiles(entriesByName);

    final destinationExisted = destination.existsSync();
    if (destinationExisted && destination.listSync().isNotEmpty) {
      throw StateError(
        'Release extraction destination must be empty: ${destination.path}',
      );
    }
    try {
      destination.createSync(recursive: true);
      for (final validated in validatedEntries) {
        final entry = validated.key;
        final output = validated.value;
        if (entry.isSymbolicLink) {
          continue;
        }
        if (entry.isDirectory) {
          Directory(output).createSync(recursive: true);
        } else {
          final bytes = _validatedEntryBytes(entry);
          File(output)
            ..parent.createSync(recursive: true)
            ..writeAsBytesSync(bytes);
          if (!Platform.isWindows && (entry.unixPermissions & 0x49) != 0) {
            final chmod = Process.runSync('chmod', ['+x', output]);
            if (chmod.exitCode != 0) {
              throw StateError('Could not set executable mode on $output.');
            }
          }
        }
      }
      for (final validated in validatedEntries.where(
        (item) => item.key.isSymbolicLink,
      )) {
        final entry = validated.key;
        final output = validated.value;
        final bytes = _validatedEntryBytes(entry);
        final decodedTarget = utf8.decode(bytes, allowMalformed: false);
        if (decodedTarget != entry.symbolicLink) {
          throw StateError(
            'Zip symlink metadata is inconsistent: ${entry.name}',
          );
        }
        Link(output)
          ..parent.createSync(recursive: true)
          ..createSync(entry.symbolicLink!);
      }
    } on Object {
      if (destination.existsSync()) {
        destination.deleteSync(recursive: true);
      }
      if (destinationExisted) {
        destination.createSync(recursive: true);
      }
      rethrow;
    }
  }

  List<int> _validatedEntryBytes(ArchiveFile entry) {
    final bytes = entry.readBytes();
    if (bytes == null || bytes.length != entry.size) {
      throw StateError('Zip entry size does not match metadata: ${entry.name}');
    }
    if (entry.crc32 != null && getCrc32(bytes) != entry.crc32) {
      throw StateError('Zip entry checksum does not match: ${entry.name}');
    }
    return bytes;
  }

  void _rejectEntriesNestedUnderLinks(
    List<ArchiveFile> entries,
    Set<String> symlinkNames,
  ) {
    for (final entry in entries) {
      final normalizedName = _normalizedArchiveName(entry.name);
      var parent = p.posix.dirname(normalizedName);
      while (parent != '.' && parent.isNotEmpty) {
        if (symlinkNames.contains(_archiveKey(parent))) {
          throw StateError(
            'Zip entry is nested beneath a symlink: ${entry.name}',
          );
        }
        parent = p.posix.dirname(parent);
      }
    }
  }

  void _rejectEntriesNestedUnderFiles(Map<String, ArchiveFile> entriesByName) {
    for (final entry in entriesByName.values) {
      var parent = p.posix.dirname(_normalizedArchiveName(entry.name));
      while (parent != '.' && parent.isNotEmpty) {
        final ancestor = entriesByName[_archiveKey(parent)];
        if (ancestor != null && !ancestor.isDirectory) {
          throw StateError(
            'Zip entry is nested beneath a non-directory entry: '
            '${entry.name} beneath ${ancestor.name}',
          );
        }
        parent = p.posix.dirname(parent);
      }
    }
  }

  int _mode(FileStat stat, int fallback) {
    final mode = stat.mode & 0x1ff;
    return 0x8000 | (mode == 0 ? fallback : mode);
  }

  String _normalizedArchiveName(String name) {
    var normalized = p.posix.normalize(name);
    while (normalized.length > 1 && normalized.endsWith('/')) {
      normalized = normalized.substring(0, normalized.length - 1);
    }
    return normalized;
  }

  String _archiveKey(String normalizedName) => normalizedName.toLowerCase();

  void _rejectEscapingArchiveLink(String name, String target) {
    if (target.contains('\u0000') ||
        target.contains('\\') ||
        p.posix.isAbsolute(target) ||
        RegExp(r'^[A-Za-z]:/').hasMatch(target)) {
      throw StateError('Zip symlink has an unsafe target: $name -> $target');
    }
    final resolved = p.posix.normalize(
      p.posix.join(p.posix.dirname(name), target),
    );
    if (resolved == '..' || resolved.startsWith('../')) {
      throw StateError('Zip symlink escapes the target: $name -> $target');
    }
  }

  void _rejectUnsafeArchiveName(String name) {
    if (name.isEmpty || name.contains('\u0000')) {
      throw StateError('Zip entry has an invalid empty or NUL path.');
    }
    if (name.length > _maximumArchivePathLength ||
        name.codeUnits.any((unit) => unit < 0x20 || unit > 0x7e)) {
      throw StateError('Zip entry has a non-portable path: $name');
    }
    if (name.contains('\\') || name.contains(':')) {
      throw StateError('Zip entry has a non-portable path separator: $name');
    }
    if (p.posix.isAbsolute(name) || RegExp(r'^[A-Za-z]:/').hasMatch(name)) {
      throw StateError('Zip entry must be relative: $name');
    }
    final pathName = name.endsWith('/')
        ? name.substring(0, name.length - 1)
        : name;
    final segments = pathName.split('/');
    for (final segment in segments) {
      if (segment.isEmpty ||
          segment == '.' ||
          segment == '..' ||
          segment.length > _maximumArchiveSegmentLength ||
          segment.endsWith('.') ||
          segment.endsWith(' ')) {
        throw StateError('Zip entry has an unsafe path segment: $name');
      }
      final stem = segment.split('.').first.toUpperCase();
      if (_windowsDeviceNames.contains(stem)) {
        throw StateError('Zip entry uses a reserved device name: $name');
      }
    }
  }

  /// Sets the central-directory "version made by" OS byte without scanning or
  /// mutating file payload bytes that happen to contain the same signature.
  List<int> markZipEntriesAsUnix(List<int> bytes) {
    return _zipMetadataPolicy.markEntriesAsUnix(bytes);
  }
}

const _zipMetadataPolicy = ReleaseZipMetadataPolicy();
final _reproducibleZipTimestamp = DateTime(1980, 1, 1);
const _maximumArchivePathLength = 1024;
const _maximumArchiveSegmentLength = 255;
const _windowsDeviceNames = {
  'CON',
  'PRN',
  'AUX',
  'NUL',
  'COM1',
  'COM2',
  'COM3',
  'COM4',
  'COM5',
  'COM6',
  'COM7',
  'COM8',
  'COM9',
  'LPT1',
  'LPT2',
  'LPT3',
  'LPT4',
  'LPT5',
  'LPT6',
  'LPT7',
  'LPT8',
  'LPT9',
};
