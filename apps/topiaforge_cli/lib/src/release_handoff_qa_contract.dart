part of 'release_handoff.dart';

class _ReleaseQaSource {
  const _ReleaseQaSource({required this.json, required this.sha256});

  final Map<String, Object?> json;
  final String sha256;
}

class _ReleaseQaCaseInventory {
  const _ReleaseQaCaseInventory({
    required this.sha256,
    required this.liveCases,
    required this.liveCasesSha256,
    required this.creatorCases,
    required this.creatorCasesSha256,
    required this.creatorMinimumCycles,
  });

  final String sha256;
  final List<String> liveCases;
  final String liveCasesSha256;
  final List<String> creatorCases;
  final String creatorCasesSha256;
  final int creatorMinimumCycles;
}

class _RobotopiaGameIdentity {
  const _RobotopiaGameIdentity({
    required this.archiveSha256,
    required this.filesManifestSha256,
    required this.filesVerified,
    required this.gameExecutableSha256,
  });

  final String archiveSha256;
  final String filesManifestSha256;
  final int filesVerified;
  final String gameExecutableSha256;
}

_ReleaseQaSource _readQaSource(File file) {
  final bytes = readBoundedRegularFileSync(
    file,
    maxBytes: CliFileLimits.metadata,
  );
  final Object? decoded;
  try {
    decoded = jsonDecode(utf8.decode(bytes, allowMalformed: false));
  } on FormatException catch (error) {
    throw StateError('QA descriptor must be valid UTF-8 JSON: $error');
  }
  if (decoded is! Map) {
    throw StateError('QA descriptor must contain a JSON object.');
  }
  return _ReleaseQaSource(
    json: decoded.map((key, value) => MapEntry(key.toString(), value)),
    sha256: sha256.convert(bytes).toString(),
  );
}

void _validatePlatformQa(
  ReleasePlatformBundle bundle,
  _ReleaseHandoffContext context,
) {
  final inventory = _loadReleaseQaCaseInventory(
    context.policy.repositoryRoot,
    context.policy.gameBuildId,
  );
  final gameIdentity = _loadRobotopiaGameIdentity(context.policy);
  switch (bundle.platform) {
    case 'linux-x64':
      _validateLinuxQa(bundle, context, inventory, gameIdentity);
    case 'windows-x64':
      _validateWindowsQa(bundle, context, inventory, gameIdentity);
    default:
      throw StateError('QA is not defined for ${bundle.platform}.');
  }
}

void _validateLinuxQa(
  ReleasePlatformBundle bundle,
  _ReleaseHandoffContext context,
  _ReleaseQaCaseInventory inventory,
  _RobotopiaGameIdentity gameIdentity,
) {
  final qa = bundle.qa;
  _requireQaReleaseIdentity(
    qa,
    bundle,
    context,
    expectedPlatform: 'linux-proton',
  );
  if (qa['schema'] != releasePlatformQaLinuxSchema ||
      qa['sourceSchema'] != releaseProtonEvidenceSchema ||
      qa['result'] != 'pass' ||
      qa['suite'] != 'full' ||
      qa['gameBuildId'] != context.policy.gameBuildId ||
      qa['gameArchiveSha256'] != gameIdentity.archiveSha256 ||
      qa['gameFilesManifestSha256'] != gameIdentity.filesManifestSha256 ||
      qa['gameFilesVerified'] != gameIdentity.filesVerified ||
      qa['gameExecutableSha256'] != gameIdentity.gameExecutableSha256 ||
      qa['protonVersion'] !=
          context.platformToolchains['linux-x64']!['proton'] ||
      qa['protonAppId'] != 3658110 ||
      qa['protonDepotId'] != 3658111 ||
      qa['protonManifestId'] != '5413949673798237105' ||
      qa['protonBuildId'] != 21617411 ||
      qa['protonSourceCommit'] != 'e2becb87430ca3ff510d949d9e75fa9b401da489' ||
      qa['executionEnvironment'] != 'wsl2-wslg' ||
      qa['runtime'] != 'windows-x64-via-proton' ||
      qa['winDllOverrides'] != 'winhttp=n,b' ||
      qa['independentQa'] != false) {
    throw StateError('Linux QA runtime claims are invalid.');
  }
  _requireQaPlatformToolchainPin(
    context,
    'protonSteamAppId',
    '${qa['protonAppId']}',
  );
  _requireQaPlatformToolchainPin(
    context,
    'protonSteamDepotId',
    '${qa['protonDepotId']}',
  );
  _requireQaPlatformToolchainPin(
    context,
    'protonSteamManifestId',
    '${qa['protonManifestId']}',
  );
  _requireQaPlatformToolchainPin(
    context,
    'protonSteamBuildId',
    '${qa['protonBuildId']}',
  );
  _requireQaPlatformToolchainPin(
    context,
    'protonSourceCommit',
    '${qa['protonSourceCommit']}',
  );
  _requireQaDigest(qa, 'sourceDescriptorSha256', 'Linux QA');
  _requireQaDigest(qa, 'protonRuntimeSha256', 'Linux QA');
  _requireQaDigest(qa, 'acceptanceResultSha256', 'Linux QA');
  _requireQaDigest(qa, 'gameExecutableSha256', 'Linux QA');
  _requireQaDigest(qa, 'runtimeConfigurationSha256', 'Linux QA');
  _requireQaDigest(qa, 'wineCommandSha256', 'Linux QA');
  _requireQaDigest(qa, 'evidenceSha256', 'Linux QA');
  if (bundle.validations['proton']!.evidenceSha256 !=
      qa['sourceDescriptorSha256']) {
    throw StateError(
      'Linux proton validation must bind the exact QA descriptor bytes.',
    );
  }
  _requireQaCases(
    qa,
    inventory.liveCases,
    inventory.liveCasesSha256,
    inventory.sha256,
    'Linux QA',
  );
  if ((qa['failures'] as List).isNotEmpty) {
    throw StateError('Linux QA contains failures.');
  }
  _requireReleaseJourney(qa['releaseJourney'], 'Linux QA');
}

void _validateWindowsQa(
  ReleasePlatformBundle bundle,
  _ReleaseHandoffContext context,
  _ReleaseQaCaseInventory inventory,
  _RobotopiaGameIdentity gameIdentity,
) {
  final qa = bundle.qa;
  _requireQaReleaseIdentity(qa, bundle, context, expectedPlatform: 'windows');
  const expectedSigning = 'authenticode-timestamped';
  final expectedToolchains = {
    ...context.policy.toolchains,
    ...context.platformToolchains['windows-x64']!,
  };
  if (qa['schema'] != releasePlatformQaWindowsSchema ||
      qa['sourceSchema'] != releaseWindowsQaSummarySchema ||
      qa['signingState'] != expectedSigning ||
      qa['gameBuildId'] != context.policy.gameBuildId ||
      !_sameQaStringMap(qa['toolchains'], expectedToolchains)) {
    throw StateError('Windows QA release or toolchain claims are invalid.');
  }
  for (final field in const [
    'sourceDescriptorSha256',
    'validationDescriptorSha256',
  ]) {
    _requireQaDigest(qa, field, 'Windows QA');
  }
  final validationSha = qa['validationDescriptorSha256'];
  for (final name in ['package', 'toolchains', 'authenticode']) {
    if (bundle.validations[name]!.evidenceSha256 != validationSha) {
      throw StateError(
        'Windows $name validation must bind the local validation summary.',
      );
    }
  }
  _validateUnityQa(bundle, qa['unity']);
  _validateRobotopiaQa(bundle, qa['robotopia'], inventory, gameIdentity);
  _validateCreatorQa(bundle, qa['creator'], inventory);
}

void _validateUnityQa(ReleasePlatformBundle bundle, Object? value) {
  final qa = (value as Map).cast<String, Object?>();
  _requireQaDigest(qa, 'evidenceSha256', 'Windows Unity QA');
  if (qa['result'] != 'pass' ||
      qa['editorVersion'] != '6000.0.23f1' ||
      qa['cycles'] != 16 ||
      qa['validatorSmoke'] != true ||
      bundle.validations['unity']!.evidenceSha256 != qa['evidenceSha256']) {
    throw StateError('Windows Unity QA is incomplete or invalid.');
  }
}

void _validateRobotopiaQa(
  ReleasePlatformBundle bundle,
  Object? value,
  _ReleaseQaCaseInventory inventory,
  _RobotopiaGameIdentity gameIdentity,
) {
  final qa = (value as Map).cast<String, Object?>();
  for (final field in const [
    'evidenceSha256',
    'gameArchiveSha256',
    'gameExecutableSha256',
    'gameFilesManifestSha256',
  ]) {
    _requireQaDigest(qa, field, 'Windows Robotopia QA');
  }
  if (qa['result'] != 'pass' ||
      qa['suite'] != 'full' ||
      qa['gameArchiveSha256'] != gameIdentity.archiveSha256 ||
      qa['gameExecutableSha256'] != gameIdentity.gameExecutableSha256 ||
      qa['gameFilesManifestSha256'] != gameIdentity.filesManifestSha256 ||
      qa['gameFilesVerified'] != gameIdentity.filesVerified ||
      (qa['missingCases'] as List).isNotEmpty ||
      (qa['failures'] as List).isNotEmpty ||
      bundle.validations['robotopia']!.evidenceSha256 != qa['evidenceSha256']) {
    throw StateError('Windows Robotopia QA is incomplete or invalid.');
  }
  _requireQaCases(
    qa,
    inventory.liveCases,
    inventory.liveCasesSha256,
    inventory.sha256,
    'Windows Robotopia QA',
  );
  _requireReleaseJourney(qa['releaseJourney'], 'Windows Robotopia QA');
}

_RobotopiaGameIdentity _loadRobotopiaGameIdentity(
  TopiaForgeReleasePolicy policy,
) {
  final file = File(
    p.join(policy.repositoryRoot, policy.gameBuildMetadataFile),
  );
  final bytes = readBoundedRegularFileSync(
    file,
    maxBytes: CliFileLimits.metadata,
  );
  final Object? decoded;
  try {
    decoded = jsonDecode(utf8.decode(bytes, allowMalformed: false));
  } on FormatException catch (error) {
    throw StateError('Robotopia game-build metadata is invalid: $error');
  }
  if (decoded is! Map) {
    throw StateError('Robotopia game-build metadata must be an object.');
  }
  final metadata = decoded.cast<String, Object?>();
  final archives = metadata['archives'];
  final windowsManifest = metadata['windowsFilesManifest'];
  if (metadata['buildId'] != policy.gameBuildId ||
      archives is! Map ||
      windowsManifest is! Map) {
    throw StateError('Robotopia game-build identity is incomplete.');
  }
  final windowsArchive = archives['windows'];
  if (windowsArchive is! Map) {
    throw StateError('Robotopia Windows archive identity is missing.');
  }
  final manifest = windowsManifest.cast<String, Object?>();
  const expectedManifestKeys = {
    'path',
    'sha256',
    'fileCount',
    'gameExecutableSha256',
  };
  if (manifest.keys.toSet().length != expectedManifestKeys.length ||
      !manifest.keys.toSet().containsAll(expectedManifestKeys) ||
      manifest['path'] != 'filelist.json' ||
      manifest['fileCount'] is! int ||
      (manifest['fileCount']! as int) <= 0) {
    throw StateError('Robotopia Windows files-manifest identity is invalid.');
  }
  final archiveSha256 = windowsArchive['sha256'];
  final manifestSha256 = manifest['sha256'];
  final executableSha256 = manifest['gameExecutableSha256'];
  for (final value in [archiveSha256, manifestSha256, executableSha256]) {
    if (value is! String || !RegExp(r'^[0-9a-f]{64}$').hasMatch(value)) {
      throw StateError('Robotopia game-build identity has an invalid digest.');
    }
  }
  return _RobotopiaGameIdentity(
    archiveSha256: archiveSha256! as String,
    filesManifestSha256: manifestSha256! as String,
    filesVerified: manifest['fileCount']! as int,
    gameExecutableSha256: executableSha256! as String,
  );
}

void _validateCreatorQa(
  ReleasePlatformBundle bundle,
  Object? value,
  _ReleaseQaCaseInventory inventory,
) {
  final qa = (value as Map).cast<String, Object?>();
  for (final field in const ['descriptorSha256', 'evidenceSha256']) {
    _requireQaDigest(qa, field, 'Windows Creator QA');
  }
  if (qa['result'] != 'pass' ||
      qa['suite'] != 'creator-full' ||
      (qa['failures'] as List).isNotEmpty ||
      (qa['lifecycleCycles'] as int) < inventory.creatorMinimumCycles ||
      qa['saveStateUnchanged'] != true ||
      qa['checkpointStateUnchanged'] != true ||
      bundle.validations['creator']!.evidenceSha256 != qa['descriptorSha256']) {
    throw StateError('Windows Creator QA is incomplete or invalid.');
  }
  _requireQaCases(
    qa,
    inventory.creatorCases,
    inventory.creatorCasesSha256,
    inventory.sha256,
    'Windows Creator QA',
  );
}

void _requireQaReleaseIdentity(
  Map<String, Object?> qa,
  ReleasePlatformBundle bundle,
  _ReleaseHandoffContext context, {
  required String expectedPlatform,
}) {
  if (qa['version'] != bundle.version ||
      qa['targetSha'] != bundle.targetSha ||
      qa['platform'] != expectedPlatform ||
      qa['archiveSha256'] != bundle.archive.sha256 ||
      qa['archiveSize'] != bundle.archive.size ||
      qa['canonicalEcosystemSha256'] != bundle.canonicalEcosystemSha256 ||
      bundle.version != context.release.version ||
      bundle.targetSha != context.targetSha) {
    throw StateError('${bundle.platform} QA is for a different release.');
  }
}

void _requireQaCases(
  Map<String, Object?> qa,
  List<String> expectedCases,
  String expectedCasesSha256,
  String expectedInventorySha256,
  String label,
) {
  final requiredCases = (qa['requiredCases'] as List).cast<String>();
  final passedCases = (qa['passedCases'] as List).cast<String>();
  if (qa['caseInventorySha256'] != expectedInventorySha256 ||
      qa['requiredCasesSha256'] != expectedCasesSha256 ||
      qa['passedCasesSha256'] != expectedCasesSha256 ||
      !_sameQaList(requiredCases, expectedCases) ||
      !_sameQaList(passedCases, expectedCases)) {
    throw StateError('$label does not contain the tagged canonical case set.');
  }
}

void _requireReleaseJourney(Object? value, String label) {
  final journey = (value as Map).cast<String, Object?>();
  if (journey['enabled'] != true ||
      journey['authoringCommandCount'] != 2 ||
      journey['loadedPackageStatus'] != 'loaded' ||
      journey['logMarkerObserved'] != true) {
    throw StateError('$label release journey is incomplete.');
  }
}

void _requireQaPlatformToolchainPin(
  _ReleaseHandoffContext context,
  String name,
  String actual,
) {
  if (context.platformToolchains['linux-x64']![name] != actual) {
    throw StateError('Linux QA $name does not match its toolchain pin.');
  }
}

void _requireQaDigest(Map<String, Object?> qa, String field, String label) {
  final value = qa[field];
  if (value is! String || !RegExp(r'^[0-9a-f]{64}$').hasMatch(value)) {
    throw StateError('$label.$field must be a lowercase SHA-256 digest.');
  }
}

_ReleaseQaCaseInventory _loadReleaseQaCaseInventory(
  String repositoryRoot,
  int expectedGameBuildId,
) {
  final file = File(
    p.join(repositoryRoot, 'tests', 'live-game-acceptance.json'),
  );
  final bytes = readBoundedRegularFileSync(
    file,
    maxBytes: CliFileLimits.metadata,
  );
  final Object? decoded;
  try {
    decoded = jsonDecode(utf8.decode(bytes, allowMalformed: false));
  } on FormatException catch (error) {
    throw StateError('Tagged live QA inventory is invalid: $error');
  }
  if (decoded is! Map) {
    throw StateError('Tagged live QA inventory must be a JSON object.');
  }
  final json = decoded.cast<String, Object?>();
  final creator = (json['creatorAcceptance'] as Map?)?.cast<String, Object?>();
  if (json['schemaVersion'] != 1 ||
      creator == null ||
      creator['gameBuild'] != '$expectedGameBuildId' ||
      creator['minimumLifecycleCycles'] is! int ||
      (creator['minimumLifecycleCycles'] as int) < 10) {
    throw StateError('Tagged live QA inventory policy is invalid.');
  }
  final liveCases = _qaInventoryIds(json['cases'], 'live cases');
  final creatorCases = _qaInventoryIds(creator['cases'], 'Creator cases');
  return _ReleaseQaCaseInventory(
    sha256: sha256.convert(bytes).toString(),
    liveCases: liveCases,
    liveCasesSha256: _qaCaseSetSha256(liveCases),
    creatorCases: creatorCases,
    creatorCasesSha256: _qaCaseSetSha256(creatorCases),
    creatorMinimumCycles: creator['minimumLifecycleCycles']! as int,
  );
}

List<String> _qaInventoryIds(Object? value, String label) {
  if (value is! List || value.isEmpty) {
    throw StateError('Tagged $label must be a non-empty list.');
  }
  final ids = <String>[];
  for (final entry in value) {
    if (entry is! Map || entry['id'] is! String) {
      throw StateError('Tagged $label contains an invalid case.');
    }
    final id = entry['id']! as String;
    if (id.isEmpty || !ids.addUnique(id)) {
      throw StateError('Tagged $label contains an invalid or duplicate id.');
    }
  }
  ids.sort();
  return List.unmodifiable(ids);
}

String _qaCaseSetSha256(List<String> cases) =>
    sha256.convert(utf8.encode('${cases.join('\n')}\n')).toString();

String _qaSha256(Map<String, Object?> qa) =>
    sha256.convert(utf8.encode(jsonEncode(qa))).toString();

bool _sameQaList(List<String> left, List<String> right) =>
    left.length == right.length &&
    List.generate(
      left.length,
      (index) => left[index] == right[index],
    ).every((same) => same);

bool _sameQaStringMap(Object? value, Map<String, String> expected) {
  if (value is! Map || value.length != expected.length) return false;
  return expected.entries.every((entry) => value[entry.key] == entry.value);
}

extension on List<String> {
  bool addUnique(String value) {
    if (contains(value)) return false;
    add(value);
    return true;
  }
}
