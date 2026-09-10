import 'dart:ffi';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'live_acceptance_test_fixture.dart';

void main() {
  test('ordinary fixture roots use actual filesystem names', () {
    final fixture = AcceptanceFixture();
    try {
      expect(fixture.temp.path, fixture.temp.resolveSymbolicLinksSync());
      expect(() => fixture.admitIsolation(fixture.options()), returnsNormally);
    } finally {
      fixture.dispose();
    }
  });
  test(
    'synthetic fixture canonicalizes an owned Windows short TEMP alias',
    () {
      final parent = Directory(
        Directory.systemTemp
            .createTempSync('TopiaForge Long Acceptance Fixture-')
            .resolveSymbolicLinksSync(),
      );
      try {
        final alias = _shortWindowsPath(parent.path);
        if (p.equals(alias.toLowerCase(), parent.path.toLowerCase())) {
          markTestSkipped('The fixture volume does not supply an 8.3 alias.');
          return;
        }
        // Production input remains invalid. Only our newly created fixture root
        // adopts the actual filesystem spelling before it constructs records.
        expect(
          () => requireAcceptanceUnlinkedPath(alias),
          throwsFormatException,
        );
        final fixture = AcceptanceFixture(temporaryRoot: Directory(alias));
        try {
          expect(fixture.temp.path, fixture.temp.resolveSymbolicLinksSync());
          expect(
            () => fixture.admitIsolation(fixture.options()),
            returnsNormally,
          );
        } finally {
          fixture.dispose();
        }
      } finally {
        parent.deleteSync(recursive: true);
      }
    },
    skip: !Platform.isWindows,
  );
}

String _shortWindowsPath(String path) {
  final kernel = DynamicLibrary.open('kernel32.dll');
  final heap = kernel
      .lookupFunction<Pointer<Void> Function(), Pointer<Void> Function()>(
        'GetProcessHeap',
      )();
  final allocate = kernel
      .lookupFunction<
        Pointer<Void> Function(Pointer<Void>, Uint32, IntPtr),
        Pointer<Void> Function(Pointer<Void>, int, int)
      >('HeapAlloc');
  final free = kernel
      .lookupFunction<
        Int32 Function(Pointer<Void>, Uint32, Pointer<Void>),
        int Function(Pointer<Void>, int, Pointer<Void>)
      >('HeapFree');
  final shortPath = kernel
      .lookupFunction<
        Uint32 Function(Pointer<Uint16>, Pointer<Uint16>, Uint32),
        int Function(Pointer<Uint16>, Pointer<Uint16>, int)
      >('GetShortPathNameW');
  Pointer<Uint16> input = nullptr;
  Pointer<Uint16> output = nullptr;
  try {
    input = allocate(heap, 8, (path.length + 1) * 2).cast<Uint16>();
    output = allocate(heap, 8, 32768 * 2).cast<Uint16>();
    if (input == nullptr || output == nullptr) {
      throw StateError('Cannot allocate the test path buffers.');
    }
    input.asTypedList(path.length).setAll(0, path.codeUnits);
    final length = shortPath(input, output, 32768);
    if (length == 0 || length >= 32768) {
      throw StateError('Cannot inspect the owned fixture short path.');
    }
    return String.fromCharCodes(output.asTypedList(length));
  } finally {
    if (output != nullptr) free(heap, 0, output.cast());
    if (input != nullptr) free(heap, 0, input.cast());
  }
}
