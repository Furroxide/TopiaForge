import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  test('batch search falls back before rejecting a compatible root', () async {
    final fixture = await _GlobalInboxFixture.create();
    addTearDown(fixture.dispose);
    fixture.writePackage(
      'a.root',
      '2.0.0',
      dependencies: const {'b.provider': '>=2.0.0'},
    );
    fixture.writePackage(
      'a.root',
      '1.0.0',
      dependencies: const {'b.provider': '<2.0.0'},
    );
    fixture.writePackage('b.provider', '2.0.0');
    fixture.writePackage('b.provider', '1.0.0');
    fixture.writePackage(
      'c.consumer',
      '1.0.0',
      dependencies: const {'b.provider': '<2.0.0'},
    );

    final outcome = await fixture.repository.installInboxPackages(
      fixture.install,
    );

    expect(outcome.status, PackageInboxInstallStatus.success);
    expect(outcome.installedCount, 3);
    expect(outcome.supersededCount, 2);
    expect(outcome.consumedCount, 5);
    expect(fixture.marker('a.root', '1.0.0'), 'a.root@1.0.0');
    expect(fixture.marker('b.provider', '1.0.0'), 'b.provider@1.0.0');
    expect(fixture.marker('c.consumer', '1.0.0'), 'c.consumer@1.0.0');
    expect(fixture.installed('a.root', '2.0.0').existsSync(), isFalse);
    expect(fixture.installed('b.provider', '2.0.0').existsSync(), isFalse);
  });

  test('partial batch keeps a maximum compatible root subset', () async {
    final fixture = await _GlobalInboxFixture.create();
    addTearDown(fixture.dispose);
    fixture.writePackage(
      'a.root',
      '1.0.0',
      conflicts: const [
        {'id': 'b.bridge'},
      ],
    );
    fixture.writePackage(
      'b.bridge',
      '1.0.0',
      conflicts: const [
        {'id': 'c.root'},
      ],
    );
    fixture.writePackage('c.root', '1.0.0');

    final outcome = await fixture.repository.installInboxPackages(
      fixture.install,
    );

    expect(outcome.status, PackageInboxInstallStatus.partial);
    expect(outcome.installedCount, 2);
    expect(outcome.invalidCount, 1);
    expect(outcome.consumedCount, 2);
    expect(fixture.marker('a.root', '1.0.0'), 'a.root@1.0.0');
    expect(fixture.marker('c.root', '1.0.0'), 'c.root@1.0.0');
    expect(fixture.installed('b.bridge', '1.0.0').existsSync(), isFalse);
    expect(
      File(
        p.join(fixture.inbox.path, 'b.bridge-1.0.0.topiaforgemod'),
      ).existsSync(),
      isTrue,
    );
  });
}

class _GlobalInboxFixture {
  const _GlobalInboxFixture(
    this.root,
    this.game,
    this.inbox,
    this.repository,
    this.install,
  );

  final Directory root;
  final Directory game;
  final Directory inbox;
  final LocalLauncherRepository repository;
  final GameInstall install;

  static Future<_GlobalInboxFixture> create() async {
    final root = Directory.systemTemp.createTempSync('global-inbox-');
    final game = Directory(p.join(root.path, 'Robotopia'))..createSync();
    File(p.join(game.path, 'Robotopia.exe')).writeAsStringSync('');
    File(
      p.join(game.path, 'installed-build.json'),
    ).writeAsStringSync('{"id":2227}');
    final managed = Directory(p.join(game.path, 'Robotopia_Data', 'Managed'))
      ..createSync(recursive: true);
    File(p.join(managed.path, 'UnityEngine.dll')).writeAsStringSync('');
    final inbox = Directory(
      p.join(game.path, 'BepInEx', 'TopiaForge', 'package-inbox'),
    )..createSync(recursive: true);
    final repository = LocalLauncherRepository(
      dataRoot: p.join(root.path, 'data'),
      repositoryRoot: root.path,
      packageMetadataValidator: (_) async => const [],
    );
    final install = await repository.selectGameDirectory(game.path);
    await repository.savePackageSources(const [
      PackageSource(
        id: 'io.github.furroxide.topiaforge.local',
        name: 'Bundled Local Packages',
        url: '.',
        enabled: false,
        builtIn: true,
      ),
      PackageSource(
        id: ModRegistryFormat.officialSourceId,
        name: ModRegistryFormat.officialSourceName,
        url: ModRegistryFormat.officialRegistryUrl,
        enabled: false,
        builtIn: true,
      ),
    ]);
    return _GlobalInboxFixture(root, game, inbox, repository, install);
  }

  void writePackage(
    String id,
    String version, {
    Map<String, String> dependencies = const {},
    List<Map<String, Object?>> conflicts = const [],
  }) {
    final assembly = id.replaceAll('.', '_');
    final manifest = <String, Object?>{
      'schemaVersion': 4,
      'name': id,
      'displayName': id,
      'version': version,
      'author': {'name': 'TopiaForge'},
      'entryAssembly': '$assembly.dll',
      'entryType': '$assembly.Entry',
      'supportedGameVersionRange': '*',
      'supportedLoaderVersionRange': '*',
      'supportedSdkVersionRange': '*',
      if (dependencies.isNotEmpty) 'dependencies': dependencies,
      if (conflicts.isNotEmpty) 'conflicts': conflicts,
    };
    final archive = Archive()
      ..addFile(ArchiveFile.string('topiaforge.mod.json', jsonEncode(manifest)))
      ..addFile(ArchiveFile.string('$assembly.dll', 'managed fixture'))
      ..addFile(ArchiveFile.string('marker.txt', '$id@$version'));
    File(
      p.join(inbox.path, '$id-$version.topiaforgemod'),
    ).writeAsBytesSync(ZipEncoder().encode(archive), flush: true);
  }

  Directory installed(String id, String version) => Directory(
    p.join(game.path, 'BepInEx', 'TopiaForge', 'packages', id, version),
  );

  String marker(String id, String version) => File(
    p.join(installed(id, version).path, 'marker.txt'),
  ).readAsStringSync();

  Future<void> dispose() async {
    await repository.dispose();
    if (root.existsSync()) root.deleteSync(recursive: true);
  }
}
