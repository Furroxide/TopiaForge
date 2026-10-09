import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/release_handoff.dart';
import 'package:topiaforge/src/release_handoff_models.dart';

import 'release_handoff_policy_fixture.dart';
import 'release_handoff_qa_fixture.dart';

/// Covers the truthful not-run Windows game receipt through the handoff
/// contract. The owner's P0-GAME-01 disposition makes live acceptance optional
/// for RC1; a candidate that skipped it must say so exactly, carry no game
/// evidence, and never borrow the shape or evidence of a performed run.
void main() {
  const version = '0.1.0-rc.1';
  const targetSha = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';
  const ecosystemSha =
      'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb';
  const contract = TopiaForgeReleaseHandoff();
  late Directory temp;
  late String root;

  setUp(() {
    temp = Directory.systemTemp.createTempSync('topiaforge-not-run-handoff-');
    root = writeSignedReleaseHandoffRoot(temp);
    File(
      p.join(temp.path, 'TopiaForge-windows-x64.zip'),
    ).writeAsStringSync('archive:TopiaForge-windows-x64.zip\n');
  });

  tearDown(() {
    if (temp.existsSync()) temp.deleteSync(recursive: true);
  });

  File qaFile() => File(releaseQaPath(temp, 'windows-x64'));
  File bundleFile() =>
      File(p.join(temp.path, releasePlatformBundleFileName('windows-x64')));

  void writeQa({required bool run, String distribution = 'signed'}) =>
      writeReleaseQaFixtures(
        repositoryRoot: root,
        assets: temp,
        version: version,
        targetSha: targetSha,
        ecosystemSha: ecosystemSha,
        windowsDistribution: distribution,
        liveGameAcceptanceRun: run,
      );

  Future<String> bundle({
    required bool runEvidence,
    String distribution = 'signed',
    Map<String, String>? evidence,
  }) => contract.buildPlatformBundle(
    repositoryRoot: root,
    version: version,
    targetSha: targetSha,
    platform: 'windows-x64',
    archivePath: p.join(temp.path, 'TopiaForge-windows-x64.zip'),
    canonicalEcosystemSha256: ecosystemSha,
    evidenceSha256:
        evidence ??
        releaseQaEvidenceFor(
          temp,
          'windows-x64',
          ecosystemSha: ecosystemSha,
          windowsDistribution: distribution,
          liveGameAcceptanceRun: runEvidence,
        ),
    qaPath: qaFile().path,
    outputPath: bundleFile().path,
  );

  Future<ReleaseHandoffVerification> handoff() async {
    await contract.buildHandoff(
      repositoryRoot: root,
      version: version,
      targetSha: targetSha,
      assetsDirectory: temp.path,
    );
    return contract.verify(
      repositoryRoot: root,
      version: version,
      targetSha: targetSha,
      assetsDirectory: temp.path,
    );
  }

  void editQaReceipt(void Function(Map<String, Object?> receipt) edit) {
    final qa = _json(qaFile());
    edit((qa['robotopia']! as Map).cast<String, Object?>());
    _writeJson(qaFile(), qa);
  }

  Matcher refusal(String message) => throwsA(
    isA<StateError>().having(
      (error) => error.message,
      'message',
      contains(message),
    ),
  );

  test('a not-run candidate seals a handoff with no game evidence', () async {
    writeQa(run: false);
    await bundle(runEvidence: false);
    final result = await handoff();
    final windows = result.platformBundles['windows-x64']!;
    expect(windows.validations.keys.toSet(), {
      'authenticode',
      'ecosystem-reproducibility',
      'package',
      'toolchains',
      'unity',
    });
    expect(
      result.handoff.platformBundles.single.validations.keys,
      isNot(contains('robotopia')),
    );
    final game = _json(File(p.join(root, '.github/robotopia-game-build.json')));
    final manifest = game['windowsFilesManifest']! as Map;
    expect(windows.qa['robotopia'], {
      'result': 'not-run',
      'gameArchiveSha256':
          ((game['archives']! as Map)['windows'] as Map)['sha256'],
      'gameExecutableSha256': manifest['gameExecutableSha256'],
      'gameFilesManifestSha256': manifest['sha256'],
      'gameFilesVerified': manifest['fileCount'],
      'caseInventorySha256': sha256
          .convert(
            File(
              p.join(root, 'tests', 'live-game-acceptance.json'),
            ).readAsBytesSync(),
          )
          .toString(),
    });
    // The pinned Unity authoring cycles stay a mandatory build check.
    expect((windows.qa['unity']! as Map)['cycles'], 16);
  });

  test('an unsigned not-run candidate drops only authenticode and game '
      'evidence', () async {
    final policyFile = File(p.join(root, 'release', 'release-policy.json'));
    _writeJson(
      policyFile,
      _json(policyFile)
        ..['signingIdentities'] = {'windowsDistribution': 'unsigned'},
    );
    writeQa(run: false, distribution: 'unsigned');
    await bundle(runEvidence: false, distribution: 'unsigned');
    final windows = (await handoff()).platformBundles['windows-x64']!;
    expect(windows.validations.keys.toSet(), {
      'ecosystem-reproducibility',
      'package',
      'toolchains',
      'unity',
    });
    expect(windows.signing.status, 'unsigned');
  });

  test('a not-run receipt with game evidence is refused', () async {
    writeQa(run: false);
    await expectLater(
      bundle(runEvidence: true),
      refusal('validations must be exactly'),
    );
  });

  test('a pass receipt without game evidence is refused', () async {
    writeQa(run: true);
    await expectLater(
      bundle(runEvidence: false),
      refusal('validations must be exactly'),
    );
  });

  test('the pinned Unity authoring evidence stays mandatory', () async {
    writeQa(run: false);
    final withoutUnity = releaseQaEvidenceFor(
      temp,
      'windows-x64',
      ecosystemSha: ecosystemSha,
      liveGameAcceptanceRun: false,
    )..remove('unity');
    await expectLater(
      bundle(runEvidence: false, evidence: withoutUnity),
      refusal('validations must be exactly'),
    );
    final qa = _json(qaFile());
    (qa['unity']! as Map)['cycles'] = 15;
    _writeJson(qaFile(), qa);
    await expectLater(
      bundle(runEvidence: false),
      refusal('Windows Unity QA is incomplete or invalid'),
    );
  });

  for (final field in const [
    'gameArchiveSha256',
    'gameExecutableSha256',
    'gameFilesManifestSha256',
    'caseInventorySha256',
  ]) {
    test('a not-run receipt must bind the exact $field', () async {
      writeQa(run: false);
      editQaReceipt((receipt) => receipt[field] = 'd' * 64);
      await expectLater(
        bundle(runEvidence: false),
        refusal('does not bind the pinned game and the tagged case inventory'),
      );
    });
  }

  test('a not-run receipt must bind the verified file count', () async {
    writeQa(run: false);
    editQaReceipt(
      (receipt) => receipt['gameFilesVerified'] =
          (receipt['gameFilesVerified']! as int) + 1,
    );
    await expectLater(
      bundle(runEvidence: false),
      refusal('does not bind the pinned game and the tagged case inventory'),
    );
  });

  for (final entry in <String, Object?>{
    'evidenceSha256': 'e' * 64,
    'suite': 'full',
    'passedCases': ['case.one'],
    'failures': <String>[],
  }.entries) {
    test('a not-run receipt cannot carry the run field ${entry.key}', () async {
      writeQa(run: false);
      editQaReceipt((receipt) => receipt[entry.key] = entry.value);
      await expectLater(
        bundle(runEvidence: false),
        refusal('forbidden or missing fields'),
      );
    });
  }

  for (final field in const [
    'gameArchiveSha256',
    'gameExecutableSha256',
    'gameFilesManifestSha256',
    'gameFilesVerified',
    'caseInventorySha256',
  ]) {
    test('a not-run receipt cannot omit $field', () async {
      writeQa(run: false);
      editQaReceipt((receipt) => receipt.remove(field));
      await expectLater(
        bundle(runEvidence: false),
        refusal('forbidden or missing fields'),
      );
    });
  }

  test('neither receipt shape can claim the other result', () async {
    writeQa(run: true);
    editQaReceipt((receipt) => receipt['result'] = 'not-run');
    await expectLater(
      bundle(runEvidence: false),
      refusal('forbidden or missing fields'),
    );
    writeQa(run: false);
    editQaReceipt((receipt) => receipt['result'] = 'pass');
    await expectLater(
      bundle(runEvidence: true),
      refusal('forbidden or missing fields'),
    );
  });

  for (final run in const [true, false]) {
    test('a ${run ? 'performed' : 'not-run'} bundle cannot be summarized '
        'with the other evidence set', () async {
      writeQa(run: run);
      await bundle(runEvidence: run);
      await handoff();
      final handoffFile = File(p.join(temp.path, releaseHandoffFileName));
      final manifest = _json(handoffFile);
      final validations =
          ((manifest['platformBundles']! as List).single as Map)['validations']
              as Map;
      if (run) {
        validations.remove('robotopia');
      } else {
        validations['robotopia'] = {
          'status': 'passed',
          'evidenceSha256': 'f' * 64,
        };
      }
      _writeJson(handoffFile, manifest);
      await expectLater(
        contract.verify(
          repositoryRoot: root,
          version: version,
          targetSha: targetSha,
          assetsDirectory: temp.path,
        ),
        refusal('handoff summary differs from its platform bundle'),
      );
    });
  }
}

Map<String, Object?> _json(File file) =>
    (jsonDecode(file.readAsStringSync()) as Map).cast<String, Object?>();

void _writeJson(File file, Map<String, Object?> value) {
  file.writeAsStringSync(
    '${const JsonEncoder.withIndent('  ').convert(value)}\n',
  );
}
