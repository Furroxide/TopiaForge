import 'dart:convert';
import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/release_handoff.dart';
import 'package:topiaforge/src/release_handoff_models.dart';
import 'package:topiaforge/src/release_policy.dart';

import 'release_handoff_qa_fixture.dart';

void main() {
  const version = '1.0.0-rc.1';
  const targetSha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
  const ecosystemSha =
      'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';
  late Directory temp;
  late String root;

  setUp(() {
    root = _repositoryRoot();
    temp = Directory.systemTemp.createTempSync('topiaforge-game-identity-');
    for (final platform in const ['linux-x64', 'windows-x64']) {
      File(
        p.join(temp.path, releaseArchiveForPlatform(platform)),
      ).writeAsStringSync('archive:$platform\n');
    }
    writeReleaseQaFixtures(
      repositoryRoot: root,
      assets: temp,
      version: version,
      targetSha: targetSha,
      ecosystemSha: ecosystemSha,
    );
  });

  tearDown(() {
    if (temp.existsSync()) temp.deleteSync(recursive: true);
  });

  test(
    'rejects QA that is not bound to the pinned official game bytes',
    () async {
      final linuxQa = File(releaseQaPath(temp, 'linux-x64'));
      final linux = _readJson(linuxQa)
        ..['gameExecutableSha256'] =
            'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc';
      _writeJson(linuxQa, linux);
      await _expectGameIdentityFailure('linux-x64', root, temp);

      writeReleaseQaFixtures(
        repositoryRoot: root,
        assets: temp,
        version: version,
        targetSha: targetSha,
        ecosystemSha: ecosystemSha,
      );
      final windowsQa = File(releaseQaPath(temp, 'windows-x64'));
      final windows = _readJson(windowsQa);
      (windows['robotopia'] as Map)['gameFilesManifestSha256'] =
          'dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd';
      _writeJson(windowsQa, windows);
      await _expectGameIdentityFailure('windows-x64', root, temp);
    },
  );
}

Future<void> _expectGameIdentityFailure(
  String platform,
  String root,
  Directory assets,
) async {
  const ecosystemSha =
      'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';
  await expectLater(
    const TopiaForgeReleaseHandoff().buildPlatformBundle(
      repositoryRoot: root,
      version: '1.0.0-rc.1',
      targetSha: 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
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
    ),
    throwsA(
      isA<StateError>().having(
        (error) => error.message,
        'message',
        anyOf(
          contains('Linux QA runtime claims are invalid'),
          contains('Windows Robotopia QA is incomplete or invalid'),
        ),
      ),
    ),
  );
}

Map<String, Object?> _readJson(File file) =>
    (jsonDecode(file.readAsStringSync()) as Map).cast<String, Object?>();

void _writeJson(File file, Map<String, Object?> value) {
  file.writeAsStringSync('${jsonEncode(value)}\n');
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
