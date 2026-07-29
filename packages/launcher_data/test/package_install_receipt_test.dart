import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:crypto/crypto.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  test('install writes a complete deterministic integrity receipt', () async {
    final root = Directory.systemTemp.createTempSync('package-receipt-');
    addTearDown(() => root.deleteSync(recursive: true));
    final game = Directory(p.join(root.path, 'TopiaForge'))..createSync();
    _createGame(game);
    final repository = LocalLauncherRepository(
      dataRoot: p.join(root.path, 'data'),
      repositoryRoot: root.path,
      packageMetadataValidator: (_) async => const [],
    );
    final install = await repository.selectGameDirectory(game.path);
    final package = _createPackage(root);

    await repository.installPackage(package.path, install);

    final packageRoot = Directory(
      p.join(game.path, 'BepInEx', 'TopiaForge', 'packages', _modId, _version),
    );
    final receiptFile = File(p.join(packageRoot.path, _receiptName));
    expect(receiptFile.existsSync(), isTrue);
    final receipt = _jsonObject(receiptFile);

    expect(receipt['schemaVersion'], 2);
    expect(receipt['modId'], _modId);
    expect(receipt['version'], _version);
    expect(receipt['sourceFile'], p.basename(package.path));
    expect(receipt['source'], 'local');
    expect(receipt['sourceSha256'], _sha256File(package));
    expect(receipt['sourceSha256'], matches(_lowerSha256));
    expect(receipt['validatorVersion'], '1');
    expect(receipt['trust'], 'local-unverified');
    expect(
      DateTime.tryParse(receipt['installedAtUtc'] as String? ?? ''),
      isNotNull,
    );

    final inventory = (receipt['files'] as List<Object?>)
        .map((item) => Map<String, Object?>.from(item! as Map))
        .toList();
    final paths = inventory.map((item) => item['path'] as String).toList();
    expect(paths, orderedEquals([...paths]..sort()));
    expect(paths, orderedEquals(_expectedFiles.keys.toList()..sort()));
    expect(paths, isNot(contains(_receiptName)));

    for (final item in inventory) {
      final relativePath = item['path']! as String;
      final installedFile = File(
        p.joinAll([packageRoot.path, ...p.posix.split(relativePath)]),
      );
      expect(installedFile.existsSync(), isTrue, reason: relativePath);
      expect(item['length'], installedFile.lengthSync(), reason: relativePath);
      expect(item['sha256'], matches(_lowerSha256), reason: relativePath);
      expect(item['sha256'], _sha256File(installedFile), reason: relativePath);
      expect(
        item['critical'],
        _expectedFiles[relativePath],
        reason: relativePath,
      );
    }

    expect(_verifyInstalledBytes(packageRoot, receipt), isEmpty);
    File(
      p.join(packageRoot.path, 'content', 'a-first.txt'),
    ).writeAsStringSync('tampered');
    expect(
      _verifyInstalledBytes(packageRoot, receipt),
      contains(contains('content/a-first.txt')),
    );
  });

  test(
    'receipt round-trips safe registry provenance and redacts unsafe ids',
    () async {
      final fixture = await _createFixture('package-receipt-provenance-');
      addTearDown(fixture.dispose);
      final digest = _sha256File(fixture.package);

      await fixture.repository.installPackage(
        fixture.package.path,
        fixture.install,
        expectedSha256: digest,
        sourceId: 'Community.Source',
      );
      final receiptFile = fixture.receiptFile;
      var receipt = _jsonObject(receiptFile);
      expect(receipt['source'], 'registry:community.source');
      expect(receipt['sourceFile'], p.basename(fixture.package.path));
      expect(receipt['trust'], 'sha256-verified');

      const secret = 'super-secret-token';
      await fixture.repository.installPackage(
        fixture.package.path,
        fixture.install,
        sourceId: 'https://user:$secret@example.test?token=$secret',
      );
      receipt = _jsonObject(receiptFile);
      expect(receipt['source'], 'registry');
      expect((receipt['source']! as String).length, lessThanOrEqualTo(160));
      expect(receiptFile.readAsStringSync(), isNot(contains(secret)));
    },
  );

  test(
    'receipt rejects invalid trust and source but accepts schema-v1 receipts',
    () async {
      final fixture = await _createFixture('package-receipt-compat-');
      addTearDown(fixture.dispose);
      await fixture.repository.installPackage(
        fixture.package.path,
        fixture.install,
      );
      final receiptFile = fixture.receiptFile;
      final receipt = _jsonObject(receiptFile);

      receipt['trust'] = 'signature-trusted';
      receiptFile.writeAsStringSync(jsonEncode(receipt), flush: true);
      var installed = (await fixture.repository.loadSnapshot()).installedMods
          .singleWhere((mod) => mod.id == _modId);
      expect(installed.errors, contains(contains('trust result is invalid')));

      receipt['trust'] = 42;
      receiptFile.writeAsStringSync(jsonEncode(receipt), flush: true);
      installed = (await fixture.repository.loadSnapshot()).installedMods
          .singleWhere((mod) => mod.id == _modId);
      expect(installed.errors, contains(contains('trust result is invalid')));

      receipt['trust'] = 'local-unverified';
      receipt['source'] = 'registry:https://user:password@example.test?token=x';
      receiptFile.writeAsStringSync(jsonEncode(receipt), flush: true);
      installed = (await fixture.repository.loadSnapshot()).installedMods
          .singleWhere((mod) => mod.id == _modId);
      expect(
        installed.errors,
        contains(contains('source provenance is invalid')),
      );

      receipt['schemaVersion'] = 1;
      receipt.remove('source');
      receiptFile.writeAsStringSync(jsonEncode(receipt), flush: true);
      installed = (await fixture.repository.loadSnapshot()).installedMods
          .singleWhere((mod) => mod.id == _modId);
      expect(installed.errors, isEmpty);
    },
  );

  test(
    'inbox installs retain inbox provenance without the inbox path',
    () async {
      final fixture = await _createFixture('package-receipt-inbox-');
      addTearDown(fixture.dispose);
      final inbox = Directory(
        p.join(fixture.game.path, 'BepInEx', 'TopiaForge', 'package-inbox'),
      )..createSync(recursive: true);
      final inboxPackage = fixture.package.copySync(
        p.join(inbox.path, p.basename(fixture.package.path)),
      );

      await fixture.repository.installInboxPackages(fixture.install);

      final receipt = _jsonObject(fixture.receiptFile);
      expect(receipt['source'], 'inbox');
      expect(receipt['sourceFile'], p.basename(inboxPackage.path));
      expect(
        fixture.receiptFile.readAsStringSync(),
        isNot(contains(inbox.path)),
      );
    },
  );

  test(
    'V5 synchronized files use runtime-compatible critical classification',
    () async {
      final fixture = await _createFixture(
        'package-receipt-multiplayer-',
        multiplayer: true,
      );
      addTearDown(fixture.dispose);

      await fixture.repository.installPackage(
        fixture.package.path,
        fixture.install,
      );

      final receipt = _jsonObject(fixture.receiptFile);
      final files = (receipt['files']! as List<Object?>)
          .map((raw) => Map<String, Object?>.from(raw! as Map))
          .toList();
      receipt['files'] = files;
      final inventory = <String, Map<String, Object?>>{
        for (final item in files) item['path']! as String: item,
      };
      expect(inventory[_multiplayerContractLockPath]!['critical'], isTrue);
      expect(inventory[_synchronizedRulesPath]!['critical'], isTrue);
      expect(inventory[_localContentPath]!['critical'], isFalse);

      var installed = (await fixture.repository.loadSnapshot()).installedMods
          .singleWhere((mod) => mod.id == _modId);
      expect(
        installed.errors,
        isEmpty,
        reason: 'A launcher receipt must pass the shared runtime contract.',
      );

      inventory[_synchronizedRulesPath]!['critical'] = false;
      fixture.receiptFile.writeAsStringSync(jsonEncode(receipt), flush: true);
      installed = (await fixture.repository.loadSnapshot()).installedMods
          .singleWhere((mod) => mod.id == _modId);
      expect(
        installed.errors,
        contains(
          contains(
            'critical classification changed for $_synchronizedRulesPath',
          ),
        ),
      );
    },
  );
}

Future<_ReceiptFixture> _createFixture(
  String prefix, {
  bool multiplayer = false,
}) async {
  final root = Directory.systemTemp.createTempSync(prefix);
  final game = Directory(p.join(root.path, 'TopiaForge'))..createSync();
  _createGame(game);
  final repository = LocalLauncherRepository(
    dataRoot: p.join(root.path, 'data'),
    repositoryRoot: root.path,
    packageMetadataValidator: (_) async => const [],
  );
  final install = await repository.selectGameDirectory(game.path);
  return _ReceiptFixture(
    root: root,
    game: game,
    repository: repository,
    install: install,
    package: multiplayer
        ? _createMultiplayerPackage(root)
        : _createPackage(root),
  );
}

class _ReceiptFixture {
  const _ReceiptFixture({
    required this.root,
    required this.game,
    required this.repository,
    required this.install,
    required this.package,
  });

  final Directory root;
  final Directory game;
  final LocalLauncherRepository repository;
  final GameInstall install;
  final File package;

  File get receiptFile => File(
    p.join(
      game.path,
      'BepInEx',
      'TopiaForge',
      'packages',
      _modId,
      _version,
      _receiptName,
    ),
  );

  Future<void> dispose() async {
    await repository.dispose();
    if (root.existsSync()) root.deleteSync(recursive: true);
  }
}

const _modId = 'example.receipt';
const _version = '1.2.3';
const _entryAssembly = 'Example.Receipt.dll';
const _apiAssembly = 'contracts/Example.Receipt.Api.dll';
const _receiptName = 'topiaforge.install.json';
const _multiplayerContractLockPath = 'topiaforge.multiplayer.lock.json';
const _synchronizedRulesPath = 'Content/gameplay-rules.json';
const _localContentPath = 'Content/local-note.txt';
final _lowerSha256 = RegExp(r'^[0-9a-f]{64}$');
const _expectedFiles = <String, bool>{
  _entryAssembly: true,
  'content/a-first.txt': false,
  'content/z-last.txt': false,
  _apiAssembly: true,
  'topiaforge.mod.json': true,
};

void _createGame(Directory game) {
  File(p.join(game.path, 'Robotopia.exe')).writeAsStringSync('');
  Directory(
    p.join(game.path, 'Robotopia_Data', 'Managed'),
  ).createSync(recursive: true);
  File(
    p.join(game.path, 'Robotopia_Data', 'Managed', 'UnityEngine.dll'),
  ).writeAsStringSync('');
}

File _createPackage(Directory root) {
  final manifest = <String, Object?>{
    'schemaVersion': 5,
    'name': _modId,
    'displayName': 'Receipt fixture',
    'version': _version,
    'author': {'name': 'TopiaForge'},
    'entryAssembly': _entryAssembly,
    'entryType': 'Example.Receipt.Entry',
    'supportedGameVersionRange': '*',
    'supportedLoaderVersionRange': '*',
    'supportedSdkVersionRange': '*',
    'apiAssemblies': [_apiAssembly],
  };
  final archive = Archive()
    ..addFile(ArchiveFile.string('content/z-last.txt', 'zulu'))
    ..addFile(ArchiveFile.string(_apiAssembly, 'public contract'))
    ..addFile(ArchiveFile.string('topiaforge.mod.json', jsonEncode(manifest)))
    ..addFile(ArchiveFile.string('content/a-first.txt', 'alpha'))
    ..addFile(ArchiveFile.string(_entryAssembly, 'entry assembly'));
  final package = File(p.join(root.path, '$_modId-$_version.topiaforgemod'));
  package.writeAsBytesSync(ZipEncoder().encode(archive));
  return package;
}

File _createMultiplayerPackage(Directory root) {
  const contractLock = '{"schemaVersion":1,"contracts":[]}';
  const synchronizedRules = '{"difficulty":2}';
  const localContent = 'not synchronized';
  final manifest = <String, Object?>{
    'schemaVersion': 5,
    'name': _modId,
    'displayName': 'Multiplayer receipt fixture',
    'version': _version,
    'author': {'name': 'TopiaForge'},
    'entryAssembly': _entryAssembly,
    'entryType': 'Example.Receipt.Entry',
    'supportedGameVersionRange': '*',
    'supportedLoaderVersionRange': '*',
    'supportedSdkVersionRange': '*',
    'apiAssemblies': [_apiAssembly],
    'multiplayer': {
      'mode': 'session',
      'presence': 'required',
      'protocol': {'version': '1.0.0', 'peerVersionRange': '>=1.0.0 <2.0.0'},
      'synchronizedFiles': [
        _multiplayerContractLockPath,
        _synchronizedRulesPath,
      ],
    },
    'hashes': {
      _multiplayerContractLockPath: sha256
          .convert(utf8.encode(contractLock))
          .toString(),
      _synchronizedRulesPath: sha256
          .convert(utf8.encode(synchronizedRules))
          .toString(),
    },
  };
  final archive = Archive()
    ..addFile(ArchiveFile.string(_entryAssembly, 'entry assembly'))
    ..addFile(ArchiveFile.string(_apiAssembly, 'public contract'))
    ..addFile(ArchiveFile.string(_multiplayerContractLockPath, contractLock))
    ..addFile(ArchiveFile.string(_synchronizedRulesPath, synchronizedRules))
    ..addFile(ArchiveFile.string(_localContentPath, localContent))
    ..addFile(ArchiveFile.string('topiaforge.mod.json', jsonEncode(manifest)));
  final package = File(p.join(root.path, '$_modId-$_version.topiaforgemod'));
  package.writeAsBytesSync(ZipEncoder().encode(archive));
  return package;
}

Map<String, Object?> _jsonObject(File file) {
  return Map<String, Object?>.from(jsonDecode(file.readAsStringSync()) as Map);
}

String _sha256File(File file) {
  return sha256.convert(file.readAsBytesSync()).toString();
}

List<String> _verifyInstalledBytes(
  Directory packageRoot,
  Map<String, Object?> receipt,
) {
  final errors = <String>[];
  final inventory = <String, Map<String, Object?>>{};
  for (final raw in receipt['files']! as List<Object?>) {
    final item = Map<String, Object?>.from(raw! as Map);
    inventory[item['path']! as String] = item;
  }

  final actualFiles =
      packageRoot
          .listSync(recursive: true, followLinks: false)
          .whereType<File>()
          .map(
            (file) => (
              file: file,
              path: p
                  .relative(file.path, from: packageRoot.path)
                  .split(p.separator)
                  .join('/'),
            ),
          )
          .where((item) => item.path != _receiptName)
          .toList()
        ..sort((left, right) => left.path.compareTo(right.path));

  for (final actual in actualFiles) {
    final expected = inventory.remove(actual.path);
    if (expected == null) {
      errors.add('${actual.path}: unreceipted');
      continue;
    }
    if (expected['length'] != actual.file.lengthSync()) {
      errors.add('${actual.path}: length changed');
      continue;
    }
    if (expected['sha256'] != _sha256File(actual.file)) {
      errors.add('${actual.path}: digest changed');
    }
  }
  for (final missing in inventory.keys) {
    errors.add('$missing: missing');
  }
  return errors;
}
