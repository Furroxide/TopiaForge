import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/mod_registry_index_builder.dart';
import 'package:topiaforge/src/registry_entry_builder.dart';

/// History rows inline the manifest (and the archive size) of each mod's
/// newest older versions, so a launcher can install the exact version a
/// multiplayer server runs.
void main() {
  late Directory temp;

  setUp(() async {
    temp = await Directory.systemTemp.createTemp('topiaforge-history-test-');
  });

  tearDown(() async {
    if (await temp.exists()) await temp.delete(recursive: true);
  });

  Directory packages(List<String> versions) {
    final directory = Directory(p.join(temp.path, 'packages'))..createSync();
    for (final version in versions) {
      File(
        p.join(directory.path, 'cool.mod-$version.topiaforgemod'),
      ).writeAsBytesSync(_packageBytes(id: 'cool.mod', version: version));
    }
    return directory;
  }

  Future<List<Map<String, Object?>>> history(
    Directory source, {
    int? historyManifests,
    String entries = '',
  }) async {
    final output = Directory(p.join(temp.path, 'out'));
    await ModRegistryIndexBuilder(clock: () => DateTime.utc(2026)).build(
      historyManifests == null
          ? ModRegistryIndexConfig(
              packagesDirectory: source.path,
              outputDirectory: output.path,
              entriesDirectory: entries,
            )
          : ModRegistryIndexConfig(
              packagesDirectory: source.path,
              outputDirectory: output.path,
              entriesDirectory: entries,
              historyManifests: historyManifests,
            ),
    );
    final index =
        jsonDecode(File(p.join(output.path, 'index.json')).readAsStringSync())
            as Map<String, Object?>;
    final mods = (index['mods']! as List).cast<Map<String, Object?>>();
    return (mods.firstWhere((mod) {
              final manifest = mod['manifest']! as Map;
              return manifest['name'] == 'cool.mod';
            })['history']!
            as List)
        .cast<Map<String, Object?>>();
  }

  test(
    'the newest 20 older versions carry their manifest by default',
    () async {
      final versions = [for (var minor = 0; minor < 23; minor++) '1.$minor.0'];
      final rows = await history(packages(versions));
      expect(rows, hasLength(22));
      expect(rows.first['version'], '1.21.0');
      for (final (index, row) in rows.indexed) {
        final manifest = row['manifest'] as Map?;
        if (index < ModRegistryIndexConfig.defaultHistoryManifests) {
          expect(manifest!['version'], row['version']);
          expect(manifest['name'], 'cool.mod');
          final file = File(
            p.join(
              temp.path,
              'packages',
              'cool.mod-${row['version']}.topiaforgemod',
            ),
          );
          expect(row['packageBytes'], file.lengthSync());
          final ref = RegistryVersionRef.fromJson(row);
          expect(ref.manifest!.version, row['version']);
        } else {
          expect(manifest, isNull);
          expect(row.containsKey('packageBytes'), isFalse);
        }
      }
    },
  );

  test('zero history manifests writes the rows as before', () async {
    final rows = await history(
      packages(['1.0.0', '1.1.0', '1.2.0']),
      historyManifests: 0,
    );
    expect(rows.map((row) => row.keys.toSet()), [
      {'version', 'downloadUrl', 'packageSha256'},
      {'version', 'downloadUrl', 'packageSha256'},
    ]);
  });

  test('community history inlines its entry manifests', () async {
    final entries = Directory(p.join(temp.path, 'registry'))..createSync();
    RegistryEntryFile? entry;
    for (final version in ['1.0.0', '1.1.0', '1.2.0']) {
      entry =
          buildRegistryEntry(
            package: readModPackage(
              _packageBytes(id: 'other.mod', version: version),
            ),
            downloadUrl:
                'https://mods.example.com/other.mod-$version.topiaforgemod',
            existing: entry,
          ).entryFile ??
          entry;
    }
    // An entry may state each version's archive size; every row keeps it.
    final json = entry!.toJson();
    for (final row in (json['versions']! as List).cast<Map>()) {
      row['packageBytes'] = 4096;
    }
    File(
      p.join(entries.path, 'other.mod.json'),
    ).writeAsStringSync(jsonEncode(json));
    final output = Directory(p.join(temp.path, 'out'));
    await ModRegistryIndexBuilder(clock: () => DateTime.utc(2026)).build(
      ModRegistryIndexConfig(
        packagesDirectory: packages(['1.0.0']).path,
        outputDirectory: output.path,
        entriesDirectory: entries.path,
        historyManifests: 1,
      ),
    );
    final index =
        jsonDecode(File(p.join(output.path, 'index.json')).readAsStringSync())
            as Map<String, Object?>;
    final other = (index['mods']! as List)
        .cast<Map<String, Object?>>()
        .firstWhere((mod) => (mod['manifest']! as Map)['name'] == 'other.mod');
    final rows = (other['history']! as List).cast<Map<String, Object?>>();
    expect(rows.map((row) => row['version']), ['1.1.0', '1.0.0']);
    expect((rows.first['manifest']! as Map)['version'], '1.1.0');
    expect(rows.last.containsKey('manifest'), isFalse);
    expect(rows.map((row) => row['packageBytes']), [4096, 4096]);
  });

  test('the history manifest count is bounded', () async {
    for (final count in [
      -1,
      ModRegistryIndexConfig.maximumHistoryManifests + 1,
    ]) {
      await expectLater(
        history(packages(['1.0.0']), historyManifests: count),
        throwsArgumentError,
      );
      await Directory(p.join(temp.path, 'packages')).delete(recursive: true);
    }
  });
}

List<int> _packageBytes({required String id, required String version}) {
  final archive = Archive()
    ..addFile(
      ArchiveFile.string(
        'topiaforge.mod.json',
        jsonEncode({
          'schemaVersion': 6,
          'name': id,
          'displayName': id,
          'version': version,
          'author': {'name': 'Tester'},
          'license': 'MIT',
          'licenseFiles': ['LICENSE'],
          'entryAssembly': 'Mod.dll',
          'entryType': 'Test.Mod',
          'supportedGameVersionRange': '*',
          'supportedLoaderVersionRange': '*',
          'supportedSdkVersionRange': '*',
        }),
      ),
    )
    ..addFile(ArchiveFile.string('LICENSE', 'MIT test fixture license'))
    ..addFile(ArchiveFile.string('Mod.dll', 'dll-bytes-$id-$version'));
  return ZipEncoder().encode(archive);
}
