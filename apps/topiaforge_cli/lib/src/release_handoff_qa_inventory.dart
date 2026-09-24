part of 'release_handoff.dart';

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
  if (json['schemaVersion'] != 1 ||
      json['gameBuild'] != '$expectedGameBuildId') {
    throw StateError('Tagged live QA inventory policy is invalid.');
  }
  final liveCases = _qaInventoryIds(json['cases'], 'live cases');
  return _ReleaseQaCaseInventory(
    sha256: sha256.convert(bytes).toString(),
    liveCases: liveCases,
    liveCasesSha256: _qaCaseSetSha256(liveCases),
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
