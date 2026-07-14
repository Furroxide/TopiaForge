import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:path/path.dart' as p;
import 'package:robotopia/src/release_package_builder.dart';
import 'package:robotopia/src/release_package_io.dart';
import 'package:robotopia/src/release_package_models.dart';
import 'package:robotopia/src/release_package_validator.dart';
import 'package:test/test.dart';

void main() {
  late Directory temp;

  setUp(() {
    temp = Directory.systemTemp.createTempSync('release-package-test-');
  });

  tearDown(() {
    if (temp.existsSync()) {
      temp.deleteSync(recursive: true);
    }
  });

  test('builds a package from prebuilt launcher and CLI fixture', () async {
    final repo = _writeFixtureRepo(temp);
    final launcher = Directory(p.join(temp.path, 'launcher'))
      ..createSync(recursive: true);
    File(
      p.join(launcher.path, 'robotopia_launcher_flutter.exe'),
    ).writeAsStringSync('launcher');
    final cli = File(p.join(temp.path, 'robotopia.exe'))
      ..writeAsStringSync('cli');
    final output = Directory(p.join(temp.path, 'out'));

    final zipPath = await ReleasePackageBuilder(
      repositoryRoot: repo.path,
      platform: ReleasePackagePlatform.windows,
      outputRoot: output.path,
      prebuiltLauncher: launcher.path,
      prebuiltCli: cli.path,
      rebuildRuntimePayload: false,
    ).build();

    await ReleasePackageValidator(
      platform: ReleasePackagePlatform.windows,
      zipPath: zipPath,
      requireRuntimePayload: false,
      runCliSmoke: false,
    ).validate();

    final extracted = Directory(p.join(temp.path, 'extracted'));
    await const ReleaseFileOps().extractPlatformZip(
      File(zipPath),
      extracted,
      ReleasePackagePlatform.windows,
    );
    expect(File(p.join(extracted.path, 'docs', 'Guide.md')).existsSync(), true);
    expect(
      Directory(p.join(extracted.path, 'docs', 'internal')).existsSync(),
      false,
    );
    expect(
      File(
        p.join(extracted.path, 'launcher', 'robotopia_launcher_flutter.exe'),
      ).existsSync(),
      true,
    );
  });

  test('validator rejects a package without launcher output', () async {
    final zip = File(p.join(temp.path, 'QuantumWorks-windows-x64.zip'));
    final archive = Archive()
      ..addFile(ArchiveFile.string('tools/readme.txt', 'tools'))
      ..addFile(ArchiveFile.string('templates/readme.txt', 'templates'))
      ..addFile(ArchiveFile.string('docs/readme.txt', 'docs'))
      ..addFile(ArchiveFile.string('bindings/readme.txt', 'bindings'))
      ..addFile(ArchiveFile.string('baselines/readme.txt', 'baselines'))
      ..addFile(ArchiveFile.string('THIRD_PARTY_NOTICES.md', 'notices'))
      ..addFile(ArchiveFile.string('dist/vpm/index.json', '{}'))
      ..addFile(ArchiveFile.string('dist/test.robotopiamod', 'pkg'))
      ..addFile(ArchiveFile.string('robotopia.exe', 'cli'));
    zip.writeAsBytesSync(ZipEncoder().encode(archive));

    expect(
      () => ReleasePackageValidator(
        platform: ReleasePackagePlatform.windows,
        zipPath: zip.path,
        requireRuntimePayload: false,
        runCliSmoke: false,
      ).validate(),
      throwsA(isA<StateError>()),
    );
  });
}

Directory _writeFixtureRepo(Directory temp) {
  final repo = Directory(p.join(temp.path, 'repo'))..createSync();
  File(p.join(repo.path, 'RobotopiaModManager.slnx')).writeAsStringSync('');
  File(p.join(repo.path, 'README.md')).writeAsStringSync('readme');
  File(
    p.join(repo.path, 'THIRD_PARTY_NOTICES.md'),
  ).writeAsStringSync('notices');
  _writeFile(repo, ['tools', 'tool.txt'], 'tool');
  _writeFile(repo, ['docs', 'Guide.md'], 'guide');
  _writeFile(repo, ['docs', 'internal', 'Plan.md'], 'internal');
  _writeFile(repo, ['bindings', 'binding.txt'], 'binding');
  _writeFile(repo, ['baselines', 'baseline.txt'], 'baseline');
  _writeFile(repo, ['templates', 'mod', 'template.txt'], 'template');
  _writeFile(repo, ['templates', 'mod', 'bin', 'ignored.txt'], 'ignored');
  _writeFile(repo, ['dist', 'vpm', 'index.json'], jsonEncode({}));
  _writeFile(repo, ['dist', 'demo.robotopiamod'], 'package');
  return repo;
}

void _writeFile(Directory root, List<String> parts, String content) {
  final file = File(p.joinAll([root.path, ...parts]));
  file.parent.createSync(recursive: true);
  file.writeAsStringSync(content);
}
