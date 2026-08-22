part of 'release_handoff_qa.dart';

Map<String, Object?> _qaWindowsToolchains(Object? value) {
  final json = _qaObject(value, 'Windows qa.toolchains');
  const fields = {
    'dart',
    'dotnetRuntime',
    'dotnetSdk',
    'flutter',
    'node',
    'unity',
    'msvc',
    'windowsSdk',
  };
  _qaExactKeys(json, fields, 'Windows qa.toolchains');
  return {
    for (final field in fields)
      field: _qaString(json, field, 'Windows qa.toolchains'),
  };
}

Map<String, Object?> _qaUnity(Object? value) {
  final json = _qaObject(value, 'Windows qa.unity');
  const fields = {
    'result',
    'editorVersion',
    'cycles',
    'validatorSmoke',
    'evidenceSha256',
  };
  _qaExactKeys(json, fields, 'Windows qa.unity');
  return {
    'result': _qaString(json, 'result', 'Windows qa.unity'),
    'editorVersion': _qaString(json, 'editorVersion', 'Windows qa.unity'),
    'cycles': _qaPositiveInt(json, 'cycles', 'Windows qa.unity'),
    'validatorSmoke': _qaBool(json, 'validatorSmoke', 'Windows qa.unity'),
    'evidenceSha256': _qaString(json, 'evidenceSha256', 'Windows qa.unity'),
  };
}

Map<String, Object?> _qaRobotopia(Object? value) {
  final json = _qaObject(value, 'Windows qa.robotopia');
  const fields = {
    'result',
    'suite',
    'gameArchiveSha256',
    'gameExecutableSha256',
    'gameFilesManifestSha256',
    'gameFilesVerified',
    'caseInventorySha256',
    'requiredCases',
    'requiredCasesSha256',
    'passedCases',
    'passedCasesSha256',
    'missingCases',
    'failures',
    'releaseJourney',
    'evidenceSha256',
  };
  _qaExactKeys(json, fields, 'Windows qa.robotopia');
  return {
    'result': _qaString(json, 'result', 'Windows qa.robotopia'),
    'suite': _qaString(json, 'suite', 'Windows qa.robotopia'),
    'gameArchiveSha256': _qaString(
      json,
      'gameArchiveSha256',
      'Windows qa.robotopia',
    ),
    'gameExecutableSha256': _qaString(
      json,
      'gameExecutableSha256',
      'Windows qa.robotopia',
    ),
    'gameFilesManifestSha256': _qaString(
      json,
      'gameFilesManifestSha256',
      'Windows qa.robotopia',
    ),
    'gameFilesVerified': _qaPositiveInt(
      json,
      'gameFilesVerified',
      'Windows qa.robotopia',
    ),
    'caseInventorySha256': _qaString(
      json,
      'caseInventorySha256',
      'Windows qa.robotopia',
    ),
    'requiredCases': _qaStringList(
      json['requiredCases'],
      'Windows qa.robotopia.requiredCases',
    ),
    'requiredCasesSha256': _qaString(
      json,
      'requiredCasesSha256',
      'Windows qa.robotopia',
    ),
    'passedCases': _qaStringList(
      json['passedCases'],
      'Windows qa.robotopia.passedCases',
    ),
    'passedCasesSha256': _qaString(
      json,
      'passedCasesSha256',
      'Windows qa.robotopia',
    ),
    'missingCases': _qaStringList(
      json['missingCases'],
      'Windows qa.robotopia.missingCases',
      allowEmpty: true,
    ),
    'failures': _qaStringList(
      json['failures'],
      'Windows qa.robotopia.failures',
      allowEmpty: true,
    ),
    'releaseJourney': _qaReleaseJourney(
      json['releaseJourney'],
      'Windows qa.robotopia',
    ),
    'evidenceSha256': _qaString(json, 'evidenceSha256', 'Windows qa.robotopia'),
  };
}

Map<String, Object?> _qaCreator(Object? value) {
  final json = _qaObject(value, 'Windows qa.creator');
  const fields = {
    'result',
    'suite',
    'acceptanceChallenge',
    'lastRunSessionId',
    'creatorPackageReceipt',
    'acceptanceResultSha256',
    'caseInventorySha256',
    'requiredCases',
    'requiredCasesSha256',
    'passedCases',
    'passedCasesSha256',
    'lifecycleCycles',
    'saveStateUnchanged',
    'checkpointStateUnchanged',
    'failures',
    'descriptorSha256',
    'evidenceSha256',
    'evidenceSize',
  };
  _qaExactKeys(json, fields, 'Windows qa.creator');
  return {
    'result': _qaString(json, 'result', 'Windows qa.creator'),
    'suite': _qaString(json, 'suite', 'Windows qa.creator'),
    'acceptanceChallenge': _qaString(
      json,
      'acceptanceChallenge',
      'Windows qa.creator',
    ),
    'lastRunSessionId': _qaString(
      json,
      'lastRunSessionId',
      'Windows qa.creator',
    ),
    'creatorPackageReceipt': _qaPackageReceipt(
      json['creatorPackageReceipt'],
      'Windows qa.creator.creatorPackageReceipt',
    ),
    'acceptanceResultSha256': _qaString(
      json,
      'acceptanceResultSha256',
      'Windows qa.creator',
    ),
    'caseInventorySha256': _qaString(
      json,
      'caseInventorySha256',
      'Windows qa.creator',
    ),
    'requiredCases': _qaStringList(
      json['requiredCases'],
      'Windows qa.creator.requiredCases',
    ),
    'requiredCasesSha256': _qaString(
      json,
      'requiredCasesSha256',
      'Windows qa.creator',
    ),
    'passedCases': _qaStringList(
      json['passedCases'],
      'Windows qa.creator.passedCases',
    ),
    'passedCasesSha256': _qaString(
      json,
      'passedCasesSha256',
      'Windows qa.creator',
    ),
    'lifecycleCycles': _qaPositiveInt(
      json,
      'lifecycleCycles',
      'Windows qa.creator',
    ),
    'saveStateUnchanged': _qaBool(
      json,
      'saveStateUnchanged',
      'Windows qa.creator',
    ),
    'checkpointStateUnchanged': _qaBool(
      json,
      'checkpointStateUnchanged',
      'Windows qa.creator',
    ),
    'failures': _qaStringList(
      json['failures'],
      'Windows qa.creator.failures',
      allowEmpty: true,
    ),
    'descriptorSha256': _qaString(
      json,
      'descriptorSha256',
      'Windows qa.creator',
    ),
    'evidenceSha256': _qaString(json, 'evidenceSha256', 'Windows qa.creator'),
    'evidenceSize': _qaPositiveInt(json, 'evidenceSize', 'Windows qa.creator'),
  };
}

/// Normalizes one exact installed-package receipt carried in QA evidence.
///
/// The receipt binds Creator evidence to the exact creator-workbench payload the
/// manager actually loaded, so a descriptor cannot be replayed against a
/// different build of the mod.
Map<String, Object?> _qaPackageReceipt(Object? value, String label) {
  final json = _qaObject(value, label);
  _qaExactKeys(json, const {'sourceSha256', 'criticalFiles'}, label);
  final rawFiles = json['criticalFiles'];
  if (rawFiles is! List || rawFiles.isEmpty || rawFiles.length > 8192) {
    throw StateError('$label.criticalFiles is invalid.');
  }
  final files = <Map<String, Object?>>[];
  String? previousPath;
  for (final rawFile in rawFiles) {
    final file = _qaObject(rawFile, '$label.criticalFiles');
    _qaExactKeys(file, const {'path', 'sha256'}, '$label.criticalFiles');
    final path = _qaString(file, 'path', '$label.criticalFiles');
    if (previousPath != null && previousPath.compareTo(path) >= 0) {
      throw StateError('$label.criticalFiles is not ordered or is duplicated.');
    }
    previousPath = path;
    files.add({
      'path': path,
      'sha256': _qaString(file, 'sha256', '$label.criticalFiles'),
    });
  }
  return {
    'sourceSha256': _qaString(json, 'sourceSha256', label),
    'criticalFiles': files,
  };
}

Map<String, Object?> _qaReleaseJourney(Object? value, String parent) {
  final label = '$parent.releaseJourney';
  final json = _qaObject(value, label);
  const fields = {
    'enabled',
    'authoringCommandCount',
    'loadedPackageStatus',
    'logMarkerObserved',
  };
  _qaExactKeys(json, fields, label);
  return {
    'enabled': _qaBool(json, 'enabled', label),
    'authoringCommandCount': _qaPositiveInt(
      json,
      'authoringCommandCount',
      label,
    ),
    'loadedPackageStatus': _qaString(json, 'loadedPackageStatus', label),
    'logMarkerObserved': _qaBool(json, 'logMarkerObserved', label),
  };
}

Map<String, Object?> _qaObject(Object? value, String label) {
  if (value is! Map) throw StateError('$label must be a JSON object.');
  return value.map((key, value) => MapEntry(key.toString(), value));
}

void _qaExactKeys(
  Map<String, Object?> json,
  Set<String> expected,
  String label,
) {
  final actual = json.keys.toSet();
  if (actual.length != expected.length || !actual.containsAll(expected)) {
    final missing = expected.difference(actual).toList()..sort();
    final extra = actual.difference(expected).toList()..sort();
    throw StateError(
      '$label has forbidden or missing fields. '
      'Missing: ${missing.join(', ')}. Extra: ${extra.join(', ')}.',
    );
  }
}

String _qaString(Map<String, Object?> json, String key, String label) =>
    _qaRequiredString(json[key], '$label.$key');

String _qaRequiredString(Object? value, String label) {
  if (value is! String || value.isEmpty) {
    throw StateError('$label must be a non-empty string.');
  }
  return value;
}

int _qaPositiveInt(Map<String, Object?> json, String key, String label) {
  final value = json[key];
  if (value is! int || value <= 0) {
    throw StateError('$label.$key must be a positive integer.');
  }
  return value;
}

bool _qaBool(Map<String, Object?> json, String key, String label) {
  final value = json[key];
  if (value is! bool) throw StateError('$label.$key must be a boolean.');
  return value;
}

List<String> _qaStringList(
  Object? value,
  String label, {
  bool allowEmpty = false,
}) {
  if (value is! List ||
      (!allowEmpty && value.isEmpty) ||
      value.any((entry) => entry is! String || entry.isEmpty)) {
    throw StateError('$label must be a typed string list.');
  }
  return List<String>.unmodifiable(value.cast<String>());
}
