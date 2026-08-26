import 'dart:convert';
import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/release_handoff.dart';
import 'package:topiaforge/src/release_handoff_models.dart';
import 'package:topiaforge/src/release_policy.dart';

import 'release_handoff_qa_fixture.dart';

void main() {
  const version = '0.1.0-rc.1';
  const targetSha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
  const ecosystemSha = _testEcosystemSha;
  late Directory temp;
  late String root;

  setUp(() {
    root = _repositoryRoot();
    temp = Directory.systemTemp.createTempSync('topiaforge-handoff-test-');
    _writeArchives(temp);
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

  test('builds and verifies the deterministic policy-target handoff', () async {
    await _buildPlatformBundles(
      root: root,
      assets: temp,
      targetSha: targetSha,
      ecosystemSha: ecosystemSha,
    );
    const contract = TopiaForgeReleaseHandoff();
    final handoffPath = await contract.buildHandoff(
      repositoryRoot: root,
      version: version,
      targetSha: targetSha,
      assetsDirectory: temp.path,
    );
    final firstBytes = File(handoffPath).readAsBytesSync();
    final trustFile = File(p.join(temp.path, '.platform-trust-evidence.json'));
    final result = await contract.verify(
      repositoryRoot: root,
      version: version,
      targetSha: targetSha,
      assetsDirectory: temp.path,
      trustOutputPath: trustFile.path,
    );

    expect(
      result.platformBundles.keys,
      TopiaForgeReleasePolicy.load(root).targetPlatforms,
    );
    expect(result.handoff.canonicalEcosystemSha256, ecosystemSha);
    expect(
      result.handoff.platformBundles.every(
        (bundle) =>
            bundle.validations['ecosystem-reproducibility']?.status == 'passed',
      ),
      isTrue,
    );
    expect(result.handoff.toolchains, {
      'dart': '3.12.2',
      'dotnetRuntime': '10.0.9',
      'dotnetSdk': '10.0.301',
      'flutter': '3.44.6',
      'node': '24.18.0',
      'unity': '6000.0.23f1',
    });
    expect(_json(trustFile), {
      'windows-x64': {'status': 'trusted', 'exceptionApplied': false},
    });

    await contract.buildHandoff(
      repositoryRoot: root,
      version: version,
      targetSha: targetSha,
      assetsDirectory: temp.path,
    );
    expect(File(handoffPath).readAsBytesSync(), firstBytes);
  });

  test(
    'platform manifests contain only scrubbed deterministic fields',
    () async {
      await _buildPlatformBundles(
        root: root,
        assets: temp,
        targetSha: targetSha,
        ecosystemSha: ecosystemSha,
      );
      final windows = _json(
        File(p.join(temp.path, releasePlatformBundleFileName('windows-x64'))),
      );
      expect(windows.keys.toSet(), {
        'schema',
        'version',
        'targetSha',
        'platform',
        'builderProfile',
        'archive',
        'canonicalEcosystemSha256',
        'toolchains',
        'platformToolchains',
        'signing',
        'validations',
        'qa',
      });
      expect(windows['builderProfile'], 'admin-windows');
      expect(windows['platformToolchains'], {
        'msvc': '14.51.36231',
        'windowsSdk': '10.0.26100.0',
      });
      expect(windows['signing'], {
        'scheme': 'authenticode',
        'status': 'verified',
        'notarization': 'not-applicable',
        'exceptionApplied': false,
      });
      final encoded = jsonEncode(windows).toLowerCase();
      for (final forbidden in const [
        'username',
        'hostname',
        'password',
        'credential',
        'rawlog',
        r'c:\',
        '/users/',
        '/home/',
      ]) {
        expect(encoded, isNot(contains(forbidden)));
      }
    },
  );

  test('rejects forbidden fields and arbitrary toolchain metadata', () async {
    await _buildPlatformBundles(
      root: root,
      assets: temp,
      targetSha: targetSha,
      ecosystemSha: ecosystemSha,
    );
    final windowsFile = File(
      p.join(temp.path, releasePlatformBundleFileName('windows-x64')),
    );
    final windows = _json(windowsFile)
      ..['timestamp'] = '2026-07-31T00:00:00Z'
      ..['rawLog'] = 'secret game log';
    _writeJson(windowsFile, windows);

    await expectLater(
      const TopiaForgeReleaseHandoff().buildHandoff(
        repositoryRoot: root,
        version: version,
        targetSha: targetSha,
        assetsDirectory: temp.path,
      ),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          contains('forbidden or missing fields'),
        ),
      ),
    );

    windows
      ..remove('timestamp')
      ..remove('rawLog');
    (windows['toolchains'] as Map)['hostname'] = 'builder.internal';
    _writeJson(windowsFile, windows);
    await expectLater(
      const TopiaForgeReleaseHandoff().buildHandoff(
        repositoryRoot: root,
        version: version,
        targetSha: targetSha,
        assetsDirectory: temp.path,
      ),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          contains('toolchains do not exactly match'),
        ),
      ),
    );

    (windows['toolchains'] as Map).remove('hostname');
    (windows['platformToolchains'] as Map)['msvc'] = 'unreviewed';
    _writeJson(windowsFile, windows);
    await expectLater(
      const TopiaForgeReleaseHandoff().buildHandoff(
        repositoryRoot: root,
        version: version,
        targetSha: targetSha,
        assetsDirectory: temp.path,
      ),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          contains('platformToolchains do not exactly match'),
        ),
      ),
    );
  });

  test('rejects missing evidence and invalid signing state', () async {
    const contract = TopiaForgeReleaseHandoff();
    await expectLater(
      contract.buildPlatformBundle(
        repositoryRoot: root,
        version: version,
        targetSha: targetSha,
        platform: 'windows-x64',
        archivePath: p.join(temp.path, 'TopiaForge-windows-x64.zip'),
        canonicalEcosystemSha256: ecosystemSha,
        evidenceSha256: {
          ...releaseQaEvidenceFor(
            temp,
            'windows-x64',
            ecosystemSha: ecosystemSha,
          ),
        }..remove('robotopia'),
        qaPath: releaseQaPath(temp, 'windows-x64'),
        outputPath: p.join(
          temp.path,
          releasePlatformBundleFileName('windows-x64'),
        ),
      ),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          contains('validations must be exactly'),
        ),
      ),
    );

    await _buildPlatformBundles(
      root: root,
      assets: temp,
      targetSha: targetSha,
      ecosystemSha: ecosystemSha,
    );
    final windowsFile = File(
      p.join(temp.path, releasePlatformBundleFileName('windows-x64')),
    );
    final windows = _json(windowsFile);
    (windows['signing'] as Map)['exceptionApplied'] = true;
    _writeJson(windowsFile, windows);
    await expectLater(
      contract.buildHandoff(
        repositoryRoot: root,
        version: version,
        targetSha: targetSha,
        assetsDirectory: temp.path,
      ),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          contains('signing state does not match release policy'),
        ),
      ),
    );
  });

  test('fails closed for target, ecosystem, and archive mismatches', () async {
    await _buildPlatformBundles(
      root: root,
      assets: temp,
      targetSha: targetSha,
      ecosystemSha: ecosystemSha,
    );
    final bundleFile = File(
      p.join(temp.path, releasePlatformBundleFileName('windows-x64')),
    );
    final bundle = _json(bundleFile);
    bundle['canonicalEcosystemSha256'] =
        'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc';
    ((bundle['validations'] as Map)['ecosystem-reproducibility']
            as Map)['evidenceSha256'] =
        'cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc';
    _writeJson(bundleFile, bundle);
    await expectLater(
      const TopiaForgeReleaseHandoff().buildHandoff(
        repositoryRoot: root,
        version: version,
        targetSha: targetSha,
        assetsDirectory: temp.path,
      ),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          contains('QA is for a different release'),
        ),
      ),
    );

    bundle['canonicalEcosystemSha256'] = ecosystemSha;
    ((bundle['validations'] as Map)['ecosystem-reproducibility']
            as Map)['evidenceSha256'] =
        ecosystemSha;
    ((bundle['validations'] as Map)['ecosystem-reproducibility']
            as Map)['evidenceSha256'] =
        'eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee';
    _writeJson(bundleFile, bundle);
    const contract = TopiaForgeReleaseHandoff();
    await expectLater(
      contract.buildHandoff(
        repositoryRoot: root,
        version: version,
        targetSha: targetSha,
        assetsDirectory: temp.path,
      ),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          contains('must equal canonicalEcosystemSha256'),
        ),
      ),
    );
    ((bundle['validations'] as Map)['ecosystem-reproducibility']
            as Map)['evidenceSha256'] =
        ecosystemSha;
    _writeJson(bundleFile, bundle);
    await contract.buildHandoff(
      repositoryRoot: root,
      version: version,
      targetSha: targetSha,
      assetsDirectory: temp.path,
    );
    File(
      p.join(temp.path, 'TopiaForge-windows-x64.zip'),
    ).writeAsStringSync('changed bytes');
    await expectLater(
      contract.verify(
        repositoryRoot: root,
        version: version,
        targetSha: targetSha,
        assetsDirectory: temp.path,
      ),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          contains('does not match its recorded bytes'),
        ),
      ),
    );
    await expectLater(
      contract.verify(
        repositoryRoot: root,
        version: version,
        targetSha: 'dddddddddddddddddddddddddddddddddddddddd',
        assetsDirectory: temp.path,
      ),
      throwsA(isA<StateError>()),
    );
  });

  test('immutable outputs reject a different exact rerun', () async {
    const contract = TopiaForgeReleaseHandoff();
    final output = p.join(
      temp.path,
      releasePlatformBundleFileName('windows-x64'),
    );
    await contract.buildPlatformBundle(
      repositoryRoot: root,
      version: version,
      targetSha: targetSha,
      platform: 'windows-x64',
      archivePath: p.join(temp.path, 'TopiaForge-windows-x64.zip'),
      canonicalEcosystemSha256: ecosystemSha,
      evidenceSha256: releaseQaEvidenceFor(
        temp,
        'windows-x64',
        ecosystemSha: ecosystemSha,
      ),
      qaPath: releaseQaPath(temp, 'windows-x64'),
      outputPath: output,
    );
    File(
      p.join(temp.path, 'TopiaForge-windows-x64.zip'),
    ).writeAsStringSync('replacement');
    writeReleaseQaFixtures(
      repositoryRoot: root,
      assets: temp,
      version: version,
      targetSha: targetSha,
      ecosystemSha: ecosystemSha,
    );
    await expectLater(
      contract.buildPlatformBundle(
        repositoryRoot: root,
        version: version,
        targetSha: targetSha,
        platform: 'windows-x64',
        archivePath: p.join(temp.path, 'TopiaForge-windows-x64.zip'),
        canonicalEcosystemSha256: ecosystemSha,
        evidenceSha256: releaseQaEvidenceFor(
          temp,
          'windows-x64',
          ecosystemSha: ecosystemSha,
        ),
        qaPath: releaseQaPath(temp, 'windows-x64'),
        outputPath: output,
      ),
      throwsA(
        isA<StateError>().having(
          (error) => error.message,
          'message',
          contains('Refusing to replace different'),
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
    final archiveName = releaseArchiveForPlatform(platform);
    await contract.buildPlatformBundle(
      repositoryRoot: root,
      version: '0.1.0-rc.1',
      targetSha: targetSha,
      platform: platform,
      archivePath: p.join(assets.path, archiveName),
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

void _writeArchives(Directory assets) {
  for (final name in const ['TopiaForge-windows-x64.zip']) {
    File(p.join(assets.path, name)).writeAsStringSync('archive:$name\n');
  }
}

Map<String, Object?> _json(File file) =>
    (jsonDecode(file.readAsStringSync()) as Map).cast<String, Object?>();

void _writeJson(File file, Map<String, Object?> value) {
  file.writeAsStringSync(
    '${const JsonEncoder.withIndent('  ').convert(value)}\n',
  );
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

const _testEcosystemSha =
    'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';
