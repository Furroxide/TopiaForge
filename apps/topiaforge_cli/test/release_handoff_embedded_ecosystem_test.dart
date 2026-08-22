import 'dart:convert';
import 'dart:io';

import 'package:archive/archive_io.dart';
import 'package:crypto/crypto.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/release_handoff.dart';
import 'package:topiaforge/src/release_handoff_models.dart';
import 'package:topiaforge/src/release_policy.dart';

import 'release_handoff_qa_fixture.dart';

void main() {
  const version = '0.1.0-rc.1';
  const targetSha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
  late Directory temp;
  late String root;

  setUp(() {
    root = _repositoryRoot();
    temp = Directory.systemTemp.createTempSync(
      'topiaforge-handoff-embedded-test-',
    );
  });

  tearDown(() {
    if (temp.existsSync()) temp.deleteSync(recursive: true);
  });

  test(
    'option recomputes normalized ecosystem identity from every archive',
    () async {
      final release = TopiaForgeReleaseCatalog.load(root).release(version);
      final ecosystemFiles = _ecosystemFiles(release);
      final embeddedSha = _canonicalTreeDigest(ecosystemFiles);
      _writeEcosystemArchives(temp, ecosystemFiles);
      writeReleaseQaFixtures(
        repositoryRoot: root,
        assets: temp,
        version: version,
        targetSha: targetSha,
        ecosystemSha: embeddedSha,
      );
      await _buildPlatformBundles(
        root: root,
        assets: temp,
        targetSha: targetSha,
        ecosystemSha: embeddedSha,
      );
      const contract = TopiaForgeReleaseHandoff();
      await contract.buildHandoff(
        repositoryRoot: root,
        version: version,
        targetSha: targetSha,
        assetsDirectory: temp.path,
      );
      final verification = await contract.verify(
        repositoryRoot: root,
        version: version,
        targetSha: targetSha,
        assetsDirectory: temp.path,
        verifyEmbeddedEcosystem: true,
      );
      expect(
        verification.platformBundles.map(
          (platform, bundle) => MapEntry(platform, bundle.platformToolchains),
        ),
        {
          'windows-x64': {'msvc': '14.51.36231', 'windowsSdk': '10.0.26100.0'},
        },
      );

      final mismatched = Directory(p.join(temp.path, 'mismatched'))
        ..createSync();
      _writeEcosystemArchives(
        mismatched,
        ecosystemFiles,
        changedPlatform: 'windows-x64',
      );
      writeReleaseQaFixtures(
        repositoryRoot: root,
        assets: mismatched,
        version: version,
        targetSha: targetSha,
        ecosystemSha: embeddedSha,
      );
      await _buildPlatformBundles(
        root: root,
        assets: mismatched,
        targetSha: targetSha,
        ecosystemSha: embeddedSha,
      );
      await contract.buildHandoff(
        repositoryRoot: root,
        version: version,
        targetSha: targetSha,
        assetsDirectory: mismatched.path,
      );
      await expectLater(
        contract.verify(
          repositoryRoot: root,
          version: version,
          targetSha: targetSha,
          assetsDirectory: mismatched.path,
          verifyEmbeddedEcosystem: true,
        ),
        throwsA(
          isA<StateError>().having(
            (error) => error.message,
            'message',
            contains('embedded ecosystem digest does not match'),
          ),
        ),
      );
    },
  );

  test('platform toolchain policy rejects unreviewed fields', () async {
    final fixtureRoot = Directory(p.join(temp.path, 'policy-fixture'));
    final fixtureRelease = Directory(p.join(fixtureRoot.path, 'release'))
      ..createSync(recursive: true);
    for (final name in const [
      'release-policy.json',
      'catalog.json',
      'platform-toolchains.json',
    ]) {
      File(
        p.join(root, 'release', name),
      ).copySync(p.join(fixtureRelease.path, name));
    }
    final pinsFile = File(
      p.join(fixtureRelease.path, 'platform-toolchains.json'),
    );
    final pins = (jsonDecode(pinsFile.readAsStringSync()) as Map)
        .cast<String, Object?>();
    (pins['windows'] as Map)['hostname'] = 'builder.internal';
    pinsFile.writeAsStringSync('${jsonEncode(pins)}\n');
    final archive = File(p.join(temp.path, 'TopiaForge-windows-x64.zip'))
      ..writeAsStringSync('archive');

    await expectLater(
      const TopiaForgeReleaseHandoff().buildPlatformBundle(
        repositoryRoot: fixtureRoot.path,
        version: version,
        targetSha: targetSha,
        platform: 'windows-x64',
        archivePath: archive.path,
        canonicalEcosystemSha256:
            'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
        evidenceSha256: {
          'authenticode':
              'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc',
          'creator':
              '2222222222222222222222222222222222222222222222222222222222222222',
          'ecosystem-reproducibility':
              'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',
          'package':
              'dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd',
          'robotopia':
              'eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee',
          'toolchains':
              'ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff',
          'unity':
              '1111111111111111111111111111111111111111111111111111111111111111',
        },
        qaPath: 'unused',
        outputPath: 'unused',
      ),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          contains('forbidden or missing fields'),
        ),
      ),
    );
  });
}

Future<void> _buildPlatformBundles({
  required String root,
  required Directory assets,
  required String targetSha,
  required String ecosystemSha,
}) async {
  const contract = TopiaForgeReleaseHandoff();
  for (final platform in TopiaForgeReleasePolicy.load(root).targetPlatforms) {
    await contract.buildPlatformBundle(
      repositoryRoot: root,
      version: '0.1.0-rc.1',
      targetSha: targetSha,
      platform: platform,
      archivePath: p.join(assets.path, releaseArchiveForPlatform(platform)),
      canonicalEcosystemSha256: ecosystemSha,
      evidenceSha256: releaseQaEvidenceFor(
        assets,
        platform,
        ecosystemSha: ecosystemSha,
      ),
      qaPath: releaseQaPath(assets, platform),
      outputPath: p.join(assets.path, releasePlatformBundleFileName(platform)),
    );
  }
}

Map<String, List<int>> _ecosystemFiles(TopiaForgeReleaseCatalogEntry release) =>
    {
      for (final entry in release.mods.entries)
        '${entry.key}-${entry.value}.topiaforgemod': utf8.encode(
          'mod:${entry.key}:${entry.value}\n',
        ),
      'vpm/index.json': utf8.encode('{"packages":{}}\n'),
      for (final entry in release.vpmPackages.entries)
        'vpm/${entry.key}-${entry.value}.zip': utf8.encode(
          'vpm:${entry.key}:${entry.value}\n',
        ),
    };

String _canonicalTreeDigest(Map<String, List<int>> files) {
  final lines = <String>[
    for (final path in files.keys.toList()..sort())
      '${sha256.convert(files[path]!)}  ./$path',
  ];
  return sha256.convert(utf8.encode('${lines.join('\n')}\n')).toString();
}

void _writeEcosystemArchives(
  Directory assets,
  Map<String, List<int>> files, {
  String? changedPlatform,
}) {
  const archives = <String, String>{
    'windows-x64': 'TopiaForge-windows-x64.zip',
  };
  for (final archiveEntry in archives.entries) {
    final archive = Archive();
    for (final entry in files.entries) {
      final bytes =
          changedPlatform == archiveEntry.key &&
              entry.key.endsWith('.topiaforgemod')
          ? utf8.encode('changed:${entry.key}\n')
          : entry.value;
      archive.addFile(
        ArchiveFile.bytes(
          'TopiaForge-${archiveEntry.key}/dist/${entry.key}',
          bytes,
        ),
      );
    }
    File(
      p.join(assets.path, archiveEntry.value),
    ).writeAsBytesSync(ZipEncoder().encode(archive));
  }
}

String _repositoryRoot() {
  var directory = Directory.current.absolute;
  while (!File(p.join(directory.path, 'TopiaForge.slnx')).existsSync()) {
    if (directory.parent.path == directory.path) {
      throw StateError('Repository root not found.');
    }
    directory = directory.parent;
  }
  return directory.path;
}
