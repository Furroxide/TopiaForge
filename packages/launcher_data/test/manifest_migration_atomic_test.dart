import 'dart:convert';
import 'dart:io';
import 'dart:isolate';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory root;
  late File manifest;
  setUp(() {
    root = Directory.systemTemp.createTempSync('migration-native-');
    manifest = File(p.join(root.path, 'topiaforge.mod.json'))
      ..writeAsStringSync(
        jsonEncode({
          'schemaVersion': 5,
          'name': 'example.mod',
          'displayName': 'Example',
          'version': '0.1.0',
          'author': {'name': 'Author'},
          'entryAssembly': 'Example.dll',
          'entryType': 'Example.Mod',
          'supportedGameVersionRange': '*',
          'supportedLoaderVersionRange': '*',
          'supportedSdkVersionRange': '*',
        }),
      );
  });
  tearDown(() {
    root.deleteSync(recursive: true);
  });
  const writer = ManifestMigrationWriter();
  ManifestMigrationPlan plan(ManifestMigrationSnapshot s) =>
      const ManifestMigrationPlanner().plan(s.sourceText, sourceLabel: s.path);
  Future<void> finish(
    Process process, {
    required bool resume,
    bool requireSuccess = true,
  }) async {
    try {
      if (resume) {
        process.stdin.writeln('finish');
        await process.stdin.close();
      }
      final code = await process.exitCode.timeout(const Duration(seconds: 10));
      if (requireSuccess) expect(code, 0);
    } on Object {
      process.kill();
      await process.exitCode;
      rethrow;
    }
  }

  test('OS lease rejects a competing migration in another process', () async {
    final library = await Isolate.resolvePackageUri(
      Uri.parse('package:launcher_data/launcher_data.dart'),
    );
    final packageRoot = File.fromUri(library!).parent.parent.path;
    final process = await Process.start(Platform.resolvedExecutable, [
      'run',
      p.join(packageRoot, 'test', 'manifest_migration_writer_child.dart'),
      manifest.path,
    ], workingDirectory: packageRoot);
    final errors = process.stderr.transform(utf8.decoder).join();
    var ready = false;
    try {
      expect(
        await process.stdout
            .transform(utf8.decoder)
            .transform(const LineSplitter())
            .first
            .timeout(const Duration(seconds: 15)),
        'ready',
      );
      ready = true;
      final snapshot = await writer.prepare(manifest.path);
      await expectLater(
        writer.commit(snapshot, plan(snapshot)),
        throwsStateError,
      );
      expect(manifest.readAsStringSync(), snapshot.sourceText);
    } finally {
      await finish(process, resume: ready, requireSuccess: ready);
    }
    expect(await errors, isEmpty);
    expect(
      (jsonDecode(manifest.readAsStringSync()) as Map)['schemaVersion'],
      6,
    );
    expect(root.listSync(), hasLength(1));
  });
  test(
    'Windows sharing-denied replacement leaves original whole',
    () async {
      final release = File(p.join(root.path, 'release-child'));
      final process = await Process.start(
        'powershell.exe',
        [
          '-NoLogo',
          '-NoProfile',
          '-NonInteractive',
          '-Command',
          r"""
$ErrorActionPreference = 'Stop'
$file = [IO.File]::Open($env:TOPIAFORGE_TEST_MANIFEST, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
try {
  [Console]::Out.WriteLine('ready'); [Console]::Out.Flush()
  while (-not [IO.File]::Exists($env:TOPIAFORGE_TEST_RELEASE)) { Start-Sleep -Milliseconds 10 }
}
finally { $file.Dispose() }
""",
        ],
        environment: {
          'TOPIAFORGE_TEST_MANIFEST': manifest.path,
          'TOPIAFORGE_TEST_RELEASE': release.path,
        },
      );
      final errors = process.stderr.transform(utf8.decoder).join();
      final original = manifest.readAsBytesSync();
      try {
        expect(
          await process.stdout
              .transform(utf8.decoder)
              .transform(const LineSplitter())
              .first
              .timeout(const Duration(seconds: 10)),
          'ready',
        );
        final snapshot = await writer.prepare(manifest.path);
        await expectLater(
          writer.commit(snapshot, plan(snapshot)),
          throwsA(isA<FileSystemException>()),
        );
        expect(manifest.readAsBytesSync(), original);
        expect(root.listSync(), hasLength(1));
      } finally {
        release.writeAsStringSync('finish');
        await finish(process, resume: false);
        release.deleteSync();
      }
      expect(await errors, isEmpty);
      final snapshot = await writer.prepare(manifest.path);
      await writer.commit(snapshot, plan(snapshot));
    },
    skip: !Platform.isWindows ? 'Windows sharing semantics.' : false,
  );
  for (final ancestor in [false, true]) {
    test(
      'preparation rejects a linked ${ancestor ? 'ancestor' : 'source'} before canonicalization',
      () async {
        final link = Link(
          p.join(root.path, ancestor ? 'linked-root' : 'linked.json'),
        );
        try {
          link.createSync(ancestor ? root.path : manifest.path);
        } on FileSystemException {
          markTestSkipped('Host does not permit symbolic-link creation.');
          return;
        }
        final before = manifest.readAsBytesSync();
        try {
          await expectLater(
            writer.prepare(
              ancestor ? p.join(link.path, 'topiaforge.mod.json') : link.path,
            ),
            throwsStateError,
          );
          expect(manifest.readAsBytesSync(), before);
        } finally {
          link.deleteSync();
        }
      },
    );
  }
  test('link substitution after preparation never writes through it', () async {
    final snapshot = await writer.prepare(manifest.path);
    final outside = File(p.join(root.path, 'outside.json'))
      ..writeAsStringSync('preserve me');
    final testLink = Link(p.join(root.path, 'test-link'));
    try {
      testLink.createSync(outside.path);
    } on FileSystemException {
      markTestSkipped('Host does not permit symbolic-link creation.');
      return;
    }
    testLink.deleteSync();
    manifest.deleteSync();
    Link(manifest.path).createSync(outside.path);
    await expectLater(
      writer.commit(snapshot, plan(snapshot)),
      throwsStateError,
    );
    expect(outside.readAsStringSync(), 'preserve me');
    expect(
      FileSystemEntity.typeSync(manifest.path, followLinks: false),
      FileSystemEntityType.link,
    );
  });
  test('unchanged current V6 plan cannot create transaction output', () async {
    final raw = jsonDecode(manifest.readAsStringSync()) as Map;
    raw['schemaVersion'] = 6;
    manifest.writeAsStringSync(jsonEncode(raw));
    final snapshot = await writer.prepare(manifest.path);
    final current = plan(snapshot);
    expect(current.disposition, ManifestMigrationDisposition.unchanged);
    await expectLater(writer.commit(snapshot, current), throwsStateError);
    expect(manifest.readAsStringSync(), snapshot.sourceText);
    expect(root.listSync(), hasLength(1));
  });
}
