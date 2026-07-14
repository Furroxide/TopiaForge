import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

// Self-contained on purpose: the shared fixtures live inside
// launcher_data_test.dart's part files and are not importable.
void main() {
  late Directory temp;
  late Directory dataRoot;
  late Directory repoRoot;

  setUp(() async {
    temp = await Directory.systemTemp.createTemp('topiaforge-sources-test-');
    dataRoot = Directory(p.join(temp.path, 'data'))..createSync();
    repoRoot = Directory(p.join(temp.path, 'repo'))..createSync();
    Directory(p.join(repoRoot.path, 'dist')).createSync();
  });

  tearDown(() async {
    if (await temp.exists()) {
      await temp.delete(recursive: true);
    }
  });

  LocalLauncherRepository repository() {
    return LocalLauncherRepository(
      dataRoot: dataRoot.path,
      repositoryRoot: repoRoot.path,
      knownGamePath: p.join(temp.path, 'no-game-here'),
    );
  }

  void writeSources(
    List<Map<String, Object?>> sources, {
    int? formatVersion = 2,
  }) {
    final payload = <String, Object?>{'sources': sources};
    if (formatVersion != null) {
      payload['formatVersion'] = formatVersion;
    }
    File(
      p.join(dataRoot.path, 'package_sources.json'),
    ).writeAsStringSync(jsonEncode(payload));
  }

  for (final formatVersion in <int?>[null, 1]) {
    final label = formatVersion == null ? 'missing' : 'version 1';
    test('package source store rejects $label formatVersion', () async {
      writeSources([
        {
          'id':
              'robo'
              'topia.custom',
          'name': 'Retired',
          'url': temp.path,
        },
      ], formatVersion: formatVersion);

      await expectLater(repository().loadSnapshot(), throwsFormatException);
    });
  }

  test('package source store rejects retired ids at formatVersion 2', () async {
    writeSources([
      {
        'id':
            'robo'
            'topia.custom',
        'name': 'Retired',
        'url': temp.path,
      },
    ]);

    await expectLater(repository().loadSnapshot(), throwsStateError);
    await expectLater(
      repository().savePackageSources([
        PackageSource(
          id:
              'robo'
              'topia.custom',
          name: 'Retired',
          url: temp.path,
        ),
      ]),
      throwsStateError,
    );
  });

  test('package source store round-trips formatVersion 2', () async {
    final sources = [
      PackageSource.fromJson(_localSourceJson(repoRoot)),
      PackageSource.fromJson(_officialDisabledJson()),
      PackageSource(id: 'custom', name: 'Custom', url: temp.path),
    ];
    await repository().savePackageSources(sources);

    final stored =
        jsonDecode(
              File(
                p.join(dataRoot.path, 'package_sources.json'),
              ).readAsStringSync(),
            )
            as Map<String, Object?>;
    final snapshot = await repository().loadSnapshot();

    expect(stored['formatVersion'], 2);
    expect(
      snapshot.packageSources.map((source) => source.id),
      contains('custom'),
    );
  });

  test('built-in sources are reconciled and keep the persisted flag', () async {
    writeSources([
      {
        'id': 'io.github.furroxide.topiaforge.local',
        'name': 'Stale Name',
        'url': 'file:///somewhere/stale',
        'enabled': true,
        'builtIn': true,
      },
      {
        'id': ModRegistryFormat.officialSourceId,
        'name': 'Stale Registry',
        'url': 'https://stale.example/index.json',
        'enabled': false,
        'builtIn': true,
      },
    ]);

    final snapshot = await repository().loadSnapshot();
    final local = snapshot.packageSources.singleWhere(
      (source) => source.id == 'io.github.furroxide.topiaforge.local',
    );
    final official = snapshot.packageSources.singleWhere(
      (source) => source.id == ModRegistryFormat.officialSourceId,
    );

    expect(local.url, contains('dist'));
    expect(local.url, isNot(contains('stale')));
    expect(official.url, ModRegistryFormat.officialRegistryUrl);
    expect(official.enabled, isFalse, reason: 'player choice survives');
    expect(official.builtIn, isTrue);
  });

  test(
    'official registry is restored when required built-ins are missing',
    () async {
      writeSources([
        {
          'id': 'io.github.furroxide.topiaforge.local',
          'name': 'Bundled Local Packages',
          'url': Uri.file(p.join(repoRoot.path, 'dist')).toString(),
          'enabled': true,
          'builtIn': true,
        },
      ]);

      final snapshot = await repository().loadSnapshot();

      expect(
        snapshot.packageSources.map((source) => source.id),
        containsAll([
          'io.github.furroxide.topiaforge.local',
          ModRegistryFormat.officialSourceId,
        ]),
      );
    },
  );

  test('registry mods dedupe to the highest version across sources', () async {
    _writePackage(
      Directory(p.join(repoRoot.path, 'dist')),
      id: 'cool.mod',
      version: '1.0.0',
    );
    final userDir = Directory(p.join(temp.path, 'community'))..createSync();
    _writePackage(userDir, id: 'cool.mod', version: '1.1.0');
    writeSources([
      _localSourceJson(repoRoot),
      _officialDisabledJson(),
      {'id': 'community', 'name': 'Community', 'url': userDir.path},
    ]);

    final snapshot = await repository().loadSnapshot();
    final mod = snapshot.registryMods.singleWhere(
      (item) => item.manifest.id == 'cool.mod',
    );

    expect(mod.manifest.version, '1.1.0');
    expect(mod.sourceId, 'community');
  });

  test('version ties keep the earlier source (bundled local wins)', () async {
    _writePackage(
      Directory(p.join(repoRoot.path, 'dist')),
      id: 'cool.mod',
      version: '1.0.0',
    );
    final userDir = Directory(p.join(temp.path, 'community'))..createSync();
    _writePackage(userDir, id: 'cool.mod', version: '1.0.0');
    writeSources([
      _localSourceJson(repoRoot),
      _officialDisabledJson(),
      {'id': 'community', 'name': 'Community', 'url': userDir.path},
    ]);

    final snapshot = await repository().loadSnapshot();
    final mod = snapshot.registryMods.singleWhere(
      (item) => item.manifest.id == 'cool.mod',
    );

    expect(mod.sourceId, 'io.github.furroxide.topiaforge.local');
  });

  test('a dead document source degrades without failing the load', () async {
    _writePackage(
      Directory(p.join(repoRoot.path, 'dist')),
      id: 'cool.mod',
      version: '1.0.0',
    );
    writeSources([
      _localSourceJson(repoRoot),
      _officialDisabledJson(),
      {
        'id': 'dead',
        'name': 'Dead Source',
        'url': p.join(temp.path, 'missing-registry.json'),
      },
    ]);

    final snapshot = await repository().loadSnapshot();

    expect(
      snapshot.registryMods.map((mod) => mod.manifest.id),
      contains('cool.mod'),
      reason: 'healthy sources still load when one source is dead',
    );
    final healthy = snapshot.sourceStatuses.singleWhere(
      (status) => status.sourceId == 'io.github.furroxide.topiaforge.local',
    );
    final dead = snapshot.sourceStatuses.singleWhere(
      (status) => status.sourceId == 'dead',
    );
    expect(healthy.ok, isTrue);
    expect(healthy.modCount, 1);
    expect(dead.ok, isFalse);
    expect(dead.message, isNotEmpty);
  });

  test('malformed trust metadata fails only the offending source', () async {
    _writePackage(
      Directory(p.join(repoRoot.path, 'dist')),
      id: 'cool.mod',
      version: '1.0.0',
    );
    final unsafe = File(p.join(temp.path, 'unsafe-source.json'))
      ..writeAsStringSync(
        jsonEncode({
          'formatVersion': ModRegistryFormat.indexFormatVersion,
          'mods': [
            {
              'manifest': {
                'schemaVersion': 3,
                'name': 'unsafe.mod',
                'displayName': 'Unsafe',
                'version': '1.0.0',
                'entryAssembly': 'Unsafe.dll',
                'entryType': 'Unsafe.Mod',
              },
              'downloadUrl': 'https://packages.example/unsafe.topiaforgemod',
            },
          ],
        }),
      );
    writeSources([
      _localSourceJson(repoRoot),
      _officialDisabledJson(),
      {'id': 'unsafe', 'name': 'Unsafe', 'url': unsafe.path},
    ]);

    final snapshot = await repository().loadSnapshot();

    expect(snapshot.registryMods.map((mod) => mod.manifest.id), ['cool.mod']);
    final status = snapshot.sourceStatuses.singleWhere(
      (item) => item.sourceId == 'unsafe',
    );
    expect(status.ok, isFalse);
    expect(status.message, contains('SHA-256'));
  });

  for (final formatVersion in <int?>[null, 1]) {
    final label = formatVersion == null ? 'missing' : 'version 1';
    test('flat registry rejects $label formatVersion', () async {
      final sourceId = 'retired-${formatVersion ?? 'missing'}';
      final index = File(p.join(temp.path, '$sourceId.json'));
      final payload = <String, Object?>{'mods': <Object?>[]};
      if (formatVersion != null) {
        payload['formatVersion'] = formatVersion;
      }
      index.writeAsStringSync(jsonEncode(payload));
      writeSources([
        _localSourceJson(repoRoot),
        _officialDisabledJson(),
        {'id': sourceId, 'name': 'Retired', 'url': index.path},
      ]);

      final snapshot = await repository().loadSnapshot();
      final status = snapshot.sourceStatuses.singleWhere(
        (item) => item.sourceId == sourceId,
      );

      expect(status.ok, isFalse);
      expect(status.message, contains('formatVersion 2'));
    });
  }

  test('oversized source documents are rejected before decoding', () async {
    final oversized = File(p.join(temp.path, 'oversized-source.json'))
      ..writeAsBytesSync(List<int>.filled(16 * 1024 * 1024 + 1, 0x20));
    writeSources([
      _localSourceJson(repoRoot),
      _officialDisabledJson(),
      {'id': 'oversized', 'name': 'Oversized', 'url': oversized.path},
    ]);

    final snapshot = await repository().loadSnapshot();
    final status = snapshot.sourceStatuses.singleWhere(
      (item) => item.sourceId == 'oversized',
    );

    expect(status.ok, isFalse);
    expect(status.message, contains('16777216'));
  });
}

Map<String, Object?> _localSourceJson(Directory repoRoot) => {
  'id': 'io.github.furroxide.topiaforge.local',
  'name': 'Bundled Local Packages',
  'url': Uri.file(p.join(repoRoot.path, 'dist')).toString(),
  'enabled': true,
  'builtIn': true,
};

// Disabled so unit tests never touch the real network.
Map<String, Object?> _officialDisabledJson() => {
  'id': ModRegistryFormat.officialSourceId,
  'name': ModRegistryFormat.officialSourceName,
  'url': ModRegistryFormat.officialRegistryUrl,
  'enabled': false,
  'builtIn': true,
};

void _writePackage(
  Directory directory, {
  required String id,
  required String version,
}) {
  directory.createSync(recursive: true);
  final archive = Archive()
    ..addFile(
      ArchiveFile.string(
        'topiaforge.mod.json',
        jsonEncode({
          'schemaVersion': 3,
          'name': id,
          'displayName': id,
          'version': version,
          'author': {'name': 'Tester'},
          'entryAssembly': 'Mod.dll',
          'entryType': 'Test.Mod',
        }),
      ),
    )
    ..addFile(ArchiveFile.string('Mod.dll', 'dll'));
  File(
    p.join(directory.path, '$id-$version.topiaforgemod'),
  ).writeAsBytesSync(ZipEncoder().encode(archive));
}
