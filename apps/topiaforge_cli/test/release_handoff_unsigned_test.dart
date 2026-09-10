import 'dart:convert';
import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/release_handoff.dart';
import 'package:topiaforge/src/release_handoff_models.dart';

import 'release_handoff_qa_fixture.dart';

/// Covers the unsigned Windows distribution through the handoff contract.
///
/// `release_distribution_mode_test.dart` proves the policy records the mode.
/// This proves the rest of the contract then agrees with it: the recorded mode
/// used to reach `_requiredEvidenceFor` and `_signingState` while the QA
/// validator kept its own hardcoded `authenticode-timestamped` and its own
/// evidence-key list, so an unsigned candidate failed validation three
/// different ways with nothing exercising the path.
void main() {
  const version = '0.1.0-rc.1';
  const targetSha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
  const ecosystemSha =
      'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';

  late Directory temp;
  late String root;

  setUp(() {
    root = _repositoryRoot();
    temp = Directory.systemTemp.createTempSync('topiaforge-unsigned-handoff-');
  });

  tearDown(() {
    if (temp.existsSync()) temp.deleteSync(recursive: true);
  });

  Future<ReleaseHandoffVerification> run(String windowsDistribution) async {
    final candidate = _overlayRoot(root, temp, windowsDistribution);
    final assets = Directory(p.join(temp.path, 'assets-$windowsDistribution'))
      ..createSync(recursive: true);
    File(
      p.join(assets.path, 'TopiaForge-windows-x64.zip'),
    ).writeAsStringSync('archive:TopiaForge-windows-x64.zip\n');
    writeReleaseQaFixtures(
      repositoryRoot: candidate,
      assets: assets,
      version: version,
      targetSha: targetSha,
      ecosystemSha: ecosystemSha,
      windowsDistribution: windowsDistribution,
    );

    const contract = TopiaForgeReleaseHandoff();
    await contract.buildPlatformBundle(
      repositoryRoot: candidate,
      version: version,
      targetSha: targetSha,
      platform: 'windows-x64',
      archivePath: p.join(assets.path, 'TopiaForge-windows-x64.zip'),
      canonicalEcosystemSha256: ecosystemSha,
      evidenceSha256: releaseQaEvidenceFor(
        assets,
        'windows-x64',
        ecosystemSha: ecosystemSha,
        windowsDistribution: windowsDistribution,
      ),
      qaPath: releaseQaPath(assets, 'windows-x64'),
      outputPath: p.join(
        assets.path,
        releasePlatformBundleFileName('windows-x64'),
      ),
    );
    await contract.buildHandoff(
      repositoryRoot: candidate,
      version: version,
      targetSha: targetSha,
      assetsDirectory: assets.path,
    );
    return contract.verify(
      repositoryRoot: candidate,
      version: version,
      targetSha: targetSha,
      assetsDirectory: assets.path,
      trustOutputPath: p.join(assets.path, '.platform-trust-evidence.json'),
    );
  }

  test(
    'an unsigned Windows candidate builds and verifies its handoff',
    () async {
      final result = await run('unsigned');
      final windows = result.handoff.platformBundles.single;

      // The whole point of the mode: no Authenticode claim anywhere.
      expect(windows.validations.keys, isNot(contains('authenticode')));
      expect(windows.validations.keys.toSet(), {
        'ecosystem-reproducibility',
        'package',
        'robotopia',
        'toolchains',
        'unity',
      });
      expect(windows.signing.scheme, 'not-applicable');
      expect(windows.signing.status, 'unsigned');
      expect(windows.signing.exceptionApplied, isFalse);
    },
  );

  test('a signed Windows candidate still carries Authenticode', () async {
    final result = await run('signed');
    final windows = result.handoff.platformBundles.single;

    expect(windows.validations.keys, contains('authenticode'));
    expect(windows.signing.scheme, 'authenticode');
    expect(windows.signing.status, 'verified');
  });

  test(
    'an unsigned candidate is rejected if it sends Authenticode anyway',
    () async {
      final candidate = _overlayRoot(root, temp, 'unsigned');
      final assets = Directory(p.join(temp.path, 'assets-contradiction'))
        ..createSync(recursive: true);
      File(
        p.join(assets.path, 'TopiaForge-windows-x64.zip'),
      ).writeAsStringSync('archive:TopiaForge-windows-x64.zip\n');
      writeReleaseQaFixtures(
        repositoryRoot: candidate,
        assets: assets,
        version: version,
        targetSha: targetSha,
        ecosystemSha: ecosystemSha,
        windowsDistribution: 'unsigned',
      );

      // The exact defect this test exists for: unsigned policy, signed evidence.
      // It must fail loudly rather than publish a fabricated Authenticode claim.
      await expectLater(
        const TopiaForgeReleaseHandoff().buildPlatformBundle(
          repositoryRoot: candidate,
          version: version,
          targetSha: targetSha,
          platform: 'windows-x64',
          archivePath: p.join(assets.path, 'TopiaForge-windows-x64.zip'),
          canonicalEcosystemSha256: ecosystemSha,
          evidenceSha256: releaseQaEvidenceFor(
            assets,
            'windows-x64',
            ecosystemSha: ecosystemSha,
          ),
          qaPath: releaseQaPath(assets, 'windows-x64'),
          outputPath: p.join(
            assets.path,
            releasePlatformBundleFileName('windows-x64'),
          ),
        ),
        throwsA(isA<StateError>()),
      );
    },
  );
}

/// A repository root carrying the release inputs the handoff contract reads,
/// with the Windows distribution mode overridden.
///
/// Copying the five files it actually opens keeps the test honest about the
/// real schemas and the real pinned game build without needing a full checkout.
String _overlayRoot(String root, Directory temp, String windowsDistribution) {
  final candidate = Directory(p.join(temp.path, 'root-$windowsDistribution'))
    ..createSync(recursive: true);
  for (final relative in const [
    'release/catalog.json',
    'release/platform-toolchains.json',
    '.github/robotopia-game-build.json',
    'tests/live-game-acceptance.json',
  ]) {
    final source = File(p.join(root, relative));
    final destination = File(p.join(candidate.path, relative))
      ..createSync(recursive: true);
    destination.writeAsBytesSync(source.readAsBytesSync());
  }

  final policy =
      jsonDecode(
            File(
              p.join(root, 'release', 'release-policy.json'),
            ).readAsStringSync(),
          )
          as Map<String, Object?>;
  policy['signingIdentities'] = <String, Object?>{
    'windowsDistribution': windowsDistribution,
    if (windowsDistribution != 'unsigned')
      'windowsCertificateSha256': List.filled(64, 'a').join(),
  };
  File(p.join(candidate.path, 'release', 'release-policy.json'))
    ..createSync(recursive: true)
    ..writeAsStringSync(
      '${const JsonEncoder.withIndent('  ').convert(policy)}\n',
    );

  return candidate.path;
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
