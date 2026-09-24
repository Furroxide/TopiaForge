import 'dart:convert';
import 'dart:ffi';
import 'dart:io';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  group('Windows migration filesystem names', () {
    late Directory root;
    late File manifest;
    late String shortRoot;
    setUp(() {
      root = Directory(
        Directory.systemTemp
            .createTempSync('migration-long-name-')
            .resolveSymbolicLinksSync(),
      );
      shortRoot = _shortPath(root.path);
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
    tearDown(() => root.deleteSync(recursive: true));
    bool supported() {
      if (p.equals(shortRoot, root.path)) {
        markTestSkipped('This volume does not assign a Windows short name.');
        return false;
      }
      return true;
    }

    test(
      'short input becomes one canonical snapshot and commit destination',
      () async {
        if (!supported()) return;
        const writer = ManifestMigrationWriter();
        final snapshot = await writer.prepare(
          p.join(shortRoot, 'topiaforge.mod.json'),
        );
        expect(snapshot.path, manifest.resolveSymbolicLinksSync());
        final plan = const ManifestMigrationPlanner().plan(
          snapshot.sourceText,
          sourceLabel: snapshot.path,
        );
        final result = await writer.commit(snapshot, plan);
        expect(result.path, snapshot.path);
        expect(
          (jsonDecode(manifest.readAsStringSync()) as Map)['schemaVersion'],
          6,
        );
        expect(root.listSync().map((e) => p.basename(e.path)), [
          'topiaforge.mod.json',
        ]);
      },
    );
    test(
      'short OS temporary root supports atomic migration lease files',
      () async {
        if (!supported()) return;
        final child = await Process.run(
          Platform.resolvedExecutable,
          [
            'run',
            p.join(
              Directory.current.path,
              'test',
              'manifest_migration_writer_child.dart',
            ),
            manifest.path,
            '--no-pause',
          ],
          environment: {'TEMP': shortRoot, 'TMP': shortRoot},
        );
        expect(child.exitCode, 0, reason: child.stderr.toString());
        expect(
          (jsonDecode(manifest.readAsStringSync()) as Map)['schemaVersion'],
          6,
        );
      },
    );
    test(
      'long and short source spellings compete for the same OS lease',
      () async {
        if (!supported()) return;
        final child = await Process.start(Platform.resolvedExecutable, [
          'run',
          p.join(
            Directory.current.path,
            'test',
            'manifest_migration_writer_child.dart',
          ),
          p.join(shortRoot, 'topiaforge.mod.json'),
        ]);
        final errors = child.stderr.transform(utf8.decoder).join();
        var ready = false;
        try {
          final line = await child.stdout
              .transform(utf8.decoder)
              .transform(const LineSplitter())
              .first
              .timeout(const Duration(seconds: 15));
          expect(line, 'ready');
          ready = true;
          const writer = ManifestMigrationWriter();
          final snapshot = await writer.prepare(manifest.path);
          final plan = const ManifestMigrationPlanner().plan(
            snapshot.sourceText,
            sourceLabel: snapshot.path,
          );
          await expectLater(writer.commit(snapshot, plan), throwsStateError);
          expect(manifest.readAsStringSync(), snapshot.sourceText);
        } finally {
          if (ready) {
            child.stdin.writeln('finish');
            await child.stdin.close();
          }
          final code = await child.exitCode.timeout(
            const Duration(seconds: 10),
            onTimeout: () async {
              child.kill();
              return child.exitCode;
            },
          );
          expect(code, 0, reason: await errors);
        }
        expect(
          (jsonDecode(manifest.readAsStringSync()) as Map)['schemaVersion'],
          6,
        );
      },
    );
  }, skip: !Platform.isWindows);
}

// Primary Win32 API: GetShortPathNameW. A filesystem may decline to assign an
// 8.3 name, so absence is a platform capability skip rather than fabricated input.
String _shortPath(String path) {
  final api = DynamicLibrary.open('kernel32.dll');
  final getHeap = api
      .lookupFunction<Pointer<Void> Function(), Pointer<Void> Function()>(
        'GetProcessHeap',
      );
  final alloc = api
      .lookupFunction<
        Pointer<Void> Function(Pointer<Void>, Uint32, UintPtr),
        Pointer<Void> Function(Pointer<Void>, int, int)
      >('HeapAlloc');
  final free = api
      .lookupFunction<
        Int32 Function(Pointer<Void>, Uint32, Pointer<Void>),
        int Function(Pointer<Void>, int, Pointer<Void>)
      >('HeapFree');
  final query = api
      .lookupFunction<
        Uint32 Function(Pointer<Uint16>, Pointer<Uint16>, Uint32),
        int Function(Pointer<Uint16>, Pointer<Uint16>, int)
      >('GetShortPathNameW');
  final heap = getHeap();
  final input = alloc(heap, 8, (path.length + 1) * 2).cast<Uint16>();
  final output = alloc(heap, 8, 32768 * 2).cast<Uint16>();
  try {
    if (input == nullptr || output == nullptr) {
      throw StateError('Short-path test allocation failed.');
    }
    input.asTypedList(path.length).setAll(0, path.codeUnits);
    final count = query(input, output, 32768);
    if (count == 0 || count >= 32768) {
      throw StateError('Short-path OS query failed.');
    }
    return String.fromCharCodes(output.asTypedList(count));
  } finally {
    if (input != nullptr) free(heap, 0, input.cast());
    if (output != nullptr) free(heap, 0, output.cast());
  }
}
