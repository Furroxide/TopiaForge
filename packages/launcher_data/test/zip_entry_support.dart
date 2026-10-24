/// Rewrites ZIP bytes the ways a dishonest or damaged package arrives: an
/// entry that declares fewer bytes than its data expands to, or one whose data
/// is damaged while every size it declares is true.
library;

/// Makes [name] declare [size] uncompressed bytes, in its local header and in
/// its central directory entry alike, whatever its data expands to.
void declareEntrySize(List<int> zip, String name, int size) {
  final headers = _headersOf(zip, name);
  _writeUint32(zip, headers.local + 22, size);
  _writeUint32(zip, headers.central + 24, size);
}

/// Damages [name]'s deflate data while every size it declares stays true: its
/// first block becomes one of the reserved type, which no inflater accepts.
void damageEntryData(List<int> zip, String name) {
  final local = _headersOf(zip, name).local;
  if (_readUint16(zip, local + 8) != 8) {
    throw StateError('$name is not deflated, so this cannot damage it.');
  }
  final data =
      local + 30 + _readUint16(zip, local + 26) + _readUint16(zip, local + 28);
  zip[data] = 0xff;
}

/// Finds [name]'s local header and central directory entry through the end of
/// central directory record, as a reader does.
({int local, int central}) _headersOf(List<int> zip, String name) {
  var end = zip.length - 22;
  while (end >= 0 && _readUint32(zip, end) != 0x06054b50) {
    end--;
  }
  if (end < 0) {
    throw StateError('The ZIP has no end of central directory record.');
  }
  var central = _readUint32(zip, end + 16);
  for (var entry = 0; entry < _readUint16(zip, end + 10); entry++) {
    final nameLength = _readUint16(zip, central + 28);
    final entryName = String.fromCharCodes(
      zip.sublist(central + 46, central + 46 + nameLength),
    );
    if (entryName == name) {
      return (local: _readUint32(zip, central + 42), central: central);
    }
    central +=
        46 +
        nameLength +
        _readUint16(zip, central + 30) +
        _readUint16(zip, central + 32);
  }
  throw StateError('The ZIP has no entry named $name.');
}

int _readUint16(List<int> bytes, int offset) =>
    bytes[offset] | (bytes[offset + 1] << 8);

int _readUint32(List<int> bytes, int offset) =>
    _readUint16(bytes, offset) | (_readUint16(bytes, offset + 2) << 16);

void _writeUint32(List<int> bytes, int offset, int value) {
  for (var index = 0; index < 4; index++) {
    bytes[offset + index] = (value >> (index * 8)) & 0xff;
  }
}
