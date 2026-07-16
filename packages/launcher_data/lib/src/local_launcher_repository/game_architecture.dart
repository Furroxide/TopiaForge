part of '../local_launcher_repository.dart';

const _maxExecutableHeaderOffset = 1024 * 1024;
const _peMachineX64 = 0x8664;
const _peMachineArm64 = 0xaa64;
const _machCpuX64 = 0x01000007;
const _machCpuArm64 = 0x0100000c;

String _readGameArchitecture(File executable) {
  if (FileSystemEntity.typeSync(executable.path, followLinks: false) !=
      FileSystemEntityType.file) {
    return '';
  }
  RandomAccessFile? handle;
  try {
    handle = executable.openSync(mode: FileMode.read);
    final length = handle.lengthSync();
    if (length < 8) return '';
    final prefix = handle.readSync(length < 64 ? length : 64);
    if (prefix.length >= 64 && prefix[0] == 0x4d && prefix[1] == 0x5a) {
      return _readPeArchitecture(handle, prefix, length);
    }
    return _readMachArchitecture(handle, prefix, length);
  } on Object {
    return '';
  } finally {
    handle?.closeSync();
  }
}

String _readPeArchitecture(
  RandomAccessFile handle,
  Uint8List prefix,
  int length,
) {
  final peOffset = ByteData.sublistView(prefix).getUint32(0x3c, Endian.little);
  if (peOffset < 0x40 ||
      peOffset > _maxExecutableHeaderOffset ||
      peOffset + 6 > length) {
    return '';
  }
  handle.setPositionSync(peOffset);
  final header = handle.readSync(6);
  if (header.length != 6 ||
      header[0] != 0x50 ||
      header[1] != 0x45 ||
      header[2] != 0 ||
      header[3] != 0) {
    return '';
  }
  return _architectureForMachine(
    ByteData.sublistView(header).getUint16(4, Endian.little),
  );
}

String _readMachArchitecture(
  RandomAccessFile handle,
  Uint8List prefix,
  int length,
) {
  final prefixData = ByteData.sublistView(prefix);
  final bigMagic = prefixData.getUint32(0, Endian.big);
  final littleMagic = prefixData.getUint32(0, Endian.little);
  if (bigMagic == 0xfeedface || bigMagic == 0xfeedfacf) {
    return _architectureForMachCpu(prefixData.getUint32(4, Endian.big));
  }
  if (littleMagic == 0xfeedface || littleMagic == 0xfeedfacf) {
    return _architectureForMachCpu(prefixData.getUint32(4, Endian.little));
  }
  final fat64 = bigMagic == 0xcafebabf || littleMagic == 0xcafebabf;
  final fat32 = bigMagic == 0xcafebabe || littleMagic == 0xcafebabe;
  if (!fat32 && !fat64) return '';
  final endian = bigMagic == 0xcafebabe || bigMagic == 0xcafebabf
      ? Endian.big
      : Endian.little;
  final count = prefixData.getUint32(4, endian);
  if (count == 0 || count > 16) return '';
  final entrySize = fat64 ? 32 : 20;
  final headerLength = 8 + count * entrySize;
  if (headerLength > length || headerLength > 520) return '';
  handle.setPositionSync(0);
  final header = handle.readSync(headerLength);
  if (header.length != headerLength) return '';
  final data = ByteData.sublistView(header);
  final architectures = <String>{};
  for (var index = 0; index < count; index++) {
    final architecture = _architectureForMachCpu(
      data.getUint32(8 + index * entrySize, endian),
    );
    if (architecture.isNotEmpty) architectures.add(architecture);
  }
  if (architectures.length == 1) return architectures.single;
  final processArchitecture = _processArchitecture();
  return architectures.contains(processArchitecture) ? processArchitecture : '';
}

String _architectureForMachine(int machine) => switch (machine) {
  _peMachineX64 => 'x64',
  _peMachineArm64 => 'arm64',
  _ => '',
};

String _architectureForMachCpu(int cpu) => switch (cpu) {
  _machCpuX64 => 'x64',
  _machCpuArm64 => 'arm64',
  _ => '',
};

String _processArchitecture() {
  final name = ffi.Abi.current().toString().toLowerCase();
  if (name.contains('arm64')) return 'arm64';
  if (name.contains('x64')) return 'x64';
  return '';
}
