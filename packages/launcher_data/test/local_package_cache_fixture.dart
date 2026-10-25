import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'multiplayer_join_content_fixtures.dart';

final class LocalCacheFixture {
  LocalCacheFixture._(this.root, this.game, this.data);

  final Directory root;
  final Directory game;
  final Directory data;
  late LocalLauncherRepository repository;
  late GameInstall install;

  static Future<LocalCacheFixture> create() async {
    final root = Directory.systemTemp.createTempSync('tf-local-cache-');
    addTearDown(() => root.deleteSync(recursive: true));
    final fixture = LocalCacheFixture._(
      root,
      createTestGame(root),
      Directory(p.join(root.path, 'data')),
    );
    fixture.repository = fixture.open();
    fixture.install = await fixture.repository.selectGameDirectory(
      fixture.game.path,
    );
    await fixture.sources();
    return fixture;
  }

  LocalLauncherRepository open({
    PackageInstallCommitHook? hook,
    PackageMetadataValidator? validator,
  }) {
    final repository = LocalLauncherRepository(
      dataRoot: data.path,
      repositoryRoot: root.path,
      packageMetadataValidator: validator ?? (_) async => const [],
      packageInstallCommitHook: hook,
      gameRunningProbe: (_) async => false,
    );
    addTearDown(repository.dispose);
    return repository;
  }

  Directory get cache => Directory(p.join(data.path, 'package-cache'));
  File cached(TestArchive archive) =>
      File(p.join(cache.path, '${archive.sha256Hex}.topiaforgemod'));
  File receipt(TestArchive archive) => File(
    p.join(
      game.path,
      'BepInEx',
      'TopiaForge',
      'packages',
      archive.id,
      archive.version,
      'topiaforge.install.json',
    ),
  );
  List<File> get archives => cache.existsSync()
      ? cache
            .listSync()
            .whereType<File>()
            .where((file) => p.extension(file.path) == '.topiaforgemod')
            .toList()
      : [];

  Future<void> put(
    TestArchive archive, {
    LocalLauncherRepository? using,
  }) async {
    await (using ?? repository).installPackage(
      archive.writeTo(root).path,
      install,
    );
  }

  Future<void> sources([List<TestArchive> packages = const []]) async {
    final source = File(p.join(root.path, 'source.json'))
      ..writeAsStringSync(
        jsonEncode({
          'packages': {
            for (final archive in packages)
              archive.id: {
                'versions': {
                  archive.version: {
                    'manifest': archive.manifestJson,
                    'url': archive.writeTo(root).uri.toString(),
                    'zipSHA256': archive.sha256Hex,
                  },
                },
              },
          },
        }),
      );
    await repository.savePackageSources([
      const PackageSource(
        id: 'io.github.furroxide.topiaforge.local',
        name: 'Bundled Local Packages',
        url: '.',
        enabled: false,
        builtIn: true,
      ),
      const PackageSource(
        id: ModRegistryFormat.officialSourceId,
        name: ModRegistryFormat.officialSourceName,
        url: ModRegistryFormat.officialRegistryUrl,
        enabled: false,
        builtIn: true,
      ),
      if (packages.isNotEmpty)
        PackageSource(
          id: 'test.local',
          name: 'Local',
          url: source.uri.toString(),
        ),
    ]);
  }

  String get log =>
      File(p.join(data.path, 'logs', 'launcher.log')).readAsStringSync();
}
