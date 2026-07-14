class ReleaseZipMetadataPolicy {
  const ReleaseZipMetadataPolicy();

  void validateForExtraction(List<int> bytes) {
    if (bytes.length > maxCompressedBytes) {
      throw StateError(
        'Release zip exceeds the compressed size limit '
        '($maxCompressedBytes bytes).',
      );
    }
    final directory = _readDirectory(bytes);
    if (directory.totalEntries > maxEntries) {
      throw StateError(
        'Release zip has too many entries (${directory.totalEntries}; '
        'maximum: $maxEntries).',
      );
    }

    var totalUncompressed = 0;
    _visitEntries(bytes, directory, (cursor) {
      final flags = _readUint16(bytes, cursor + 8);
      if ((flags & 0x1) != 0) {
        throw StateError('Encrypted release zip entries are not supported.');
      }
      final compression = _readUint16(bytes, cursor + 10);
      if (compression != _storedCompression &&
          compression != _deflateCompression) {
        throw StateError(
          'Release zip uses unsupported compression method $compression.',
        );
      }
      final compressed = _readUint32(bytes, cursor + 20);
      final uncompressed = _readUint32(bytes, cursor + 24);
      if (compressed == _zip64Sentinel || uncompressed == _zip64Sentinel) {
        throw StateError('ZIP64 release zips are not supported.');
      }
      if (compressed > maxCompressedEntryBytes ||
          uncompressed > maxUncompressedEntryBytes) {
        throw StateError('Release zip entry exceeds its size limit.');
      }
      totalUncompressed += uncompressed;
      if (totalUncompressed > maxTotalUncompressedBytes) {
        throw StateError(
          'Release zip exceeds the total uncompressed size limit.',
        );
      }
      if (uncompressed > _ratioCheckFloorBytes &&
          (compressed == 0 ||
              uncompressed > compressed * maxCompressionRatio)) {
        throw StateError(
          'Release zip entry exceeds the maximum compression ratio.',
        );
      }

      final hostSystem = _readUint16(bytes, cursor + 4) >> 8;
      if (hostSystem == _unixHostSystem) {
        final mode = _readUint32(bytes, cursor + 38) >> 16;
        final fileType = mode & _unixFileTypeMask;
        if (fileType != 0 &&
            fileType != _unixRegularFile &&
            fileType != _unixDirectory &&
            fileType != _unixSymlink) {
          throw StateError(
            'Release zip contains a Unix device or special-file entry.',
          );
        }
        if (fileType == _unixSymlink && uncompressed > maxSymlinkTargetBytes) {
          throw StateError('Release zip symlink target is too large.');
        }
      }
    });
  }

  List<int> markEntriesAsUnix(List<int> bytes) {
    final patched = List<int>.of(bytes);
    final directory = _readDirectory(patched);
    _visitEntries(patched, directory, (cursor) {
      patched[cursor + 5] = _unixHostSystem;
    });
    return patched;
  }

  _ZipDirectory _readDirectory(List<int> bytes) {
    final eocd = _findEndOfCentralDirectory(bytes);
    final diskNumber = _readUint16(bytes, eocd + 4);
    final centralDisk = _readUint16(bytes, eocd + 6);
    final diskEntries = _readUint16(bytes, eocd + 8);
    final totalEntries = _readUint16(bytes, eocd + 10);
    final centralSize = _readUint32(bytes, eocd + 12);
    final centralOffset = _readUint32(bytes, eocd + 16);
    if (diskNumber != 0 || centralDisk != 0 || diskEntries != totalEntries) {
      throw StateError('Multi-disk release zips are not supported.');
    }
    if (totalEntries == _zip16Sentinel ||
        centralSize == _zip64Sentinel ||
        centralOffset == _zip64Sentinel) {
      throw StateError('ZIP64 release zips are not supported.');
    }

    final centralEnd = centralOffset + centralSize;
    if (centralEnd > eocd || centralEnd < centralOffset) {
      throw StateError('Zip central directory is outside the archive bounds.');
    }
    return _ZipDirectory(
      totalEntries: totalEntries,
      centralOffset: centralOffset,
      centralEnd: centralEnd,
    );
  }

  void _visitEntries(
    List<int> bytes,
    _ZipDirectory directory,
    void Function(int cursor) visit,
  ) {
    var cursor = directory.centralOffset;
    for (var entry = 0; entry < directory.totalEntries; entry += 1) {
      if (cursor + _centralHeaderLength > directory.centralEnd ||
          _readUint32(bytes, cursor) != _centralHeaderSignature) {
        throw StateError('Zip central directory contains an invalid entry.');
      }
      final nameLength = _readUint16(bytes, cursor + 28);
      final extraLength = _readUint16(bytes, cursor + 30);
      final commentLength = _readUint16(bytes, cursor + 32);
      final next =
          cursor +
          _centralHeaderLength +
          nameLength +
          extraLength +
          commentLength;
      if (next > directory.centralEnd) {
        throw StateError('Zip central-directory entry exceeds its bounds.');
      }
      visit(cursor);
      cursor = next;
    }
    if (cursor != directory.centralEnd) {
      throw StateError(
        'Zip central-directory size does not match its entries.',
      );
    }
  }

  int _findEndOfCentralDirectory(List<int> bytes) {
    final earliest = bytes.length - _minimumEocdLength - _maximumZipComment;
    for (
      var offset = bytes.length - _minimumEocdLength;
      offset >= (earliest < 0 ? 0 : earliest);
      offset -= 1
    ) {
      if (_readUint32(bytes, offset) != _eocdSignature) {
        continue;
      }
      final commentLength = _readUint16(bytes, offset + 20);
      if (offset + _minimumEocdLength + commentLength == bytes.length) {
        return offset;
      }
    }
    throw StateError('Zip end-of-central-directory record was not found.');
  }

  int _readUint16(List<int> bytes, int offset) {
    if (offset < 0 || offset + 2 > bytes.length) {
      throw StateError('Zip structure exceeds the archive bounds.');
    }
    return bytes[offset] | (bytes[offset + 1] << 8);
  }

  int _readUint32(List<int> bytes, int offset) {
    if (offset < 0 || offset + 4 > bytes.length) {
      throw StateError('Zip structure exceeds the archive bounds.');
    }
    return bytes[offset] |
        (bytes[offset + 1] << 8) |
        (bytes[offset + 2] << 16) |
        (bytes[offset + 3] << 24);
  }

  static const maxCompressedBytes = 512 * 1024 * 1024;
  static const maxCompressedEntryBytes = 512 * 1024 * 1024;
  static const maxUncompressedEntryBytes = 512 * 1024 * 1024;
  static const maxTotalUncompressedBytes = 2 * 1024 * 1024 * 1024;
  static const maxEntries = 20000;
  static const maxCompressionRatio = 200;
  static const maxSymlinkTargetBytes = 4096;
  static const _ratioCheckFloorBytes = 4 * 1024 * 1024;
}

class _ZipDirectory {
  const _ZipDirectory({
    required this.totalEntries,
    required this.centralOffset,
    required this.centralEnd,
  });

  final int totalEntries;
  final int centralOffset;
  final int centralEnd;
}

const _centralHeaderSignature = 0x02014b50;
const _eocdSignature = 0x06054b50;
const _centralHeaderLength = 46;
const _minimumEocdLength = 22;
const _maximumZipComment = 0xffff;
const _zip16Sentinel = 0xffff;
const _zip64Sentinel = 0xffffffff;
const _unixHostSystem = 3;
const _storedCompression = 0;
const _deflateCompression = 8;
const _unixFileTypeMask = 0xf000;
const _unixRegularFile = 0x8000;
const _unixDirectory = 0x4000;
const _unixSymlink = 0xa000;
