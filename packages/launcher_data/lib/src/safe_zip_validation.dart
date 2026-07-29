part of 'safe_zip_archive.dart';

Set<String> _preflight(
  ZipDirectory directory,
  int archiveBytes,
  SafeArchivePolicy policy,
  String label,
  bool allowContainedLinks,
) {
  final links = <String>{};
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
      allowContainedLinks: allowContainedLinks,
    );
    if (((header.externalFileAttributes >> 16) & 0xf000) == 0xa000) {
      links.add(header.filename);
    }
  }
  return links;
}

void _validateEntries(
  Archive archive,
  SafeArchivePolicy policy,
  String label,
  bool allowContainedLinks,
  Set<String> linkNames,
) {
  if (archive.files.length > policy.maxEntries) {
    throw StateError('$label contains too many entries.');
  }
  final paths = <String, bool>{};
  final links = <String>{};
  var expanded = 0;
  for (final entry in archive.files) {
    final isSymbolicLink =
        entry.isSymbolicLink || linkNames.contains(entry.name);
    if (isSymbolicLink && !allowContainedLinks) {
      throw StateError('$label contains a symbolic link: ${entry.name}');
    }
    _requireRegularZipType(
      entry.mode,
      entry.name,
      label,
      allowContainedLinks: allowContainedLinks,
    );
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
      if (links.contains(parent)) {
        throw StateError(
          '$label contains an entry nested beneath a link: $normalized',
        );
      }
      if (paths[parent] == true) {
        throw StateError('$label path collides with a file: $normalized');
      }
      parent = p.posix.dirname(parent);
    }
    if (entry.isFile && paths.keys.any((path) => path.startsWith('$key/'))) {
      throw StateError('$label path collides with a directory: $normalized');
    }
    if (isSymbolicLink) {
      final target =
          entry.symbolicLink ??
          utf8.decode(entry.readBytes() ?? const [], allowMalformed: false);
      _validateContainedLink(normalized, target, label);
      links.add(key);
    }
    paths[key] = entry.isFile || isSymbolicLink;
  }
}

void _requireRegularZipType(
  int mode,
  String name,
  String label, {
  bool allowContainedLinks = false,
}) {
  final type = mode & 0xf000;
  if (type == 0xa000 && !allowContainedLinks) {
    throw StateError('$label contains a symbolic link: $name');
  }
  if (type != 0 &&
      type != 0x4000 &&
      type != 0x8000 &&
      !(allowContainedLinks && type == 0xa000)) {
    throw StateError('$label contains an unsupported file type: $name');
  }
  if ((mode & 0xe00) != 0) {
    throw StateError('$label contains setuid/setgid/sticky permissions: $name');
  }
}

void _validateContainedLink(String name, String? target, String label) {
  if (target == null ||
      target.isEmpty ||
      target.length > 4096 ||
      target.contains('\u0000') ||
      target.contains('\\') ||
      p.posix.isAbsolute(target) ||
      RegExp(r'^[A-Za-z]:').hasMatch(target)) {
    throw StateError('$label contains an unsafe symbolic link: $name');
  }
  final resolved = p.posix.normalize(
    p.posix.join(p.posix.dirname(name), target),
  );
  if (resolved == '..' || resolved.startsWith('../')) {
    throw StateError('$label contains an escaping symbolic link: $name');
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
