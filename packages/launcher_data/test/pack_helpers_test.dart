import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory root;
  late Directory repoRoot;
  late Directory projectDir;
  late LocalDeveloperRepository repository;

  setUp(() {
    root = Directory.systemTemp.createTempSync('pack_helpers_test');
    repoRoot = Directory(p.join(root.path, 'repo'))..createSync();
    projectDir = Directory(p.join(root.path, 'sample.mod'))..createSync();
    repository = LocalDeveloperRepository(
      dataRoot: p.join(root.path, 'data'),
      repositoryRoot: repoRoot.path,
    );
  });

  tearDown(() {
    if (root.existsSync()) {
      root.deleteSync(recursive: true);
    }
  });

  Map<String, Object?> manifestJson() => {
    'schemaVersion': 2,
    'name': 'sample.mod',
    'displayName': 'Sample Mod',
    'version': '1.2.3',
    'entryAssembly': 'Sample.dll',
    'entryType': 'Sample.Entry',
  };

  void writeManifest([Map<String, Object?>? overrides]) {
    File(p.join(projectDir.path, 'robotopia.mod.json')).writeAsStringSync(
      jsonEncode({...manifestJson(), ...?overrides}),
    );
  }

  Archive readPackage(String path) =>
      ZipDecoder().decodeBytes(File(path).readAsBytesSync());

  test('manifest-only pack ships the project tree minus build dirs', () async {
    writeManifest();
    File(p.join(projectDir.path, 'Sample.dll')).writeAsStringSync('payload');
    File(
      p.join(projectDir.path, 'assets', 'texture.png'),
    ).createSync(recursive: true);
    File(
      p.join(projectDir.path, 'bin', 'ignored.dll'),
    ).createSync(recursive: true);
    File(
      p.join(projectDir.path, 'obj', 'ignored.cache'),
    ).createSync(recursive: true);

    final packagePath = await repository.packModDirectory(projectDir.path);

    expect(p.basename(packagePath), 'sample.mod-1.2.3.robotopiamod');
    final archive = readPackage(packagePath);
    final names = archive.files.map((file) => file.name).toSet();
    expect(names, contains('robotopia.mod.json'));
    expect(names, contains('Sample.dll'));
    expect(names, contains('assets/texture.png'));
    expect(names.where((name) => name.startsWith('bin/')), isEmpty);
    expect(names.where((name) => name.startsWith('obj/')), isEmpty);
  });

  test('ships the repo-root game bindings file when present', () async {
    writeManifest();
    File(p.join(projectDir.path, 'Sample.dll')).writeAsStringSync('payload');
    File(
      p.join(repoRoot.path, 'bindings', 'sample.mod.gamebindings.json'),
    ).createSync(recursive: true);

    final packagePath = await repository.packModDirectory(projectDir.path);

    final names = readPackage(packagePath).files.map((f) => f.name).toSet();
    expect(names, contains('bindings/sample.mod.gamebindings.json'));
  });

  test('rejects a manifest missing required fields', () async {
    writeManifest({'entryType': ''});
    await expectLater(
      repository.packModDirectory(projectDir.path),
      throwsA(isA<StateError>()),
    );
  });

  test('sanitizes unsafe characters in the package file name', () async {
    writeManifest({'name': 'weird name!', 'version': '1.0.0+build'});
    File(p.join(projectDir.path, 'Sample.dll')).writeAsStringSync('payload');

    final packagePath = await repository.packModDirectory(projectDir.path);

    expect(p.basename(packagePath), 'weird_name_-1.0.0_build.robotopiamod');
  });
}
