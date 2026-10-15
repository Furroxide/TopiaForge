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
Future<Future<void> Function()?> holdUnreadable(File file) async {
  final release = Platform.isWindows
      ? await _holdOnWindows(file)
      : await _denyReading(file);
  if (release == null) {
    markTestSkipped('This account can read any file.');
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

/// Holds the file until the release file appears, as
/// `manifest_migration_atomic_test.dart` holds its manifest.
const _holder = r'''
$ErrorActionPreference = 'Stop'
$file = [IO.File]::Open(
  $env:TOPIAFORGE_TEST_HOLD,
  [IO.FileMode]::Open,
  [IO.FileAccess]::ReadWrite,
  [IO.FileShare]::Delete)
try {
  [Console]::Out.WriteLine('held')
  [Console]::Out.Flush()
  while (-not [IO.File]::Exists($env:TOPIAFORGE_TEST_RELEASE)) {
    [System.Threading.Thread]::Sleep(10)
  }
}
finally { $file.Dispose() }
''';

Future<Future<void> Function()?> _holdOnWindows(File file) async {
  final flags = Directory.systemTemp.createTempSync('tf-mp-hold-');
  final releaseFlag = File('${flags.path}${Platform.pathSeparator}release');
  final process = await Process.start(
    '${Platform.environment['SystemRoot']}\\System32\\WindowsPowerShell\\'
    'v1.0\\powershell.exe',
    ['-NoLogo', '-NoProfile', '-NonInteractive', '-Command', _holder],
    environment: {
      'TOPIAFORGE_TEST_HOLD': file.path,
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
