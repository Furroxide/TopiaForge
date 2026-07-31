import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:path/path.dart' as p;

String releaseQaPath(Directory assets, String platform) => p.join(
  assets.path,
  platform == 'linux-x64' ? 'proton-evidence.json' : 'windows-qa-summary.json',
);

void writeReleaseQaFixtures({
  required String repositoryRoot,
  required Directory assets,
  required String version,
  required String targetSha,
  required String ecosystemSha,
}) {
  final inventoryBytes = File(
    p.join(repositoryRoot, 'tests', 'live-game-acceptance.json'),
  ).readAsBytesSync();
  final inventory = jsonDecode(utf8.decode(inventoryBytes)) as Map;
  final liveCases = _caseIds(inventory['cases']);
  final creator = inventory['creatorAcceptance'] as Map;
  final creatorCases = _caseIds(creator['cases']);
  final inventorySha = sha256.convert(inventoryBytes).toString();
  final gameMetadata =
      jsonDecode(
            File(
              p.join(repositoryRoot, '.github', 'robotopia-game-build.json'),
            ).readAsStringSync(),
          )
          as Map;
  final windowsGameArchive =
      (gameMetadata['archives'] as Map)['windows'] as Map;
  final windowsFilesManifest = gameMetadata['windowsFilesManifest'] as Map;
  final linuxArchive = File(p.join(assets.path, 'TopiaForge-linux-x64.zip'));
  final windowsArchive = File(
    p.join(assets.path, 'TopiaForge-windows-x64.zip'),
  );

  _writeJson(File(releaseQaPath(assets, 'linux-x64')), {
    'schema': 'release-proton-evidence-v1',
    'version': version,
    'targetSha': targetSha,
    'platform': 'linux-proton',
    'archiveSha256': _fileSha(linuxArchive),
    'archiveSize': linuxArchive.lengthSync(),
    'canonicalEcosystemSha256': ecosystemSha,
    'gameBuildId': 2309,
    'gameArchiveSha256': windowsGameArchive['sha256'],
    'gameFilesManifestSha256': windowsFilesManifest['sha256'],
    'gameFilesVerified': windowsFilesManifest['fileCount'],
    'result': 'pass',
    'suite': 'full',
    'protonVersion': '10.0-4',
    'protonAppId': 3658110,
    'protonDepotId': 3658111,
    'protonManifestId': '5413949673798237105',
    'protonBuildId': 21617411,
    'protonSourceCommit': 'e2becb87430ca3ff510d949d9e75fa9b401da489',
    'protonRuntimeSha256': _digest('linux:proton-runtime'),
    'executionEnvironment': 'wsl2-wslg',
    'runtime': 'windows-x64-via-proton',
    'winDllOverrides': 'winhttp=n,b',
    'independentQa': false,
    'caseInventorySha256': inventorySha,
    'requiredCases': liveCases,
    'requiredCasesSha256': _caseSetSha(liveCases),
    'passedCases': liveCases,
    'passedCasesSha256': _caseSetSha(liveCases),
    'failures': <String>[],
    'releaseJourney': _releaseJourney,
    'acceptanceResultSha256': _digest('linux:acceptance-result'),
    'gameExecutableSha256': windowsFilesManifest['gameExecutableSha256'],
    'runtimeConfigurationSha256': _digest('linux:runtime-configuration'),
    'wineCommandSha256': _digest('linux:wine-command'),
    'evidenceSha256': _digest('linux:evidence-bundle'),
    'evidenceSize': 4096,
  });

  final validationSha = _digest('windows:validation');
  _writeJson(File(releaseQaPath(assets, 'windows-x64')), {
    'schema': 'release-windows-qa-summary-v1',
    'version': version,
    'targetSha': targetSha,
    'platform': 'windows',
    'archiveSha256': _fileSha(windowsArchive),
    'archiveSize': windowsArchive.lengthSync(),
    'canonicalEcosystemSha256': ecosystemSha,
    'signingState': 'authenticode-timestamped',
    'toolchains': {
      'dart': '3.12.2',
      'dotnetRuntime': '10.0.9',
      'dotnetSdk': '10.0.301',
      'flutter': '3.44.6',
      'node': '24.18.0',
      'unity': '6000.0.23f1',
      'msvc': '14.51.36231',
      'windowsSdk': '10.0.26100.0',
    },
    'gameBuildId': 2309,
    'validationDescriptorSha256': validationSha,
    'unity': {
      'result': 'pass',
      'editorVersion': '6000.0.23f1',
      'cycles': 16,
      'validatorSmoke': true,
      'evidenceSha256': _digest('windows-x64:unity'),
    },
    'robotopia': {
      'result': 'pass',
      'suite': 'full',
      'gameArchiveSha256': windowsGameArchive['sha256'],
      'gameExecutableSha256': windowsFilesManifest['gameExecutableSha256'],
      'gameFilesManifestSha256': windowsFilesManifest['sha256'],
      'gameFilesVerified': windowsFilesManifest['fileCount'],
      'caseInventorySha256': inventorySha,
      'requiredCases': liveCases,
      'requiredCasesSha256': _caseSetSha(liveCases),
      'passedCases': liveCases,
      'passedCasesSha256': _caseSetSha(liveCases),
      'missingCases': <String>[],
      'failures': <String>[],
      'releaseJourney': _releaseJourney,
      'evidenceSha256': _digest('windows-x64:robotopia'),
    },
    'creator': {
      'result': 'pass',
      'suite': 'creator-full',
      'caseInventorySha256': inventorySha,
      'requiredCases': creatorCases,
      'requiredCasesSha256': _caseSetSha(creatorCases),
      'passedCases': creatorCases,
      'passedCasesSha256': _caseSetSha(creatorCases),
      'lifecycleCycles': creator['minimumLifecycleCycles'],
      'saveStateUnchanged': true,
      'checkpointStateUnchanged': true,
      'failures': <String>[],
      'descriptorSha256': _digest('windows-x64:creator-descriptor'),
      'evidenceSha256': _digest('windows-x64:creator-bundle'),
      'evidenceSize': 8192,
    },
  });
}

Map<String, String> releaseQaEvidenceFor(
  Directory assets,
  String platform, {
  required String ecosystemSha,
}) {
  if (platform == 'linux-x64') {
    final validationSha = _digest('linux-x64:validation');
    return {
      'ecosystem-reproducibility': ecosystemSha,
      'package': validationSha,
      'proton': _fileSha(File(releaseQaPath(assets, platform))),
      'toolchains': validationSha,
    };
  }
  if (platform == 'windows-x64') {
    final validationSha = _digest('windows:validation');
    return {
      'creator': _digest('windows-x64:creator-descriptor'),
      'ecosystem-reproducibility': ecosystemSha,
      'package': validationSha,
      'robotopia': _digest('windows-x64:robotopia'),
      'toolchains': validationSha,
      'authenticode': validationSha,
      'unity': _digest('windows-x64:unity'),
    };
  }
  throw StateError('Unsupported QA fixture platform: $platform');
}

const _releaseJourney = <String, Object?>{
  'enabled': true,
  'authoringCommandCount': 2,
  'loadedPackageStatus': 'loaded',
  'logMarkerObserved': true,
};

List<String> _caseIds(Object? value) {
  final ids = [
    for (final entry in value! as List) (entry as Map)['id']! as String,
  ]..sort();
  return ids;
}

String _caseSetSha(List<String> cases) =>
    sha256.convert(utf8.encode('${cases.join('\n')}\n')).toString();

String _fileSha(File file) => sha256.convert(file.readAsBytesSync()).toString();

String _digest(String value) => sha256.convert(utf8.encode(value)).toString();

void _writeJson(File file, Map<String, Object?> value) {
  file.writeAsStringSync(
    '${const JsonEncoder.withIndent('  ').convert(value)}\n',
  );
}
