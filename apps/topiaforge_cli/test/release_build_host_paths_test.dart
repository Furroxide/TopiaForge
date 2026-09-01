import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:topiaforge/src/release_build_host_paths.dart';
import 'package:test/test.dart';

/// Minimal PE-shaped bytes. The scanner reads content, not structure, so the
/// header only has to keep the file from looking like text to a reader.
List<int> _binaryCarrying(String payload) => <int>[
  0x4D, 0x5A, 0x90, 0x00,
  ...payload.codeUnits,
  0x00, 0x00,
];

File _write(Directory root, String name, List<int> bytes) {
  final file = File(p.join(root.path, name))..createSync(recursive: true);
  file.writeAsBytesSync(bytes);
  return file;
}

void main() {
  late Directory temp;

  setUp(() {
    temp = Directory.systemTemp.createTempSync('build-host-paths-');
  });

  tearDown(() {
    if (temp.existsSync()) temp.deleteSync(recursive: true);
  });

  test('accepts a payload whose binaries carry no home directory', () {
    _write(temp, 'topiaforge.dll', _binaryCarrying(r'/_/src/Thing/obj/a.pdb'));
    _write(temp, 'launcher.exe', _binaryCarrying('file:///t:/apps/cli/bin/x.dart'));
    expect(() => const BuildHostPathScanner().assertClean(temp.path), returnsNormally);
  });

  test('rejects a Windows profile path recorded by the compiler', () {
    _write(
      temp,
      'TopiaForge.Mods.Abstractions.dll',
      _binaryCarrying(r'RSDS....C:\Users\buildacct\Code\TopiaForge\obj\a.pdb'),
    );
    expect(
      () => const BuildHostPathScanner().assertClean(temp.path),
      throwsA(
        isA<StateError>().having(
          (e) => e.message,
          'message',
          allOf(
            contains('TopiaForge.Mods.Abstractions.dll'),
            contains(r'C:\Users\buildacct'),
          ),
        ),
      ),
    );
  });

  test('rejects a Dart AOT snapshot recording a POSIX home directory', () {
    _write(
      temp,
      'topiaforge.so',
      _binaryCarrying('file:///home/buildacct/topiaforge/bin/topiaforge.dart'),
    );
    expect(
      () => const BuildHostPathScanner().assertClean(temp.path),
      throwsA(isA<StateError>()),
    );
  });

  test('rejects a macOS home directory', () {
    _write(temp, 'thing.dylib', _binaryCarrying('/Users/buildacct/src/x.dart'));
    expect(
      () => const BuildHostPathScanner().assertClean(temp.path),
      throwsA(isA<StateError>()),
    );
  });

  test('allows machine accounts that identify no person', () {
    _write(temp, 'shared.dll', _binaryCarrying(r'C:\Users\Public\Documents\x'));
    _write(temp, 'other.dll', _binaryCarrying(r'C:\Users\Default\AppData\y'));
    expect(() => const BuildHostPathScanner().assertClean(temp.path), returnsNormally);
  });

  test('allows a neutral absolute build root such as the WSL builder', () {
    _write(temp, 'cli.so', _binaryCarrying('file:///root/topiaforge-build/bin/x.dart'));
    _write(temp, 'other.so', _binaryCarrying('/build/src/y.dart'));
    expect(() => const BuildHostPathScanner().assertClean(temp.path), returnsNormally);
  });

  test('ignores documentation, which legitimately shows example paths', () {
    File(p.join(temp.path, 'Troubleshooting.md'))
      ..createSync(recursive: true)
      ..writeAsStringSync(r'Set ROBOTOPIA_GAME_DIR, e.g. C:\Users\alice\Games\Robotopia.');
    expect(() => const BuildHostPathScanner().assertClean(temp.path), returnsNormally);
  });

  test('finds a path stored as UTF-16, as PE resources do', () {
    final utf16 = <int>[];
    for (final unit in r'C:\Users\buildacct\obj\a.pdb'.codeUnits) {
      utf16..add(unit)..add(0x00);
    }
    _write(temp, 'resource.dll', <int>[0x4D, 0x5A, ...utf16]);
    expect(
      () => const BuildHostPathScanner().assertClean(temp.path),
      throwsA(isA<StateError>()),
    );
  });

  test('scans extension-less Unix executables by magic number', () {
    // ELF header, then the leaked path.
    _write(temp, 'topiaforge', <int>[
      0x7F, 0x45, 0x4C, 0x46,
      ...'file:///home/buildacct/x.dart'.codeUnits,
    ]);
    expect(
      () => const BuildHostPathScanner().assertClean(temp.path),
      throwsA(isA<StateError>()),
    );
  });
}
