import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  const rejectedMetadata = <String, String>{
    'bad managed PE':
        'TFPKG101: entryAssembly SampleMod.dll is not a valid managed PE.',
    'wrong entry type':
        'TFPKG105: entryType sample.mod.Entry was not found or does not derive from TopiaForgeMod.',
    'missing public parameterless constructor':
        'TFPKG106: entryType sample.mod.Entry needs a public parameterless constructor.',
    'bundled framework assembly':
        'TFPKG109: framework assembly TopiaForge.Mods.Abstractions.dll must not be bundled.',
  };

  for (final rejection in rejectedMetadata.entries) {
    test('rejects ${rejection.key} before package commit', () async {
      final fixture = _Fixture.create();
      addTearDown(fixture.dispose);
      var validations = 0;
      final repository = fixture.repository(
        validator: (packageRoot) async {
          validations++;
          expect(
            File(p.join(packageRoot.path, 'topiaforge.mod.json')).existsSync(),
            isTrue,
          );
          return [rejection.value];
        },
      );
      final install = await repository.selectGameDirectory(fixture.game.path);
      final package = fixture.package('sample.mod', '1.0.0');

      await expectLater(
        repository.installPackage(package.path, install),
        throwsA(
          predicate((error) => error.toString().contains(rejection.value)),
        ),
      );

      expect(validations, 1);
      expect(
        fixture.packageDirectory('sample.mod', '1.0.0').existsSync(),
        false,
      );
      expect(fixture.stagingEntries(), isEmpty);
    });
  }

  test(
    'metadata rejection preserves the installed version and state',
    () async {
      final fixture = _Fixture.create();
      addTearDown(fixture.dispose);
      final accepting = fixture.repository(validator: (_) async => const []);
      final install = await accepting.selectGameDirectory(fixture.game.path);
      await accepting.installPackage(
        fixture.package('sample.mod', '1.0.0').path,
        install,
      );
      final originalAssembly = File(
        p.join(
          fixture.packageDirectory('sample.mod', '1.0.0').path,
          'SampleMod.dll',
        ),
      ).readAsStringSync();

      final rejecting = fixture.repository(
        validator: (_) async => const [
          'TFPKG106: entry type needs a public parameterless constructor.',
        ],
      );
      await expectLater(
        rejecting.installPackage(
          fixture.package('sample.mod', '2.0.0').path,
          install,
        ),
        throwsA(predicate((error) => error.toString().contains('TFPKG106'))),
      );

      expect(
        fixture.packageDirectory('sample.mod', '2.0.0').existsSync(),
        false,
      );
      expect(
        File(
          p.join(
            fixture.packageDirectory('sample.mod', '1.0.0').path,
            'SampleMod.dll',
          ),
        ).readAsStringSync(),
        originalAssembly,
      );
      final snapshot = await rejecting.loadSnapshot();
      expect(snapshot.installedMods, hasLength(1));
      expect(snapshot.installedMods.single.version, '1.0.0');
      expect(fixture.stagingEntries(), isEmpty);
    },
  );
}

class _Fixture {
  _Fixture._(this.root, this.game);

  factory _Fixture.create() {
    final root = Directory.systemTemp.createTempSync('package-metadata-');
    final game = Directory(p.join(root.path, 'TopiaForge'))..createSync();
    File(p.join(game.path, 'Robotopia.exe')).writeAsStringSync('');
    final managed = Directory(p.join(game.path, 'Robotopia_Data', 'Managed'))
      ..createSync(recursive: true);
    File(p.join(managed.path, 'UnityEngine.dll')).writeAsStringSync('');
    return _Fixture._(root, game);
  }

  final Directory root;
  final Directory game;

  LocalLauncherRepository repository({
    required PackageMetadataValidator validator,
  }) => LocalLauncherRepository(
    dataRoot: p.join(root.path, 'data'),
    repositoryRoot: root.path,
    packageMetadataValidator: validator,
  );

  File package(String id, String version) {
    final manifest = <String, Object?>{
      'schemaVersion': ModManifest.currentSchemaVersion,
      'name': id,
      'displayName': 'Sample Mod',
      'version': version,
      'author': {'name': 'TopiaForge'},
      'entryAssembly': 'SampleMod.dll',
      'entryType': '$id.Entry',
      'supportedGameVersionRange': '*',
      'supportedLoaderVersionRange': '*',
      'supportedSdkVersionRange': '*',
    };
    final archive = Archive()
      ..addFile(ArchiveFile.string('topiaforge.mod.json', jsonEncode(manifest)))
      ..addFile(ArchiveFile.string('SampleMod.dll', version));
    return File(p.join(root.path, '$id-$version.topiaforgemod'))
      ..writeAsBytesSync(ZipEncoder().encode(archive));
  }

  Directory packageDirectory(String id, String version) => Directory(
    p.join(game.path, 'BepInEx', 'TopiaForge', 'packages', id, version),
  );

  List<FileSystemEntity> stagingEntries() {
    final staging = Directory(
      p.join(game.path, 'BepInEx', 'TopiaForge', 'staging'),
    );
    return staging.existsSync() ? staging.listSync() : const [];
  }

  void dispose() {
    if (root.existsSync()) root.deleteSync(recursive: true);
  }
}
