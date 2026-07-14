import 'dart:convert';
import 'dart:io';

import 'package:archive/archive.dart';
import 'package:crypto/crypto.dart';
import 'package:path/path.dart' as p;
import 'package:robotopia/src/release_metadata.dart';
import 'package:robotopia/src/release_policy.dart';
import 'package:test/test.dart';

void main() {
  late Directory temp;
  late String root;
  late RobotopiaReleaseCatalogEntry release;
  const targetSha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';

  setUp(() {
    temp = Directory.systemTemp.createTempSync('robotopia-metadata-test-');
    root = _repositoryRoot();
    release = RobotopiaReleaseCatalog.load(root).release('0.1.1');
    _writeCandidateAssets(temp, release);
  });

  tearDown(() {
    if (temp.existsSync()) temp.deleteSync(recursive: true);
  });

  test(
    'technical metadata is schema-valid, complete, and non-distributable',
    () async {
      final builder = const RobotopiaReleaseMetadataBuilder();
      await builder.build(
        repositoryRoot: root,
        version: release.version,
        targetSha: targetSha,
        assetsDirectory: temp.path,
        outputDirectory: temp.path,
        allowUnresolvedPolicy: true,
      );
      final bom = _json(File(p.join(temp.path, 'release-bom.json')));
      final sbom = _json(File(p.join(temp.path, 'release-sbom.spdx.json')));

      expect(bom['distributable'], isFalse);
      expect((bom['blockingReasons'] as List), isNotEmpty);
      expect(
        (bom['expectedArtifactSet'] as List).toSet(),
        release.artifacts.toSet(),
      );
      expect(
        ((bom['ecosystem'] as Map)['platformCopies'] as List),
        hasLength(3),
      );
      expect(
        (bom['provenance'] as Map).keys,
        containsAll(['unity', 'bepInEx']),
      );
      expect((bom['legalInventory'] as List), isNotEmpty);
      expect(sbom['spdxVersion'], 'SPDX-2.3');
      await builder.verify(
        repositoryRoot: root,
        version: release.version,
        targetSha: targetSha,
        assetsDirectory: temp.path,
        metadataDirectory: temp.path,
        allowUnresolvedPolicy: true,
      );
    },
  );

  test(
    'nested payload, provenance, and legal inventory tampering fails',
    () async {
      final builder = const RobotopiaReleaseMetadataBuilder();
      await builder.build(
        repositoryRoot: root,
        version: release.version,
        targetSha: targetSha,
        assetsDirectory: temp.path,
        outputDirectory: temp.path,
        allowUnresolvedPolicy: true,
      );

      final bomFile = File(p.join(temp.path, 'release-bom.json'));
      for (final section in const ['provenance', 'legalInventory']) {
        final bom = _json(bomFile);
        if (section == 'provenance') {
          ((bom[section] as Map)['unity'] as Map)['bundleSha256'] = List.filled(
            64,
            '0',
          ).join();
        } else {
          ((bom[section] as List).first as Map)['sha256'] = List.filled(
            64,
            '0',
          ).join();
        }
        _writeJson(bomFile, bom);
        _refreshChecksum(temp, 'release-bom.json');
        await expectLater(
          builder.verify(
            repositoryRoot: root,
            version: release.version,
            targetSha: targetSha,
            assetsDirectory: temp.path,
            metadataDirectory: temp.path,
            allowUnresolvedPolicy: true,
          ),
          throwsStateError,
        );
        await builder.build(
          repositoryRoot: root,
          version: release.version,
          targetSha: targetSha,
          assetsDirectory: temp.path,
          outputDirectory: temp.path,
          allowUnresolvedPolicy: true,
        );
      }

      _writePlatformArchive(
        File(p.join(temp.path, 'QuantumWorks-linux-x64.zip')),
        release,
        prefix: 'QuantumWorks/',
        changedPath: 'dist/vpm/index.json',
      );
      _refreshChecksum(temp, 'QuantumWorks-linux-x64.zip');
      await expectLater(
        builder.verify(
          repositoryRoot: root,
          version: release.version,
          targetSha: targetSha,
          assetsDirectory: temp.path,
          metadataDirectory: temp.path,
          allowUnresolvedPolicy: true,
        ),
        throwsStateError,
      );
    },
  );
}

void _writeCandidateAssets(
  Directory output,
  RobotopiaReleaseCatalogEntry release,
) {
  for (final entry in release.mods.entries) {
    File(p.join(output.path, '${entry.key}-${entry.value}.robotopiamod'))
      ..parent.createSync(recursive: true)
      ..writeAsStringSync('package:${entry.key}:${entry.value}\n');
  }
  _writePlatformArchive(
    File(p.join(output.path, 'QuantumWorks-linux-x64.zip')),
    release,
    prefix: 'QuantumWorks/',
  );
  _writePlatformArchive(
    File(p.join(output.path, 'QuantumWorks-windows-x64.zip')),
    release,
    prefix: 'QuantumWorks/',
  );
  _writePlatformArchive(
    File(p.join(output.path, 'QuantumWorks-macos-universal.zip')),
    release,
    prefix: 'QuantumWorks.app/Contents/Resources/QuantumWorks/',
  );
}

void _writePlatformArchive(
  File file,
  RobotopiaReleaseCatalogEntry release, {
  required String prefix,
  String? changedPath,
}) {
  final archive = Archive();
  for (final entry in release.mods.entries) {
    final relative = 'dist/${entry.key}-${entry.value}.robotopiamod';
    final bytes = File(
      p.join(file.parent.path, p.basename(relative)),
    ).readAsBytesSync();
    archive.addFile(ArchiveFile.bytes('$prefix$relative', bytes));
  }
  final indexBytes = utf8.encode(
    changedPath == 'dist/vpm/index.json'
        ? '{"changed":true}\n'
        : '{"packages":{}}\n',
  );
  archive.addFile(
    ArchiveFile.bytes('${prefix}dist/vpm/index.json', indexBytes),
  );
  for (final entry in release.vpmPackages.entries) {
    final relative = 'dist/vpm/${entry.key}-${entry.value}.zip';
    archive.addFile(
      ArchiveFile.string(
        '$prefix$relative',
        changedPath == relative
            ? 'changed\n'
            : 'vpm:${entry.key}:${entry.value}\n',
      ),
    );
  }
  file.writeAsBytesSync(ZipEncoder().encode(archive));
}

void _refreshChecksum(Directory directory, String name) {
  final sums = File(p.join(directory.path, 'SHA256SUMS'));
  final file = File(p.join(directory.path, name));
  final hash = sha256.convert(file.readAsBytesSync()).toString();
  final lines = sums.readAsLinesSync();
  final replacement = '$hash  $name';
  sums.writeAsStringSync(
    '${lines.map((line) => line.endsWith('  $name') ? replacement : line).join('\n')}\n',
  );
}

Map<String, Object?> _json(File file) =>
    (jsonDecode(file.readAsStringSync()) as Map).cast<String, Object?>();

void _writeJson(File file, Object value) => file.writeAsStringSync(
  '${const JsonEncoder.withIndent('  ').convert(value)}\n',
);

String _repositoryRoot() {
  var directory = Directory.current.absolute;
  while (!File(
    p.join(directory.path, 'RobotopiaModManager.slnx'),
  ).existsSync()) {
    if (directory.parent.path == directory.path) {
      throw StateError('Repository root not found.');
    }
    directory = directory.parent;
  }
  return directory.path;
}
