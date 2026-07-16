import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:archive/archive.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  group('package install runtime constraints', () {
    test('matching constrained package previews and installs', () async {
      final fixture = _Fixture.create();
      addTearDown(fixture.dispose);
      final install = await fixture.repository.selectGameDirectory(
        fixture.game.path,
      );
      final package = fixture.package(
        'matching.mod',
        platforms: const ['windows'],
        architectures: const ['x64'],
        contentTargets: const ['standalonewindows64'],
      );

      final preview = await fixture.repository.previewPackage(
        package.path,
        install,
      );
      final installed = await fixture.repository.installPackage(
        package.path,
        install,
      );

      expect(preview.hasBlockingIssues, isFalse);
      expect(installed.map((mod) => mod.id), contains('matching.mod'));
    });

    test('mismatching constrained package cannot preview or install', () async {
      final fixture = _Fixture.create();
      addTearDown(fixture.dispose);
      final install = await fixture.repository.selectGameDirectory(
        fixture.game.path,
      );
      final package = fixture.package(
        'mismatch.mod',
        platforms: const ['macos'],
      );

      final preview = await fixture.repository.previewPackage(
        package.path,
        install,
      );

      expect(preview.hasBlockingIssues, isTrue);
      expect(
        preview.issues.map((issue) => issue.message).join(' '),
        contains('does not support host platform windows'),
      );
      await expectLater(
        fixture.repository.installPackage(package.path, install),
        throwsA(
          predicate(
            (error) => error.toString().contains(
              'does not support host platform windows',
            ),
          ),
        ),
      );
    });
  });
}

class _Fixture {
  _Fixture(this.root, this.game, this.repository);

  factory _Fixture.create() {
    final root = Directory.systemTemp.createTempSync(
      'package-runtime-constraints-',
    );
    final game = Directory(p.join(root.path, 'TopiaForge'))..createSync();
    _writeX64Pe(File(p.join(game.path, 'Robotopia.exe')));
    final managed = Directory(p.join(game.path, 'Robotopia_Data', 'Managed'))
      ..createSync(recursive: true);
    File(p.join(managed.path, 'UnityEngine.dll')).writeAsStringSync('');
    return _Fixture(
      root,
      game,
      LocalLauncherRepository(
        dataRoot: p.join(root.path, 'data'),
        repositoryRoot: root.path,
        packageMetadataValidator: (_) async => const [],
      ),
    );
  }

  final Directory root;
  final Directory game;
  final LocalLauncherRepository repository;

  File package(
    String id, {
    List<String> platforms = const [],
    List<String> architectures = const [],
    List<String> contentTargets = const [],
  }) {
    final archive = Archive()
      ..addFile(
        ArchiveFile.string(
          'topiaforge.mod.json',
          jsonEncode({
            'schemaVersion': 4,
            'name': id,
            'displayName': id,
            'version': '1.0.0',
            'author': {'name': 'TopiaForge'},
            'entryAssembly': 'RuntimeConstraintsMod.dll',
            'entryType': 'RuntimeConstraintsMod.Entry',
            'supportedGameVersionRange': '*',
            'supportedLoaderVersionRange': '*',
            'supportedSdkVersionRange': '*',
            if (platforms.isNotEmpty) 'platforms': platforms,
            if (architectures.isNotEmpty) 'architectures': architectures,
            if (contentTargets.isNotEmpty) 'contentTargets': contentTargets,
          }),
        ),
      )
      ..addFile(ArchiveFile.string('RuntimeConstraintsMod.dll', 'dll'));
    final file = File(p.join(root.path, '$id-1.0.0.topiaforgemod'));
    file.writeAsBytesSync(ZipEncoder().encode(archive));
    return file;
  }

  Future<void> dispose() async {
    await repository.dispose();
    if (root.existsSync()) root.deleteSync(recursive: true);
  }
}

void _writeX64Pe(File file) {
  final bytes = Uint8List(0x86);
  bytes[0] = 0x4d;
  bytes[1] = 0x5a;
  final data = ByteData.sublistView(bytes);
  data.setUint32(0x3c, 0x80, Endian.little);
  bytes[0x80] = 0x50;
  bytes[0x81] = 0x45;
  data.setUint16(0x84, 0x8664, Endian.little);
  file.writeAsBytesSync(bytes, flush: true);
}
