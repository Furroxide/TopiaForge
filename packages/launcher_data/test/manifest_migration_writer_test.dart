import 'dart:convert';
import 'dart:io';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory root;
  late File manifest;
  setUp(() {
    root = Directory.systemTemp.createTempSync('migration-writer-');
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
          'x-values': [null, true, 1.5],
        }),
      );
  });
  tearDown(() {
    root.deleteSync(recursive: true);
  });
  ManifestMigrationPlan plan(
    ManifestMigrationSnapshot snapshot, {
    bool stub = false,
  }) => const ManifestMigrationPlanner().plan(
    snapshot.sourceText,
    sourceLabel: snapshot.path,
    mode: stub ? ManifestMigrationMode.stub : ManifestMigrationMode.automatic,
  );
  test('atomic migration commits planned bytes and no backup window', () async {
    const writer = ManifestMigrationWriter();
    final snapshot = await writer.prepare(manifest.path);
    final migration = plan(snapshot);
    final result = await writer.commit(snapshot, migration);
    expect(manifest.readAsStringSync(), migration.outputText);
    expect(result.disposition, ManifestMigrationDisposition.migrated);
    expect(result.beforeSha256, isNot(result.afterSha256));
    expect(root.listSync().map((v) => p.basename(v.path)), [
      'topiaforge.mod.json',
    ]);
  });
  test('source changes after preparation are preserved', () async {
    const writer = ManifestMigrationWriter();
    final snapshot = await writer.prepare(manifest.path);
    manifest.writeAsStringSync('author edit');
    await expectLater(
      writer.commit(snapshot, plan(snapshot)),
      throwsStateError,
    );
    expect(manifest.readAsStringSync(), 'author edit');
    expect(root.listSync(), hasLength(1));
  });
  test('plan must belong to exact source snapshot', () async {
    const writer = ManifestMigrationWriter();
    final snapshot = await writer.prepare(manifest.path);
    final other = const ManifestMigrationPlanner().plan(
      snapshot.sourceText.replaceFirst('Example', 'Other'),
      sourceLabel: snapshot.path,
    );
    final before = manifest.readAsBytesSync();
    await expectLater(writer.commit(snapshot, other), throwsStateError);
    expect(manifest.readAsBytesSync(), before);
  });
  test(
    'replacement failure retains original and removes only owned staging',
    () async {
      final writer = ManifestMigrationWriter(
        replaceFile: (staging, target) async {
          expect(target.readAsStringSync(), contains('"schemaVersion":5'));
          expect(staging.readAsStringSync(), contains('"schemaVersion": 6'));
          throw const FileSystemException('injected replace refusal');
        },
      );
      final snapshot = await writer.prepare(manifest.path);
      final before = manifest.readAsBytesSync();
      await expectLater(
        writer.commit(snapshot, plan(snapshot)),
        throwsA(isA<FileSystemException>()),
      );
      expect(manifest.readAsBytesSync(), before);
      expect(root.listSync(), hasLength(1));
    },
  );
  test(
    'invalid stub can commit only as explicitly requested invalid output',
    () async {
      final json = jsonDecode(manifest.readAsStringSync()) as Map;
      json['worldGamemodes'] = [
        {'id': 'example.mod.mode', 'name': 'Mode'},
      ];
      manifest.writeAsStringSync(jsonEncode(json));
      const writer = ManifestMigrationWriter();
      final snapshot = await writer.prepare(manifest.path);
      await expectLater(
        writer.commit(snapshot, plan(snapshot)),
        throwsStateError,
      );
      final migration = plan(snapshot, stub: true);
      final result = await writer.commit(snapshot, migration);
      expect(result.disposition, ManifestMigrationDisposition.invalidStub);
      expect(
        () => ModManifest.fromJson(
          (jsonDecode(manifest.readAsStringSync()) as Map)
              .cast<String, Object?>(),
        ),
        throwsFormatException,
      );
    },
  );
  test('nonordinary source cannot be prepared', () async {
    manifest.deleteSync();
    Directory(manifest.path).createSync();
    await expectLater(
      const ManifestMigrationWriter().prepare(manifest.path),
      throwsA(anything),
    );
  });
}
