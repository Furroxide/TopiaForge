import 'release_handoff.dart';

/// Checks scrubbed acceptance content after exact-source schema and handoff
/// verification. Hashes and reviewer references bind evidence; they do not
/// establish a human reviewer's identity or authorization.
void validateCandidateAcceptance({
  required Map<String, Object?> acceptance,
  required Map<String, Object?> decision,
  required Map<String, Object?> gameMetadata,
  required Map<String, Object?> liveInventory,
  required Map<String, Object?> redesignInventory,
  required ReleaseHandoffVerification handoff,
}) {
  _keys(acceptance, const {
    'schema',
    'repository',
    'releaseVersion',
    'targetSha',
    'contractSha256',
    'handoffSha256',
    'payloads',
    'gameBuildId',
    'result',
    'gameCycles',
    'authoringCycles',
    'gameEvidenceSha256',
    'authoringEvidenceSha256',
    'isolation',
    'cases',
    'reviewerEvidence',
  }, 'acceptance');
  if (acceptance['schema'] != 'release-candidate-acceptance-v1' ||
      acceptance['result'] != 'passed' ||
      acceptance['releaseVersion'] != handoff.handoff.version ||
      acceptance['targetSha'] != handoff.handoff.targetSha) {
    _fail('Acceptance does not identify the passed handoff candidate.');
  }
  _text(
    acceptance['repository'],
    RegExp(r'^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$'),
    'repository',
  );
  for (final field in const [
    'contractSha256',
    'handoffSha256',
    'gameEvidenceSha256',
    'authoringEvidenceSha256',
  ]) {
    _digest(acceptance[field], field);
  }
  _payloads(acceptance['payloads']);
  if (gameMetadata['buildId'] is! int ||
      liveInventory['schemaVersion'] != 1 ||
      redesignInventory['schemaVersion'] != 1 ||
      liveInventory['gameBuild'] != '${gameMetadata['buildId']}' ||
      redesignInventory['gameBuild'] != liveInventory['gameBuild'] ||
      liveInventory['requiredLifecycleCycles'] != 10 ||
      redesignInventory['requiredLifecycleCycles'] != 10 ||
      redesignInventory['requiredAuthoringCycles'] != 16) {
    _fail(
      'Acceptance inventories do not bind the pinned game and cycle counts.',
    );
  }
  for (final entry in {
    'gameBuildId': gameMetadata['buildId'],
    'gameCycles': liveInventory['requiredLifecycleCycles'],
    'authoringCycles': redesignInventory['requiredAuthoringCycles'],
  }.entries) {
    if (acceptance[entry.key] is! int || acceptance[entry.key] != entry.value) {
      _fail('Acceptance ${entry.key} differs from its pinned contract.');
    }
  }
  _isolation(acceptance['isolation']);
  _cases(acceptance['cases'], _inventoryCases(redesignInventory));
  _reviewers(acceptance['reviewerEvidence'], decision);
  final windows = handoff.platformBundles['windows-x64'];
  if (windows == null ||
      windows.qa['gameBuildId'] != acceptance['gameBuildId']) {
    _fail('Acceptance requires the verified Windows game handoff.');
  }
  final game = _object(windows.qa['robotopia'], 'game receipt');
  final authoring = _object(windows.qa['unity'], 'authoring receipt');
  final liveCases = _inventoryCases(liveInventory);
  if (game['result'] != 'pass' ||
      game['suite'] != 'full' ||
      !_sameStrings(game['requiredCases'], liveCases) ||
      !_sameStrings(game['passedCases'], liveCases) ||
      !_emptyList(game['missingCases']) ||
      !_emptyList(game['failures']) ||
      acceptance['gameEvidenceSha256'] != game['evidenceSha256']) {
    _fail('Acceptance game receipt is stale, incomplete or failed.');
  }
  if (authoring['result'] != 'pass' ||
      authoring['cycles'] != 16 ||
      authoring['validatorSmoke'] != true ||
      acceptance['authoringEvidenceSha256'] != authoring['evidenceSha256']) {
    _fail('Acceptance authoring receipt is stale, incomplete or failed.');
  }
}

void _isolation(Object? value) {
  final isolation = _object(value, 'isolation');
  _keys(isolation, const {
    'kind',
    'evidenceSha256',
    'persistentDataIsolated',
    'normalUserDataAccessed',
  }, 'isolation');
  if (!const ['windows-user', 'virtual-machine'].contains(isolation['kind']) ||
      isolation['persistentDataIsolated'] != true ||
      isolation['normalUserDataAccessed'] != false) {
    _fail(
      'Acceptance must attest isolated persistent data without normal user access.',
    );
  }
  _digest(isolation['evidenceSha256'], 'isolation evidence');
}

void _payloads(Object? value) {
  final payloads = _list(value, 'payloads', 256);
  String? previous;
  for (final item in payloads) {
    final payload = _object(item, 'payload');
    _keys(payload, const {'name', 'size', 'sha256'}, 'payload');
    final name = _text(
      payload['name'],
      RegExp(r'^[A-Za-z0-9][A-Za-z0-9._-]{0,179}$'),
      'payload name',
    );
    if (previous != null && name.compareTo(previous) <= 0 ||
        payload['size'] is! int ||
        (payload['size']! as int) <= 0) {
      _fail(
        'Acceptance payloads require sorted unique names and positive integer sizes.',
      );
    }
    _digest(payload['sha256'], 'payload digest');
    previous = name;
  }
}

void _cases(Object? value, List<String> expected) {
  final cases = _list(value, 'cases', 256);
  final actual = <String>[];
  for (final item in cases) {
    final entry = _object(item, 'case');
    _keys(entry, const {'id', 'result', 'evidenceSha256'}, 'case');
    actual.add(_caseId(entry['id']));
    if (entry['result'] != 'passed') _fail('Every acceptance case must pass.');
    _digest(entry['evidenceSha256'], 'case evidence');
  }
  if (!_sameStrings(actual, expected)) {
    _fail('Acceptance cases must exactly match the sorted required inventory.');
  }
}

void _reviewers(Object? value, Map<String, Object?> decision) {
  final gameGates = _list(decision['gates'], 'decision gates', 12)
      .map((value) => _object(value, 'decision gate'))
      .where((gate) => gate['id'] == 'P0-GAME-01')
      .toList();
  if (gameGates.length != 1 || gameGates.single['status'] != 'approved') {
    _fail('Acceptance requires one approved P0-GAME-01 decision.');
  }
  final gate = gameGates.single;
  const roles = ['robotopia-owner', 'runtime-mod-qa'];
  if (!_sameStrings(gate['reviewerRoles'], roles)) {
    _fail('Acceptance game reviewer roles differ from the required contract.');
  }
  final evidenceIds = <String>{};
  for (final item in _list(gate['evidenceIds'], 'game evidence IDs', 64)) {
    final id = _text(
      item,
      RegExp(r'^EVID-P0-GAME-01-[0-9]{4}$'),
      'game evidence ID',
    );
    if (!evidenceIds.add(id)) _fail('Game evidence IDs must be unique.');
  }
  final seenIds = <String>{};
  final seenRoles = <String>{};
  final pairs = <String>{};
  for (final item in _list(value, 'reviewer evidence', 128)) {
    final entry = _object(item, 'reviewer evidence');
    _keys(entry, const {
      'evidenceId',
      'role',
      'reference',
      'sha256',
    }, 'reviewer evidence');
    final id = entry['evidenceId'];
    final role = entry['role'];
    if (id is! String ||
        !evidenceIds.contains(id) ||
        role is! String ||
        !roles.contains(role) ||
        !pairs.add('$id\u0000$role')) {
      _fail('Reviewer evidence is duplicate or unbound to the game decision.');
    }
    _text(
      entry['reference'],
      RegExp(r'^review:[a-z0-9][a-z0-9._-]{0,95}$'),
      'review reference',
    );
    _digest(entry['sha256'], 'review evidence digest');
    seenIds.add(id);
    seenRoles.add(role);
  }
  if (seenIds.length != evidenceIds.length ||
      seenRoles.length != roles.length) {
    _fail(
      'Reviewer evidence must cover every game evidence ID and required role.',
    );
  }
}

List<String> _inventoryCases(Map<String, Object?> inventory) {
  final ids = <String>{};
  for (final value in _list(inventory['cases'], 'inventory cases', 256)) {
    final id = _caseId(_object(value, 'inventory case')['id']);
    if (!ids.add(id)) _fail('Inventory cases must be unique.');
  }
  return ids.toList()..sort();
}

bool _sameStrings(Object? actual, List<String> expected) =>
    actual is List &&
    actual.length == expected.length &&
    List.generate(
      expected.length,
      (index) => actual[index] == expected[index],
    ).every((v) => v);
bool _emptyList(Object? value) => value is List && value.isEmpty;

String _caseId(Object? value) {
  final id = _text(
    value,
    RegExp(r'^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$'),
    'case ID',
  );
  if (id.length > 96) _fail('Case IDs must be bounded.');
  return id;
}

void _digest(Object? value, String label) =>
    _text(value, RegExp(r'^(?!0{64}$)[0-9a-f]{64}$'), label);

String _text(Object? value, RegExp pattern, String label) {
  if (value is! String ||
      !pattern.hasMatch(value) ||
      pattern.firstMatch(value)?.end != value.length) {
    _fail('Invalid acceptance $label.');
  }
  return value;
}

Map<String, Object?> _object(Object? value, String label) {
  if (value is! Map<String, Object?>) {
    _fail('Acceptance $label must be an object.');
  }
  return value;
}

List<Object?> _list(Object? value, String label, int maximum) {
  if (value is! List<Object?> || value.isEmpty || value.length > maximum) {
    _fail('Acceptance $label must be a bounded nonempty list.');
  }
  return value;
}

void _keys(Map<String, Object?> value, Set<String> expected, String label) {
  if (value.length != expected.length ||
      !value.keys.toSet().containsAll(expected)) {
    _fail('Acceptance $label has missing or forbidden fields.');
  }
}

Never _fail(String message) => throw StateError(message);
