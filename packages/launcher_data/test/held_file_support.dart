/// Makes a file or a folder behave the way another program, or a folder's
/// permissions, can make it behave, for tests of what the launcher does then.
///
/// Each helper returns its release, which also runs when the test ends, or
/// null after marking the test skipped where the condition cannot be made or
/// does not hold, as for an account such as root that bypasses permissions.
library;

import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:test/test.dart';

/// Keeps [file] where it is, the way another program that holds it open
/// without delete sharing does: it can still be read, but neither moved nor
/// deleted.
///
/// On Windows a child process opens it with read sharing only. Elsewhere its
/// folder's mode denies changes (555), which holds every other entry in the
/// folder in place too, so there a test cannot show that the others stay
/// free to move or delete.
Future<Future<void> Function()?> holdInPlace(File file) => _releasedAtEnd(
  Platform.isWindows ? _openWithReadSharing(file) : _freezeFolder(file),
  'This account can move any file.',
);

/// Lets [folder] list its names but not be searched (mode 444), so every file
/// in it looks missing, [inside] among them. POSIX only.
Future<Future<void> Function()?> denySearching(Directory folder, File inside) =>
    _releasedAtEnd(
      _withMode(
        folder,
        '444',
        holds: () =>
            FileSystemEntity.typeSync(inside.path, followLinks: false) ==
            FileSystemEntityType.notFound,
      ),
      'This account can search any folder, or folder modes are not POSIX.',
    );

/// Lets [folder] be searched and written but not listed (mode 300). POSIX
/// only.
Future<Future<void> Function()?> denyListing(Directory folder) =>
    _releasedAtEnd(
      _withMode(
        folder,
        '300',
        holds: () {
          try {
            folder.listSync();
            return false;
          } on FileSystemException {
            return true;
          }
        },
      ),
      'This account can list any folder, or folder modes are not POSIX.',
    );

Future<Future<void> Function()?> _releasedAtEnd(
  Future<Future<void> Function()?> holding,
  String skipped,
) async {
  final release = await holding;
  if (release == null) {
    markTestSkipped(skipped);
    return null;
  }
  var released = false;
  Future<void> once() async {
    if (released) return;
    released = true;
    await release();
  }

  addTearDown(once);
  return once;
}

/// Opens the file named by `TOPIAFORGE_TEST_HOLD` for reading with read
/// sharing only, says so, and keeps it open until the file named by
/// `TOPIAFORGE_TEST_RELEASE` appears.
const _holder = r'''
$ErrorActionPreference = 'Stop'
$file = [IO.File]::Open(
  $env:TOPIAFORGE_TEST_HOLD,
  [IO.FileMode]::Open,
  [IO.FileAccess]::Read,
  [IO.FileShare]::Read)
try {
  [Console]::Out.WriteLine('held')
  [Console]::Out.Flush()
  while (-not [IO.File]::Exists($env:TOPIAFORGE_TEST_RELEASE)) {
    [System.Threading.Thread]::Sleep(10)
  }
}
finally { $file.Dispose() }
''';

Future<Future<void> Function()?> _openWithReadSharing(File file) async {
  final flags = Directory.systemTemp.createTempSync('tf-held-');
  final releaseFlag = File(p.join(flags.path, 'release'));
  final process = await Process.start(
    p.join(
      Platform.environment['SystemRoot'] ?? r'C:\Windows',
      'System32',
      'WindowsPowerShell',
      'v1.0',
      'powershell.exe',
    ),
    ['-NoLogo', '-NoProfile', '-NonInteractive', '-Command', _holder],
    environment: {
      'TOPIAFORGE_TEST_HOLD': file.path,
      'TOPIAFORGE_TEST_RELEASE': releaseFlag.path,
    },
  );
  final errors = process.stderr.transform(utf8.decoder).join();
  Future<void> release() async {
    releaseFlag.writeAsStringSync('release');
    await process.exitCode.timeout(
      const Duration(seconds: 30),
      onTimeout: () {
        process.kill();
        return process.exitCode;
      },
    );
    flags.deleteSync(recursive: true);
  }

  try {
    final line = await process.stdout
        .transform(utf8.decoder)
        .transform(const LineSplitter())
        .first
        .timeout(const Duration(seconds: 60));
    if (line != 'held') throw StateError('The holder answered "$line".');
  } on Object {
    process.kill();
    await process.exitCode;
    flags.deleteSync(recursive: true);
    throw StateError('The file could not be held: ${await errors}');
  }
  return release;
}

/// Denies changes in [file]'s folder; null, with the mode put back, where
/// [file] can still be moved, as for root.
Future<Future<void> Function()?> _freezeFolder(File file) => _withMode(
  file.parent,
  '555',
  holds: () {
    final probe = '${file.path}.probe';
    try {
      file.renameSync(probe);
    } on FileSystemException {
      return true;
    }
    File(probe).renameSync(file.path);
    return false;
  },
);

/// Gives [folder] [mode] and returns what puts the old mode back; null, with
/// the old mode back, where `chmod` fails, the platform is Windows, or
/// [holds] says the mode does not have its effect.
Future<Future<void> Function()?> _withMode(
  Directory folder,
  String mode, {
  required bool Function() holds,
}) async {
  if (Platform.isWindows) return null;
  final previous = (folder.statSync().mode & 0x1FF).toRadixString(8);
  Future<void> restore() async {
    await Process.run('chmod', [previous, folder.path]);
  }

  if ((await Process.run('chmod', [mode, folder.path])).exitCode != 0) {
    await restore();
    return null;
  }
  if (!holds()) {
    await restore();
    return null;
  }
  return restore;
}
