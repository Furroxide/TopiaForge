import 'dart:io';

import 'package:archive/archive.dart';
import 'package:path/path.dart' as p;
import 'package:robotopia/src/release_archive_policy.dart';
import 'package:robotopia/src/release_package_io.dart';
import 'package:robotopia/src/release_package_models.dart';
import 'package:test/test.dart';

void main() {
  late Directory temp;

  setUp(() {
    temp = Directory.systemTemp.createTempSync('release-archive-policy-');
  });

  tearDown(() {
    if (temp.existsSync()) {
      temp.deleteSync(recursive: true);
    }
  });

  test(
    'rejects an escaping source link before invoking a native writer',
    () async {
      final outside = File(p.join(temp.path, 'outside.txt'))
        ..writeAsStringSync('secret');
      final source = Directory(p.join(temp.path, 'stage'))..createSync();
      Link(
        p.join(source.path, 'leak.txt'),
      ).createSync(p.relative(outside.path, from: source.path));
      final runner = _NoRunProcessRunner();

      await expectLater(
        () => ReleaseFileOps(processRunner: runner).writePlatformZip(
          source,
          File(p.join(temp.path, 'release.zip')),
          ReleasePackagePlatform.linux,
        ),
        throwsA(isA<StateError>()),
      );
      expect(runner.runCalls, 0);
    },
    skip: Platform.isWindows
        ? 'Creating developer-mode symlinks is not reliable on Windows CI.'
        : false,
  );

  test('rejects an escaping macOS archive link before writing files', () async {
    final archive = Archive()
      ..addFile(ArchiveFile.string('safe.txt', 'safe'))
      ..addFile(ArchiveFile.string('Framework', '../outside')..mode = 0xa1ff);
    final zip = File(p.join(temp.path, 'escape.zip'))
      ..writeAsBytesSync(_markZipEntriesAsUnix(ZipEncoder().encode(archive)));
    final destination = Directory(p.join(temp.path, 'extracted'));
    final runner = _NoRunProcessRunner();

    await expectLater(
      () => ReleaseFileOps(
        processRunner: runner,
      ).extractPlatformZip(zip, destination, ReleasePackagePlatform.macos),
      throwsA(isA<StateError>()),
    );
    expect(runner.runCalls, 0);
    expect(File(p.join(destination.path, 'safe.txt')).existsSync(), false);
  });

  test('rejects archive entries nested beneath a symlink', () async {
    final archive = Archive()
      ..addFile(ArchiveFile.directory('Versions/A'))
      ..addFile(ArchiveFile.string('Versions/Current', 'A')..mode = 0xa1ff)
      ..addFile(ArchiveFile.string('Versions/Current/injected', 'bad'));
    final zip = File(p.join(temp.path, 'nested.zip'))
      ..writeAsBytesSync(_markZipEntriesAsUnix(ZipEncoder().encode(archive)));

    await expectLater(
      () => const ReleaseFileOps().extractPlatformZip(
        zip,
        Directory(p.join(temp.path, 'extracted')),
        ReleasePackagePlatform.macos,
      ),
      throwsA(isA<StateError>()),
    );
  });

  test('rejects case-folded entries nested beneath a symlink', () async {
    final archive = Archive()
      ..addFile(ArchiveFile.directory('Versions/A'))
      ..addFile(ArchiveFile.string('Versions/Current', 'A')..mode = 0xa1ff)
      ..addFile(ArchiveFile.string('versions/current/injected', 'bad'));
    final zip = File(p.join(temp.path, 'case-folded-nested.zip'))
      ..writeAsBytesSync(_markZipEntriesAsUnix(ZipEncoder().encode(archive)));

    await expectLater(
      () => const ReleaseFileOps().extractPlatformZip(
        zip,
        Directory(p.join(temp.path, 'case-folded-extracted')),
        ReleasePackagePlatform.macos,
      ),
      throwsA(isA<StateError>()),
    );
  });

  test('rejects case-folded duplicate entries before writing files', () async {
    final archive = Archive()
      ..addFile(ArchiveFile.string('safe.txt', 'safe'))
      ..addFile(ArchiveFile.string('Folder/value.txt', 'first'))
      ..addFile(ArchiveFile.string('folder/VALUE.txt', 'second'));
    final zip = File(p.join(temp.path, 'duplicate.zip'))
      ..writeAsBytesSync(ZipEncoder().encode(archive));
    final destination = Directory(p.join(temp.path, 'duplicate-extracted'));

    await expectLater(
      () => const ReleaseFileOps().extractPlatformZip(
        zip,
        destination,
        ReleasePackagePlatform.windows,
      ),
      throwsA(isA<StateError>()),
    );
    expect(File(p.join(destination.path, 'safe.txt')).existsSync(), false);
  });

  test('rejects an entry nested beneath a file before writing files', () async {
    final archive = Archive()
      ..addFile(ArchiveFile.string('safe.txt', 'safe'))
      ..addFile(ArchiveFile.string('payload', 'file'))
      ..addFile(ArchiveFile.string('payload/child.txt', 'child'));
    final zip = File(p.join(temp.path, 'file-prefix.zip'))
      ..writeAsBytesSync(ZipEncoder().encode(archive));
    final destination = Directory(p.join(temp.path, 'prefix-extracted'));

    await expectLater(
      () => const ReleaseFileOps().extractPlatformZip(
        zip,
        destination,
        ReleasePackagePlatform.windows,
      ),
      throwsA(isA<StateError>()),
    );
    expect(File(p.join(destination.path, 'safe.txt')).existsSync(), false);
  });

  test('rejects reserved device names and alternate data streams', () async {
    for (final name in ['CON.txt', 'folder/NUL', 'payload.txt:secret']) {
      final zip = File(p.join(temp.path, '${name.hashCode}.zip'))
        ..writeAsBytesSync(
          ZipEncoder().encode(
            Archive()..addFile(ArchiveFile.string(name, 'payload')),
          ),
        );
      await expectLater(
        () => const ReleaseFileOps().extractPlatformZip(
          zip,
          Directory(p.join(temp.path, 'portable-${name.hashCode}')),
          ReleasePackagePlatform.windows,
        ),
        throwsA(isA<StateError>()),
        reason: name,
      );
    }
  });

  test('rejects oversized metadata before decompression', () async {
    final encoded = ZipEncoder().encode(
      Archive()..addFile(ArchiveFile.string('payload.txt', 'small')),
    );
    final central = _findSignature(encoded, 0);
    expect(central, greaterThanOrEqualTo(0));
    _writeUint32(encoded, central + 24, 512 * 1024 * 1024 + 1);
    final zip = File(p.join(temp.path, 'oversized-metadata.zip'))
      ..writeAsBytesSync(encoded);

    await expectLater(
      () => const ReleaseFileOps().extractPlatformZip(
        zip,
        Directory(p.join(temp.path, 'oversized-extracted')),
        ReleasePackagePlatform.windows,
      ),
      throwsA(isA<StateError>()),
    );
  });

  test('rejects encrypted and Unix special-file metadata', () async {
    final encrypted = ZipEncoder().encode(
      Archive()..addFile(ArchiveFile.string('encrypted.txt', 'payload')),
    );
    final encryptedCentral = _findSignature(encrypted, 0);
    encrypted[encryptedCentral + 8] |= 0x1;

    final special = _markZipEntriesAsUnix(
      ZipEncoder().encode(
        Archive()..addFile(ArchiveFile.string('device', 'payload')),
      ),
    );
    final specialCentral = _findSignature(special, 0);
    _writeUint32(special, specialCentral + 38, 0x21b6 << 16);

    for (final entry in {'encrypted': encrypted, 'special': special}.entries) {
      final zip = File(p.join(temp.path, '${entry.key}.zip'))
        ..writeAsBytesSync(entry.value);
      await expectLater(
        () => const ReleaseFileOps().extractPlatformZip(
          zip,
          Directory(p.join(temp.path, '${entry.key}-extracted')),
          ReleasePackagePlatform.windows,
        ),
        throwsA(isA<StateError>()),
        reason: entry.key,
      );
    }
  });

  test('rejects corrupt entry checksums and rolls back extraction', () async {
    final entry = ArchiveFile.string('payload.txt', 'payload')
      ..compression = CompressionType.none;
    final encoded = ZipEncoder().encode(Archive()..addFile(entry));
    final localNameLength = encoded[26] | (encoded[27] << 8);
    final localExtraLength = encoded[28] | (encoded[29] << 8);
    final payloadOffset = 30 + localNameLength + localExtraLength;
    encoded[payloadOffset] ^= 0xff;
    final zip = File(p.join(temp.path, 'corrupt.zip'))
      ..writeAsBytesSync(encoded);
    final destination = Directory(p.join(temp.path, 'corrupt-extracted'));

    await expectLater(
      () => const ReleaseFileOps().extractPlatformZip(
        zip,
        destination,
        ReleasePackagePlatform.windows,
      ),
      throwsA(isA<StateError>()),
    );
    expect(destination.existsSync(), isFalse);
  });

  test('Unix host marking never mutates matching file payload bytes', () {
    const payload = <int>[0x50, 0x4b, 0x01, 0x02, 0x10, 0x20, 0x30, 0x40];
    final stored = ArchiveFile.bytes('signature.bin', payload)
      ..compression = CompressionType.none;
    final encoded = ZipEncoder().encode(Archive()..addFile(stored));

    final patched = const ReleaseArchivePolicy().markZipEntriesAsUnix(encoded);
    final payloadSignature = _findSignature(patched, 0);
    final centralSignature = _findSignature(patched, payloadSignature + 1);

    expect(payloadSignature, greaterThanOrEqualTo(0));
    expect(centralSignature, greaterThan(payloadSignature));
    expect(patched[payloadSignature + 5], 0x20);
    expect(patched[centralSignature + 5], 3);
    expect(ZipDecoder().decodeBytes(patched).files.single.readBytes(), payload);
  });

  test(
    'preserves a contained macOS framework link through zip extraction',
    () async {
      final source = Directory(p.join(temp.path, 'stage'))..createSync();
      final versions = Directory(p.join(source.path, 'Framework', 'Versions'))
        ..createSync(recursive: true);
      final current = Directory(p.join(versions.path, 'A'))..createSync();
      File(p.join(current.path, 'binary')).writeAsStringSync('payload');
      Link(p.join(versions.path, 'Current')).createSync('A');
      final zip = File(p.join(temp.path, 'release.zip'));

      await const ReleaseFileOps().writePlatformZip(
        source,
        zip,
        ReleasePackagePlatform.macos,
      );
      final extracted = Directory(p.join(temp.path, 'extracted'));
      await const ReleaseFileOps().extractPlatformZip(
        zip,
        extracted,
        ReleasePackagePlatform.macos,
      );

      final link = Link(
        p.join(extracted.path, 'Framework', 'Versions', 'Current'),
      );
      expect(link.targetSync(), 'A');
      expect(File(p.join(link.path, 'binary')).readAsStringSync(), 'payload');
    },
    skip: Platform.isWindows
        ? 'Creating developer-mode symlinks is not reliable on Windows CI.'
        : false,
  );
}

List<int> _markZipEntriesAsUnix(List<int> bytes) {
  final patched = List<int>.of(bytes);
  for (var index = 0; index <= patched.length - 6; index += 1) {
    final isCentralHeader =
        patched[index] == 0x50 &&
        patched[index + 1] == 0x4b &&
        patched[index + 2] == 0x01 &&
        patched[index + 3] == 0x02;
    if (isCentralHeader) {
      patched[index + 5] = 3;
    }
  }
  return patched;
}

int _findSignature(List<int> bytes, int start) {
  for (var index = start; index <= bytes.length - 4; index += 1) {
    if (bytes[index] == 0x50 &&
        bytes[index + 1] == 0x4b &&
        bytes[index + 2] == 0x01 &&
        bytes[index + 3] == 0x02) {
      return index;
    }
  }
  return -1;
}

void _writeUint32(List<int> bytes, int offset, int value) {
  bytes[offset] = value & 0xff;
  bytes[offset + 1] = (value >> 8) & 0xff;
  bytes[offset + 2] = (value >> 16) & 0xff;
  bytes[offset + 3] = (value >> 24) & 0xff;
}

class _NoRunProcessRunner extends ReleaseProcessRunner {
  var runCalls = 0;

  @override
  Future<bool> commandExists(String executable) async => true;

  @override
  Future<void> runChecked(
    String executable,
    List<String> arguments, {
    String? workingDirectory,
    Map<String, String>? environment,
    bool runInShell = false,
    Set<String> redactedValueOptions = const {},
  }) async {
    runCalls += 1;
    throw StateError('Native archive command should not run.');
  }
}
