import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:crypto/crypto.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  test(
    'downloaded dependency manifest must match the planned source manifest',
    () async {
      final fixture = await _InstallVerificationFixture.create();
      addTearDown(fixture.dispose);
      final dependency = fixture.writePackage(
        'verified.dep',
        capabilities: const ['unsafe-native'],
      );
      final source = File(p.join(fixture.root.path, 'source.json'))
        ..writeAsStringSync(
          jsonEncode({
            'packages': {
              'verified.dep': {
                'versions': {
                  '1.0.0': {
                    'manifest': _manifest('verified.dep'),
                    'url': dependency.uri.toString(),
                    'zipSHA256': _sha(dependency),
                  },
                },
              },
            },
          }),
        );
      await fixture.repository.savePackageSources(
        _isolatedSources(
          PackageSource(
            id: 'verification.test',
            name: 'Verification Test',
            url: source.uri.toString(),
          ),
        ),
      );
      final rootPackage = fixture.writePackage(
        'consumer.mod',
        dependencies: const {'verified.dep': '*'},
      );

      await expectLater(
        fixture.repository.installPackage(rootPackage.path, fixture.install),
        throwsA(predicate((error) => error.toString().contains('TFPKG170'))),
      );

      expect(fixture.installedDirectory('verified.dep').existsSync(), isFalse);
      expect(fixture.installedDirectory('consumer.mod').existsSync(), isFalse);
    },
  );

  test(
    'packed contract lock metadata survives trusted dependency install',
    () async {
      final fixture = await _InstallVerificationFixture.create();
      addTearDown(fixture.dispose);
      const lockPath = 'topiaforge.multiplayer.lock.json';
      const lockBytes =
          '{"schemaVersion":2,"protocolVersion":"1.0.0","contracts":[]}';
      final lockHash = sha256.convert(utf8.encode(lockBytes)).toString();
      final dependency = fixture.writePackage(
        'contract.dep',
        manifestOverrides: {
          'multiplayer': {
            'mode': 'session',
            'presence': 'required',
            'protocol': {'version': '1.0.0'},
            'synchronizedFiles': [lockPath],
          },
          'hashes': {lockPath: lockHash},
        },
        extraEntries: const {lockPath: lockBytes},
      );
      final source = File(p.join(fixture.root.path, 'source.json'))
        ..writeAsStringSync(
          jsonEncode({
            'packages': {
              'contract.dep': {
                'versions': {
                  '1.0.0': {
                    'manifest': {
                      ..._manifest('contract.dep'),
                      'multiplayer': {
                        'mode': 'session',
                        'presence': 'required',
                        'protocol': {'version': '1.0.0'},
                        'synchronizedFiles': [lockPath],
                      },
                      'hashes': {lockPath: lockHash},
                    },
                    'url': dependency.uri.toString(),
                    'zipSHA256': _sha(dependency),
                  },
                },
              },
            },
          }),
        );
      await fixture.repository.savePackageSources(
        _isolatedSources(
          PackageSource(
            id: 'verification.contract',
            name: 'Verification Contract',
            url: source.uri.toString(),
          ),
        ),
      );
      final rootPackage = fixture.writePackage(
        'consumer.mod',
        dependencies: const {'contract.dep': '*'},
      );

      final installed = await fixture.repository.installPackage(
        rootPackage.path,
        fixture.install,
      );

      expect(
        installed.map((mod) => mod.id),
        containsAll(['contract.dep', 'consumer.mod']),
      );
      expect(fixture.installedDirectory('contract.dep').existsSync(), isTrue);
    },
  );

  test('post-commit log failure does not fail a durable install', () async {
    final fixture = await _InstallVerificationFixture.create();
    addTearDown(fixture.dispose);
    await fixture.repository.savePackageSources(_isolatedSources());
    final package = fixture.writePackage('logging.mod');
    fixture.blockLauncherLog();

    final installed = await fixture.repository.installPackage(
      package.path,
      fixture.install,
    );

    expect(installed.map((mod) => mod.id), contains('logging.mod'));
    expect(fixture.installedDirectory('logging.mod').existsSync(), isTrue);
  });

  test('post-durable launcher mutations survive a log failure', () async {
    final fixture = await _InstallVerificationFixture.create();
    addTearDown(fixture.dispose);
    await fixture.repository.savePackageSources(_isolatedSources());
    final package = fixture.writePackage('mutation.logging.mod');
    await fixture.repository.installPackage(package.path, fixture.install);
    fixture.blockLauncherLog();

    final selected = await fixture.repository.selectGameDirectory(
      fixture.game.path,
    );
    final sources = await fixture.repository.savePackageSources(
      _isolatedSources(),
    );
    final disabled = await fixture.repository.setModEnabled(
      fixture.install,
      'mutation.logging.mod',
      false,
    );
    final disabledAll = await fixture.repository.disableAllMods(
      fixture.install,
    );
    final uninstalled = await fixture.repository.uninstallMod(
      fixture.install,
      'mutation.logging.mod',
    );

    expect(selected.path, fixture.install.path);
    expect(sources, hasLength(2));
    expect(disabled.single.enabled, isFalse);
    expect(disabledAll.single.enabled, isFalse);
    expect(uninstalled, isEmpty);
  });

  test(
    'log failure cannot turn a completed inbox install into failure',
    () async {
      final fixture = await _InstallVerificationFixture.create();
      addTearDown(fixture.dispose);
      await fixture.repository.savePackageSources(_isolatedSources());
      final package = fixture.writePackage(
        'inbox.logging.mod',
        directory: fixture.inbox,
      );
      fixture.blockLauncherLog();

      final outcome = await fixture.repository.installInboxPackages(
        fixture.install,
      );

      expect(outcome.status, PackageInboxInstallStatus.success);
      expect(outcome.installedCount, 1);
      expect(outcome.consumedCount, 1);
      expect(package.existsSync(), isFalse);
      expect(
        fixture.installedDirectory('inbox.logging.mod').existsSync(),
        isTrue,
      );
    },
  );
}

class _InstallVerificationFixture {
  _InstallVerificationFixture._(
    this.root,
    this.game,
    this.data,
    this.repository,
    this.install,
  );

  static Future<_InstallVerificationFixture> create() async {
    final root = Directory.systemTemp.createTempSync(
      'package-install-verification-',
    );
    final game = Directory(p.join(root.path, 'Robotopia'))..createSync();
    File(p.join(game.path, 'Robotopia.exe')).writeAsStringSync('');
    File(
      p.join(game.path, 'installed-build.json'),
    ).writeAsStringSync('{"id":2309}');
    final managed = Directory(p.join(game.path, 'Robotopia_Data', 'Managed'))
      ..createSync(recursive: true);
    File(p.join(managed.path, 'UnityEngine.dll')).writeAsStringSync('');
    final data = Directory(p.join(root.path, 'data'));
    final repository = LocalLauncherRepository(
      dataRoot: data.path,
      repositoryRoot: root.path,
      packageMetadataValidator: (_) async => const [],
    );
    final install = await repository.selectGameDirectory(game.path);
    return _InstallVerificationFixture._(root, game, data, repository, install);
  }

  final Directory root;
  final Directory game;
  final Directory data;
  final LocalLauncherRepository repository;
  final GameInstall install;

  Directory get inbox =>
      Directory(p.join(game.path, 'BepInEx', 'TopiaForge', 'package-inbox'))
        ..createSync(recursive: true);

  Directory installedDirectory(String id) => Directory(
    p.join(game.path, 'BepInEx', 'TopiaForge', 'packages', id, '1.0.0'),
  );

  File writePackage(
    String id, {
    Directory? directory,
    Map<String, String> dependencies = const {},
    List<String> capabilities = const [],
    Map<String, Object?> manifestOverrides = const {},
    Map<String, String> extraEntries = const {},
  }) {
    final target = directory ?? root;
    target.createSync(recursive: true);
    final manifest = {
      ..._manifest(id, dependencies: dependencies, capabilities: capabilities),
      ...manifestOverrides,
    };
    final archive = Archive()
      ..addFile(ArchiveFile.string('topiaforge.mod.json', jsonEncode(manifest)))
      ..addFile(ArchiveFile.string('${_assembly(id)}.dll', 'managed fixture'));
    for (final entry in extraEntries.entries) {
      archive.addFile(ArchiveFile.string(entry.key, entry.value));
    }
    return File(p.join(target.path, '$id.topiaforgemod'))
      ..writeAsBytesSync(ZipEncoder().encode(archive), flush: true);
  }

  void blockLauncherLog() {
    final log = File(p.join(data.path, 'logs', 'launcher.log'));
    if (log.existsSync()) log.deleteSync();
    Directory(log.path).createSync(recursive: true);
  }

  Future<void> dispose() async {
    await repository.dispose();
    if (root.existsSync()) root.deleteSync(recursive: true);
  }
}

List<PackageSource> _isolatedSources([PackageSource? source]) => [
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
  ?source,
];

Map<String, Object?> _manifest(
  String id, {
  Map<String, String> dependencies = const {},
  List<String> capabilities = const [],
}) => {
  'schemaVersion': ModManifest.currentSchemaVersion,
  'name': id,
  'displayName': id,
  'version': '1.0.0',
  'author': {'name': 'TopiaForge'},
  'entryAssembly': '${_assembly(id)}.dll',
  'entryType': '$id.Entry',
  'supportedGameVersionRange': '*',
  'supportedLoaderVersionRange': '*',
  'supportedSdkVersionRange': '*',
  if (dependencies.isNotEmpty) 'dependencies': dependencies,
  if (capabilities.isNotEmpty) 'capabilities': capabilities,
};

String _assembly(String id) => id
    .split('.')
    .map((part) => part[0].toUpperCase() + part.substring(1))
    .join();

String _sha(File file) => sha256.convert(file.readAsBytesSync()).toString();
