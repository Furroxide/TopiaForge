import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:test/test.dart';

/// Makes [file] unreadable until the test ends, the way another program can
/// hold it: on Windows a child process opens it with delete sharing only, so
/// it can still be replaced but not read; elsewhere its mode denies reading.
/// Returns the release, which the test may also call itself, or null after
/// marking the test skipped where that cannot be done, as when tests run as
/// root, which reads anything.
Future<Future<void> Function()?> holdUnreadable(File file) => _hold(
  Platform.isWindows
      ? _holdOnWindows(file, access: 'ReadWrite', share: 'Delete')
      : _denyReading(file),
  'This account can read any file.',
);

/// Keeps [file] where it is until the test ends, the way another program can
/// hold it open without delete sharing: it can still be read, but neither
/// moved nor deleted. On Windows a child process opens it with read sharing
/// only; elsewhere its folder's mode denies changes. Returns the release as
/// [holdUnreadable] does, or null after marking the test skipped where that
/// cannot be done.
Future<Future<void> Function()?> holdInPlace(File file) => _hold(
  Platform.isWindows
      ? _holdOnWindows(file, access: 'Read', share: 'Read')
      : _freezeFolder(file),
  'This account can move any file.',
);

Future<Future<void> Function()?> _hold(
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

/// Holds the file with the access and sharing named by
/// `TOPIAFORGE_TEST_ACCESS` and `TOPIAFORGE_TEST_SHARE` until the release file
/// appears, as `manifest_migration_atomic_test.dart` holds its manifest.
const _holder = r'''
$ErrorActionPreference = 'Stop'
$file = [IO.File]::Open(
  $env:TOPIAFORGE_TEST_HOLD,
  [IO.FileMode]::Open,
  [IO.FileAccess]$env:TOPIAFORGE_TEST_ACCESS,
  [IO.FileShare]$env:TOPIAFORGE_TEST_SHARE)
try {
  [Console]::Out.WriteLine('held')
  [Console]::Out.Flush()
  while (-not [IO.File]::Exists($env:TOPIAFORGE_TEST_RELEASE)) {
    [System.Threading.Thread]::Sleep(10)
  }
}
finally { $file.Dispose() }
''';

Future<Future<void> Function()?> _holdOnWindows(
  File file, {
  required String access,
  required String share,
}) async {
  final flags = Directory.systemTemp.createTempSync('tf-mp-hold-');
  final releaseFlag = File('${flags.path}${Platform.pathSeparator}release');
  final process = await Process.start(
    '${Platform.environment['SystemRoot']}\\System32\\WindowsPowerShell\\'
    'v1.0\\powershell.exe',
    ['-NoLogo', '-NoProfile', '-NonInteractive', '-Command', _holder],
    environment: {
      'TOPIAFORGE_TEST_HOLD': file.path,
      'TOPIAFORGE_TEST_ACCESS': access,
      'TOPIAFORGE_TEST_SHARE': share,
      'TOPIAFORGE_TEST_RELEASE': releaseFlag.path,
    },
  );
  final errors = process.stderr.transform(utf8.decoder).join();
  Future<void> finish() async {
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
  return finish;
}

Future<Future<void> Function()?> _denyReading(File file) async {
  if ((await Process.run('chmod', ['000', file.path])).exitCode != 0) {
    return null;
  }
  Future<void> restore() async {
    await Process.run('chmod', ['600', file.path]);
  }

  try {
    await file.readAsBytes();
  } on FileSystemException {
    return restore;
  }
  await restore();
  return null;
}

/// Denies changes in [file]'s folder, so the file can be read but neither
/// moved nor deleted; null, with nothing changed, where that does not hold.
Future<Future<void> Function()?> _freezeFolder(File file) async {
  final folder = file.parent;
  final mode = (folder.statSync().mode & 0x1FF).toRadixString(8);
  if ((await Process.run('chmod', ['555', folder.path])).exitCode != 0) {
    return null;
  }
  Future<void> restore() async {
    await Process.run('chmod', [mode, folder.path]);
  }

  final probe = '${file.path}.probe';
  try {
    file.renameSync(probe);
  } on FileSystemException {
    return restore;
  }
  File(probe).renameSync(file.path);
  await restore();
  return null;
}
