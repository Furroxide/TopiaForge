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
    temp = await Directory.systemTemp.createTemp('robotopia-sources-test-');
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

  void writeSources(List<Map<String, Object?>> sources) {
    File(
      p.join(dataRoot.path, 'package_sources.json'),
    ).writeAsStringSync(jsonEncode({'sources': sources}));
  }

  test('built-in sources are reconciled and keep the persisted flag', () async {
    writeSources([
      {
        'id': 'robotopia.local',
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
      (source) => source.id == 'robotopia.local',
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

  test('official registry is appended to pre-upgrade source files', () async {
    writeSources([
      {
        'id': 'robotopia.local',
        'name': 'Bundled Local Packages',
        'url': Uri.file(p.join(repoRoot.path, 'dist')).toString(),
        'enabled': true,
        'builtIn': true,
      },
    ]);

    final snapshot = await repository().loadSnapshot();

    expect(
      snapshot.packageSources.map((source) => source.id),
      containsAll(['robotopia.local', ModRegistryFormat.officialSourceId]),
    );
  });

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

    expect(mod.sourceId, 'robotopia.local');
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
  });
}

Map<String, Object?> _localSourceJson(Directory repoRoot) => {
  'id': 'robotopia.local',
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
        'robotopia.mod.json',
        jsonEncode({
          'schemaVersion': 2,
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
    p.join(directory.path, '$id-$version.robotopiamod'),
  ).writeAsBytesSync(ZipEncoder().encode(archive));
}
