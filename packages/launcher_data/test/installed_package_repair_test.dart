import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:crypto/crypto.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  test('unpinned selection recovers to the highest valid version', () async {
    final harness = await _RepairHarness.create();
    addTearDown(harness.dispose);
    final v1 = harness.package('selection.mod', '1.0.0');
    final v2 = harness.package('selection.mod', '2.0.0');
    await harness.install(v1);
    await harness.install(v2);
    harness.tamper('selection.mod', '2.0.0', 'tampered-v2');

    final mod = (await harness.repository.loadSnapshot()).installedMods.single;

    expect(mod.version, '1.0.0');
    expect(mod.isValid, isTrue);
    expect(mod.requestedVersion, '2.0.0');
    expect(mod.selectionReason, contains("recovered unpinned selection"));
    expect(mod.installedVersions, hasLength(2));
    expect(
      mod.installedVersions.singleWhere((item) => item.version == '1.0.0'),
      isA<InstalledModVersionStatus>()
          .having((item) => item.selected, 'selected', isTrue)
          .having((item) => item.isValid, 'valid', isTrue),
    );
    expect(
      mod.installedVersions.singleWhere((item) => item.version == '2.0.0'),
      isA<InstalledModVersionStatus>()
          .having((item) => item.selected, 'selected', isFalse)
          .having(
            (item) => item.errors.join(' '),
            'errors',
            contains('changed'),
          ),
    );
  });

  test('an exact missing pin fails closed instead of falling back', () async {
    final harness = await _RepairHarness.create();
    addTearDown(harness.dispose);
    await harness.install(harness.package('pinned.mod', '1.0.0'));
    harness.pin('pinned.mod', '9.0.0');

    final mod = (await harness.repository.loadSnapshot()).installedMods.single;

    expect(mod.version, '9.0.0');
    expect(mod.versionPinned, isTrue);
    expect(mod.isValid, isFalse);
    expect(mod.errors.join(' '), contains('refusing to fall back'));
    expect(mod.selectionReason, contains("exact profile pin '9.0.0'"));
    expect(
      mod.installedVersions.where((item) => item.selected).single.version,
      '9.0.0',
    );
  });

  test('managed metadata must remain valid at snapshot time', () async {
    final harness = await _RepairHarness.create();
    addTearDown(harness.dispose);
    await harness.install(harness.package('metadata.mod', '1.0.0'));
    final rejecting = harness.repositoryWith(
      packageMetadataValidator: (_) async => const [
        'Entry type does not derive from TopiaForgeMod.',
      ],
    );
    addTearDown(rejecting.dispose);

    final mod = (await rejecting.loadSnapshot()).installedMods.single;

    expect(mod.isValid, isFalse);
    expect(mod.errors.join(' '), contains('does not derive'));
    expect(mod.errors.join(' '), contains('Reinstall or repair'));
  });

  test('repair atomically reinstalls from a trusted local source', () async {
    final harness = await _RepairHarness.create();
    addTearDown(harness.dispose);
    final package = harness.package(
      'repair.mod',
      '1.0.0',
      directory: harness.dist,
    );
    await harness.install(package);
    harness.tamper('repair.mod', '1.0.0', 'corrupt');
    final damaged =
        (await harness.repository.loadSnapshot()).installedMods.single;
    harness.blockLauncherLog();

    final repaired = await harness.repository.repairInstalledMod(
      harness.installation,
      damaged,
    );

    expect(repaired.single.isValid, isTrue);
    expect(
      harness.installedAssembly('repair.mod', '1.0.0').readAsStringSync(),
      'valid-assembly',
    );
    expect(repaired.single.trust, 'sha256-verified');
  });

  test('repair rolls back the prior bytes when commit fails', () async {
    final harness = await _RepairHarness.create();
    addTearDown(harness.dispose);
    final package = harness.package(
      'rollback.mod',
      '1.0.0',
      directory: harness.dist,
    );
    await harness.install(package);
    harness.tamper('rollback.mod', '1.0.0', 'prior-corrupt-bytes');
    final failing = harness.repositoryWith(
      packageInstallCommitHook: (_) => throw StateError('injected failure'),
    );
    addTearDown(failing.dispose);
    final damaged = (await failing.loadSnapshot()).installedMods.single;

    await expectLater(
      failing.repairInstalledMod(harness.installation, damaged),
      throwsA(predicate((error) => '$error'.contains('injected failure'))),
    );

    expect(
      harness.installedAssembly('rollback.mod', '1.0.0').readAsStringSync(),
      'prior-corrupt-bytes',
    );
    expect(
      (await failing.loadSnapshot()).installedMods.single.isValid,
      isFalse,
    );
  });

  test('repair uses only an integrity-verified cache fallback', () async {
    final harness = await _RepairHarness.create(localSourceEnabled: false);
    addTearDown(harness.dispose);
    final package = harness.package('cache.mod', '1.0.0');
    final digest = _sha(package);
    await harness.install(package);
    final cache = File(
      p.join(harness.data.path, 'package-cache', '$digest.topiaforgemod'),
    )..createSync(recursive: true);
    package.copySync(cache.path);
    harness.tamper('cache.mod', '1.0.0', 'corrupt');
    final damaged =
        (await harness.repository.loadSnapshot()).installedMods.single;

    final repaired = await harness.repository.repairInstalledMod(
      harness.installation,
      damaged,
    );

    expect(repaired.single.isValid, isTrue);
    expect(
      harness.installedAssembly('cache.mod', '1.0.0').readAsStringSync(),
      'valid-assembly',
    );
  });

  test('cache repair rejects a package with the wrong identity', () async {
    final harness = await _RepairHarness.create(localSourceEnabled: false);
    addTearDown(harness.dispose);
    await harness.install(harness.package('target.mod', '1.0.0'));
    final other = harness.package('other.mod', '1.0.0');
    final otherDigest = _sha(other);
    final cache = File(
      p.join(harness.data.path, 'package-cache', '$otherDigest.topiaforgemod'),
    )..createSync(recursive: true);
    other.copySync(cache.path);
    harness.rewriteReceiptSource('target.mod', '1.0.0', otherDigest);
    harness.tamper('target.mod', '1.0.0', 'keep-target-damage');
    final damaged =
        (await harness.repository.loadSnapshot()).installedMods.single;
    expect(damaged.selectionReason, contains('launch remains blocked'));

    await expectLater(
      harness.repository.repairInstalledMod(harness.installation, damaged),
      throwsA(predicate((error) => '$error'.contains('not target.mod 1.0.0'))),
    );

    expect(
      harness.installedAssembly('target.mod', '1.0.0').readAsStringSync(),
      'keep-target-damage',
    );
    expect(
      Directory(
        p.join(
          harness.game.path,
          'BepInEx',
          'TopiaForge',
          'packages',
          'other.mod',
        ),
      ).existsSync(),
      isFalse,
    );
  });

  test('repair fails without a trusted source and preserves damage', () async {
    final harness = await _RepairHarness.create(localSourceEnabled: false);
    addTearDown(harness.dispose);
    final package = harness.package('unavailable.mod', '1.0.0');
    await harness.install(package);
    harness.tamper('unavailable.mod', '1.0.0', 'keep-this-damage');
    final damaged =
        (await harness.repository.loadSnapshot()).installedMods.single;

    await expectLater(
      harness.repository.repairInstalledMod(harness.installation, damaged),
      throwsA(
        predicate((error) => '$error'.contains('No trusted registry package')),
      ),
    );

    expect(
      harness.installedAssembly('unavailable.mod', '1.0.0').readAsStringSync(),
      'keep-this-damage',
    );
  });
}

class _RepairHarness {
  _RepairHarness._({
    required this.root,
    required this.game,
    required this.data,
    required this.dist,
    required this.repository,
    required this.installation,
  });

  final Directory root;
  final Directory game;
  final Directory data;
  final Directory dist;
  final LocalLauncherRepository repository;
  final GameInstall installation;

  static Future<_RepairHarness> create({bool localSourceEnabled = true}) async {
    final root = Directory.systemTemp.createTempSync('installed-repair-');
    final game = Directory(p.join(root.path, 'game'))..createSync();
    final data = Directory(p.join(root.path, 'data'));
    final dist = Directory(p.join(root.path, 'dist'))..createSync();
    _createGame(game);
    final repository = LocalLauncherRepository(
      dataRoot: data.path,
      repositoryRoot: root.path,
      packageMetadataValidator: (_) async => const [],
    );
    final installation = await repository.selectGameDirectory(game.path);
    await repository.savePackageSources([
      PackageSource(
        id: 'io.github.furroxide.topiaforge.local',
        name: 'Bundled Local Packages',
        url: dist.uri.toString(),
        enabled: localSourceEnabled,
        builtIn: true,
      ),
      const PackageSource(
        id: ModRegistryFormat.officialSourceId,
        name: ModRegistryFormat.officialSourceName,
        url: ModRegistryFormat.officialRegistryUrl,
        enabled: false,
        builtIn: true,
      ),
    ]);
    return _RepairHarness._(
      root: root,
      game: game,
      data: data,
      dist: dist,
      repository: repository,
      installation: installation,
    );
  }

  LocalLauncherRepository repositoryWith({
    PackageMetadataValidator? packageMetadataValidator,
    PackageInstallCommitHook? packageInstallCommitHook,
  }) => LocalLauncherRepository(
    dataRoot: data.path,
    repositoryRoot: root.path,
    knownGamePath: game.path,
    packageMetadataValidator: packageMetadataValidator ?? (_) async => const [],
    packageInstallCommitHook: packageInstallCommitHook,
  );

  File package(String id, String version, {Directory? directory}) {
    final target = directory ?? Directory(p.join(root.path, 'archives'));
    target.createSync(recursive: true);
    final entryAssembly = _assemblyName(id);
    final archive = Archive()
      ..addFile(
        ArchiveFile.string(
          'topiaforge.mod.json',
          jsonEncode({
            'schemaVersion': 5,
            'name': id,
            'displayName': id,
            'version': version,
            'author': {'name': 'TopiaForge'},
            'entryAssembly': '$entryAssembly.dll',
            'entryType': '$entryAssembly.Entry',
            'supportedGameVersionRange': '*',
            'supportedLoaderVersionRange': '*',
            'supportedSdkVersionRange': '*',
          }),
        ),
      )
      ..addFile(ArchiveFile.string('$entryAssembly.dll', 'valid-assembly'));
    return File(p.join(target.path, '$id-$version.topiaforgemod'))
      ..writeAsBytesSync(ZipEncoder().encode(archive));
  }

  Future<void> install(File package) => repository.installPackage(
    package.path,
    installation,
    expectedSha256: _sha(package),
  );

  File installedAssembly(String id, String version) => File(
    p.join(
      game.path,
      'BepInEx',
      'TopiaForge',
      'packages',
      id,
      version,
      '${_assemblyName(id)}.dll',
    ),
  );

  void tamper(String id, String version, String contents) =>
      installedAssembly(id, version).writeAsStringSync(contents, flush: true);

  void blockLauncherLog() {
    final log = File(p.join(data.path, 'logs', 'launcher.log'));
    if (log.existsSync()) log.deleteSync();
    Directory(log.path).createSync(recursive: true);
  }

  void pin(String id, String version) {
    final stateFile = File(
      p.join(game.path, 'BepInEx', 'TopiaForge', 'state.json'),
    );
    final state = jsonDecode(stateFile.readAsStringSync()) as Map;
    final item = (state['mods'] as List).whereType<Map>().singleWhere(
      (candidate) => candidate['id'] == id,
    );
    item['version'] = version;
    item['versionPinned'] = true;
    stateFile.writeAsStringSync(jsonEncode(state), flush: true);
  }

  void rewriteReceiptSource(String id, String version, String digest) {
    final receipt = File(
      p.join(
        game.path,
        'BepInEx',
        'TopiaForge',
        'packages',
        id,
        version,
        'topiaforge.install.json',
      ),
    );
    final value = jsonDecode(receipt.readAsStringSync()) as Map;
    value['sourceSha256'] = digest;
    value['trust'] = 'sha256-verified';
    receipt.writeAsStringSync(jsonEncode(value), flush: true);
  }

  Future<void> dispose() async {
    await repository.dispose();
    if (root.existsSync()) root.deleteSync(recursive: true);
  }
}

void _createGame(Directory root) {
  File(p.join(root.path, 'Robotopia.exe')).writeAsStringSync('');
  final managed = Directory(p.join(root.path, 'Robotopia_Data', 'Managed'))
    ..createSync(recursive: true);
  File(p.join(managed.path, 'UnityEngine.dll')).writeAsStringSync('');
}

String _assemblyName(String id) => id
    .split(RegExp('[^A-Za-z0-9]+'))
    .where((part) => part.isNotEmpty)
    .map((part) => '${part[0].toUpperCase()}${part.substring(1)}')
    .join();

String _sha(File file) => sha256.convert(file.readAsBytesSync()).toString();
