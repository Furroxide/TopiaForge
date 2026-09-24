import '../release_strict_json.dart';
import 'sandbox_json.dart';
import 'sandbox_specification.dart';

/// Closed private annex. Parsing never establishes provenance or a passed case.
final class SandboxNativeAnnex {
  SandboxNativeAnnex._(Map<String, Object?> value)
    : document = nativeFreeze(value) as Map<String, Object?>;
  final Map<String, Object?> document;
  String get runId => document['runId']! as String;
  String get sourceRevision => document['sourceRevision']! as String;
  String get challenge => document['challenge']! as String;
  Map<String, Object?> get isolation =>
      document['isolation']! as Map<String, Object?>;
  Map<String, Object?> get cleanup =>
      document['cleanup']! as Map<String, Object?>;
  List<Map<String, Object?>> get scenarioResults =>
      (document['scenarioResults']! as List).cast<Map<String, Object?>>();
  List<Map<String, Object?>> get packages =>
      (document['packages']! as List).cast<Map<String, Object?>>();
  List<Map<String, Object?>> get artifacts =>
      (document['artifacts']! as List).cast<Map<String, Object?>>();

  static SandboxNativeAnnex parse(List<int> bytes) {
    final value = sandboxDocument(bytes, 'Sandbox native annex');
    sandboxFields(value, {
      'schemaVersion',
      'kind',
      'scope',
      'runId',
      'sourceRevision',
      'sourceWorkspaceSha256',
      'specSha256',
      'driverManifestSha256',
      'challenge',
      'startedAtUtc',
      'completedAtUtc',
      'deviceProfileSha256',
      'isolation',
      'packages',
      'broker',
      'transcript',
      'artifacts',
      'scenarioResults',
      'cleanup',
      'failures',
    }, 'Sandbox native annex');
    if (value['schemaVersion'] is! int ||
        value['schemaVersion'] != 1 ||
        value['kind'] != 'sandbox-workbench-automation-v1' ||
        value['scope'] != 'supplementary-native-development') {
      throw StateError('Unsupported native annex identity.');
    }
    sandboxId(value['runId'], 'native run ID');
    nativeHex(value['sourceRevision'], 40);
    for (final key in [
      'specSha256',
      'driverManifestSha256',
      'challenge',
      'deviceProfileSha256',
      'sourceWorkspaceSha256',
    ]) {
      nativeHex(value[key], 64);
    }
    final start = nativeUtc(value['startedAtUtc']);
    final end = nativeUtc(value['completedAtUtc']);
    if (end.isBefore(start) ||
        end.difference(start) > const Duration(hours: 6)) {
      throw StateError('Native run interval is invalid.');
    }
    _isolation(value['isolation'], value['challenge']! as String);
    final packages = nativeRows(value['packages'], 32, allowEmpty: false);
    final ids = <String>{};
    for (final package in packages) {
      sandboxFields(package, {
        'id',
        'sourceSha256',
        'criticalFiles',
      }, 'package');
      final id = sandboxText(package['id'], 'package ID', maximum: 128);
      if (!RegExp(r'^[a-z0-9]+(?:[.-][a-z0-9]+)*$').hasMatch(id) ||
          !ids.add(id)) {
        throw StateError('Invalid or repeated native package.');
      }
      nativeHex(package['sourceSha256'], 64);
      final paths = <String>{};
      for (final file in nativeRows(
        package['criticalFiles'],
        128,
        allowEmpty: false,
      )) {
        nativeFile(file);
        if (!paths.add((file['path']! as String).toLowerCase())) {
          throw StateError('Repeated critical path.');
        }
      }
    }
    final broker = sandboxObject(value['broker'], 'broker');
    sandboxFields(broker, {'binarySha256', 'processId'}, 'broker');
    nativeHex(broker['binarySha256'], 64);
    nativeInteger(broker['processId'], 1, 2147483647);
    nativeFile(sandboxObject(value['transcript'], 'transcript'));
    final paths = <String>{
      (sandboxObject(value['transcript'], 'transcript')['path']! as String)
          .toLowerCase(),
    };
    for (final artifact in nativeRows(value['artifacts'], 256)) {
      nativeFile(artifact, length: true);
      if (!paths.add((artifact['path']! as String).toLowerCase())) {
        throw StateError('Native artifact paths alias or repeat.');
      }
    }
    var previous = -1;
    for (final row in nativeRows(value['scenarioResults'], 9)) {
      sandboxFields(row, {'scenarioId', 'status', 'reason'}, 'scenario claim');
      final index = sandboxScenarioIds.indexOf(
        sandboxId(row['scenarioId'], 'native scenario ID'),
      );
      if (index <= previous || index < 0) {
        throw StateError('Native scenario claims must be unique and ordered.');
      }
      previous = index;
      if (!['passed', 'failed', 'unavailable'].contains(row['status'])) {
        throw StateError('Unknown native scenario claim.');
      }
      nativeReason(row['reason'], empty: row['status'] == 'passed');
    }
    final cleanup = sandboxObject(value['cleanup'], 'cleanup');
    sandboxFields(cleanup, {
      'inputReleased',
      'fixtureReleased',
      'originalProcessExitConfirmed',
    }, 'cleanup');
    for (final flag in cleanup.values) {
      if (flag is! bool) throw StateError('Cleanup flags must be booleans.');
    }
    nativeReasons(value['failures']);
    return SandboxNativeAnnex._(value);
  }
}

void _isolation(Object? item, String challenge) {
  final isolation = sandboxObject(item, 'native isolation');
  sandboxFields(isolation, {
    'kind',
    'provisioningRecordSha256',
    'acknowledgementSha256',
    'acknowledgement',
    'processExitConfirmed',
  }, 'native isolation');
  if (!['windows-user', 'virtual-machine'].contains(isolation['kind']) ||
      isolation['processExitConfirmed'] is! bool) {
    throw StateError('Invalid native isolation identity.');
  }
  nativeHex(isolation['provisioningRecordSha256'], 64);
  nativeHex(isolation['acknowledgementSha256'], 64);
  final ack = sandboxObject(isolation['acknowledgement'], 'acknowledgement');
  sandboxFields(ack, {
    'schemaVersion',
    'requestId',
    'challenge',
    'requestSha256',
    'status',
    'process',
    'observedOsIdentity',
    'observedRoots',
    'reasons',
  }, 'acknowledgement');
  if (ack['schemaVersion'] is! int ||
      ack['schemaVersion'] != 1 ||
      ack['challenge'] != challenge ||
      ack['status'] != 'admitted') {
    throw StateError('Invalid acknowledgement correlation.');
  }
  sandboxText(ack['requestId'], 'request ID', maximum: 128);
  nativeHex(ack['requestSha256'], 64);
  final reasons = nativeReasons(ack['reasons']);
  if (ack['status'] == 'admitted' && reasons.isNotEmpty) {
    throw StateError('Admitted acknowledgement contains refusal reasons.');
  }
  final process = sandboxObject(ack['process'], 'native process');
  sandboxFields(process, {
    'pid',
    'nativeStartToken',
    'executablePath',
  }, 'native process');
  nativeInteger(process['pid'], 1, 2147483647);
  final token = sandboxText(
    process['nativeStartToken'],
    'start token',
    maximum: 64,
  );
  if (!RegExp(r'^windows:[1-9][0-9]*$').hasMatch(token)) {
    throw StateError('Invalid native process start token.');
  }
  nativeWindowsPath(process['executablePath']);
  final identity = sandboxObject(ack['observedOsIdentity'], 'OS identity');
  sandboxFields(identity, {
    'userSid',
    'logonId',
    'sessionId',
    'userProfile',
    'localAppDataLow',
  }, 'OS identity');
  final sid = sandboxText(identity['userSid'], 'user SID', maximum: 256);
  if (!RegExp(r'^S-[0-9]+(?:-[0-9]+)+$').hasMatch(sid)) {
    throw StateError('Invalid Windows SID.');
  }
  nativeHex(identity['logonId'], 16);
  nativeInteger(identity['sessionId'], 1, 2147483647);
  nativeWindowsPath(identity['userProfile']);
  nativeWindowsPath(identity['localAppDataLow']);
  final roots = sandboxObject(ack['observedRoots'], 'native roots');
  sandboxFields(roots, {
    'gameRoot',
    'bepInExRoot',
    'managerRoot',
    'persistentDataRoot',
  }, 'native roots');
  for (final path in roots.values) {
    nativeWindowsPath(path);
  }
}

List<Map<String, Object?>> nativeRows(
  Object? value,
  int maximum, {
  bool allowEmpty = true,
}) {
  if (value is! List<Object?> ||
      value.length > maximum ||
      (!allowEmpty && value.isEmpty)) {
    throw StateError('Native collection size is invalid.');
  }
  return value.map((v) => sandboxObject(v, 'native row')).toList();
}

void nativeFile(Map<String, Object?> file, {bool length = false}) {
  sandboxFields(file, {'path', 'sha256', if (length) 'length'}, 'native file');
  nativeRelativePath(file['path']);
  nativeHex(file['sha256'], 64);
  if (length) nativeInteger(file['length'], 1, 134217728);
}

String nativeRelativePath(Object? value) {
  final path = sandboxText(value, 'native relative path', maximum: 240);
  if (!RegExp(r'^[A-Za-z0-9][A-Za-z0-9._/-]*$').hasMatch(path) ||
      path
          .split('/')
          .any(
            (part) =>
                part.isEmpty ||
                part == '.' ||
                part == '..' ||
                part.endsWith('.'),
          ) ||
      path
          .split('/')
          .any(
            (part) => RegExp(
              r'^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\.|$)',
              caseSensitive: false,
            ).hasMatch(part),
          )) {
    throw StateError('Unsafe native relative path.');
  }
  return path;
}

String nativeWindowsPath(Object? value) {
  final path = sandboxText(value, 'native Windows path', maximum: 1024);
  if (!RegExp(r'^[A-Za-z]:\\').hasMatch(path) ||
      path.substring(3).contains(':') ||
      path.contains('/') ||
      path
          .substring(3)
          .split(r'\')
          .any(
            (part) =>
                part.isEmpty ||
                part == '.' ||
                part == '..' ||
                part.endsWith('.') ||
                part.endsWith(' '),
          )) {
    throw StateError('Noncanonical native Windows path.');
  }
  return path;
}

String nativeHex(Object? value, int length) {
  if (value is! String || !RegExp('^[0-9a-f]{$length}\$').hasMatch(value)) {
    throw StateError('Invalid native digest or identity.');
  }
  return value;
}

int nativeInteger(Object? value, int minimum, int maximum) {
  if (value is! int || value < minimum || value > maximum) {
    throw StateError('Invalid native bounded integer.');
  }
  return value;
}

DateTime nativeUtc(Object? value) {
  final text = sandboxText(value, 'native UTC timestamp', maximum: 32);
  if (!RegExp(
    r'^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,6})?Z$',
  ).hasMatch(text)) {
    throw StateError('Invalid native UTC timestamp.');
  }
  final date = DateTime.tryParse(text);
  if (date == null ||
      !date.isUtc ||
      date.toIso8601String().substring(0, 19) != text.substring(0, 19)) {
    throw StateError('Invalid native UTC date.');
  }
  return date;
}

String nativeReason(Object? value, {bool empty = false}) {
  if (empty && value == '') return '';
  return sandboxText(value, 'native reason');
}

List<String> nativeReasons(Object? value) {
  if (value is! List<Object?> || value.length > 64) {
    throw StateError('Invalid native reasons.');
  }
  final reasons = value.map((v) => nativeReason(v)).toList();
  if (reasons.toSet().length != reasons.length) {
    throw StateError('Repeated native reason.');
  }
  return List.unmodifiable(reasons);
}

Object? nativeFreeze(Object? value) {
  if (value is Map<String, Object?>) {
    return Map<String, Object?>.unmodifiable({
      for (final entry in value.entries) entry.key: nativeFreeze(entry.value),
    });
  }
  if (value is List) return List<Object?>.unmodifiable(value.map(nativeFreeze));
  return value;
}

/// Array order remains meaningful when comparing actual private receipts.
bool nativeSame(Object? a, Object? b) =>
    canonicalReleaseJson(a) == canonicalReleaseJson(b);
