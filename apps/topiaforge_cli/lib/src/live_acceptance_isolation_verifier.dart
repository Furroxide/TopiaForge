import 'dart:convert';
import 'dart:io';
import 'package:crypto/crypto.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'live_acceptance_models.dart';

/// Replays private evidence bytes; it never admits a new OS identity or launch.
/// The result is private too: release records copy hashes, not its paths.
Future<Map<String, Object?>> verifyLiveAcceptanceIsolation({
  required String evidencePath,
  required String isolationRecordPath,
}) async {
  try {
    final evidence = _readEvidence(evidencePath);
    _evidenceShape(evidence);
    final isolation = _object(evidence['isolation']);
    requireAcceptanceKeys(isolation, const {
      'kind',
      'provisioningRecordSha256',
      'acknowledgementSha256',
      'acknowledgement',
      'processExitConfirmed',
    });
    if (isolation['processExitConfirmed'] != true) {
      throw const FormatException('Owned exit was not confirmed.');
    }
    final record = decodeAcceptanceObject(
      readAcceptanceRecord(File(isolationRecordPath)),
    );
    final game = acceptancePath(record, 'gameRoot');
    final manager = p.join(game, 'BepInEx', 'TopiaForge');
    final embeddedAck = _object(isolation['acknowledgement']);
    final requestId = acceptanceText(embeddedAck, 'requestId', 128);
    final key = LaunchStorageKeys.request(requestId);
    final requestFile = File(
      p.join(manager, 'staging', 'acceptance-isolation-request-$key.json'),
    );
    final requestBytes = readAcceptanceRecord(requestFile);
    final request = decodeAcceptanceObject(requestBytes);
    requireAcceptanceKeys(request, const {
      'schemaVersion',
      'requestId',
      'challenge',
      'profileRequestSha256',
      'isolationEvidenceSha256',
      'issuedAtUtc',
      'expiresAtUtc',
      'expectedOsIdentity',
      'expectedRoots',
    });
    if (request['schemaVersion'] is! int ||
        request['schemaVersion'] != 1 ||
        request['requestId'] != requestId ||
        request['challenge'] != evidence['acceptanceChallenge']) {
      throw const FormatException('Private request correlation is invalid.');
    }
    for (final field in [
      'challenge',
      'profileRequestSha256',
      'isolationEvidenceSha256',
    ]) {
      _digest(request, field);
    }
    final issued = _date(request, 'issuedAtUtc');
    final expires = _date(request, 'expiresAtUtc');
    if (issued.isBefore(_date(evidence, 'startedAtUtc')) ||
        issued.isAfter(_date(evidence, 'completedAtUtc'))) {
      throw const FormatException(
        'Private request falls outside the recorded run interval.',
      );
    }
    if (!expires.isAfter(issued) ||
        expires.difference(issued) > const Duration(minutes: 15)) {
      throw const FormatException('Private request lifetime is invalid.');
    }
    final historicIdentity = _identity(_object(request['expectedOsIdentity']));
    // Injecting recorded identity is exclusively replay validation. Only run()
    // uses the live native primary-token reader for admission before effects.
    final context = AcceptanceIsolationContext.admit(
      recordPath: isolationRecordPath,
      sourceGameRoot: acceptancePath(record, 'sourceGameRoot'),
      outputRoot: acceptancePath(record, 'outputRoot'),
      identityReader: () => historicIdentity,
    );
    if (!sameAcceptancePath(
          evidencePath,
          p.join(context.outputRoot, 'acceptance-result.json'),
        ) ||
        !sameAcceptancePath(acceptanceText(evidence, 'gameDirectory'), game) ||
        context.recordSha256 !=
            _digest(isolation, 'provisioningRecordSha256') ||
        context.recordSha256 != request['isolationEvidenceSha256'] ||
        context.kind != isolation['kind']) {
      throw const FormatException(
        'Private layout or provisioning bytes do not match.',
      );
    }
    _sameRoots(_object(request['expectedRoots']), context.runtimeRoots());
    final native = _object(embeddedAck['process']);
    requireAcceptanceKeys(native, const {
      'pid',
      'nativeStartToken',
      'executablePath',
    });
    final pid = native['pid'];
    final token = acceptanceText(native, 'nativeStartToken', 64);
    final executable = acceptancePath(native, 'executablePath');
    if (pid is! int ||
        pid <= 0 ||
        pid > 2147483647 ||
        !RegExp(r'^windows:[1-9][0-9]*$').hasMatch(token) ||
        !sameAcceptancePath(p.dirname(executable), game)) {
      throw const FormatException('Private process receipt is invalid.');
    }
    final profile = ProfileLaunchConfigurationV4(
      profileId: 'private-replay',
      profileRevision: 1,
      requestId: requestId,
      command: 'main-menu',
      safeMode: false,
      inheritManagerModState: false,
      enabledMods: const [],
      selectedVersions: const {},
      packages: const [],
    );
    final expected = AcceptanceIsolationRequest(
      context,
      profile,
      request,
      sha256.convert(requestBytes).toString(),
    );
    final ack = await LaunchStagingStore(game).readAcceptanceAcknowledgement(
      expected,
      LaunchProcessIdentity(
        pid: pid,
        startTimeUtc: DateTime.utc(1970),
        executablePath: executable,
        nativeStartToken: token,
      ),
    );
    if (ack == null ||
        ack.sha256Digest != _digest(isolation, 'acknowledgementSha256') ||
        _canonical(ack.document) != _canonical(embeddedAck)) {
      throw const FormatException(
        'Actual private acknowledgement bytes do not match evidence.',
      );
    }
    return {
      'schemaVersion': 1,
      'status': 'admitted',
      'gameDirectory': game,
      'managerRoot': manager,
      'provisioningRecordSha256': context.recordSha256,
      'acknowledgementSha256': ack.sha256Digest,
    };
  } on FormatException {
    rethrow;
  } on Object catch (error) {
    throw FormatException(
      'Private acceptance evidence could not be verified: $error',
    );
  }
}

Map<String, Object?> _readEvidence(String path) {
  requireAcceptanceUnlinkedPath(path);
  final file = File(path);
  const maximum = 16 * 1024 * 1024;
  if (FileSystemEntity.typeSync(path, followLinks: false) !=
          FileSystemEntityType.file ||
      file.lengthSync() <= 0 ||
      file.lengthSync() > maximum) {
    throw const FormatException(
      'Private evidence must be a bounded ordinary file.',
    );
  }
  final bytes = file.readAsBytesSync();
  if (bytes.length > maximum) {
    throw const FormatException('Private evidence grew.');
  }
  final text = utf8.decode(bytes, allowMalformed: false);
  rejectDuplicateJsonProperties(text, label: 'Private acceptance evidence');
  return _object(jsonDecode(text));
}

Map<String, Object?> _object(Object? value) {
  if (value is! Map<String, Object?>) {
    throw const FormatException('Private evidence object required.');
  }
  return value;
}

String _digest(Map<String, Object?> value, String key) {
  final result = acceptanceText(value, key, 64);
  if (!RegExp(r'^[0-9a-f]{64}$').hasMatch(result)) {
    throw FormatException('Invalid private $key digest.');
  }
  return result;
}

DateTime _date(Map<String, Object?> value, String key) {
  final text = acceptanceText(value, key, 64);
  final date = DateTime.tryParse(text);
  if (date == null || !(text.endsWith('Z') || text.endsWith('+00:00'))) {
    throw const FormatException('Private UTC timestamp required.');
  }
  return date.toUtc();
}

WindowsAcceptanceIdentity _identity(Map<String, Object?> value) {
  requireAcceptanceKeys(value, const {
    'userSid',
    'logonId',
    'sessionId',
    'userProfile',
    'localAppDataLow',
  });
  final sid = acceptanceText(value, 'userSid', 256);
  final logon = acceptanceText(value, 'logonId', 16);
  final session = value['sessionId'];
  final profile = acceptancePath(value, 'userProfile');
  final low = acceptancePath(value, 'localAppDataLow');
  if (!RegExp(r'^S-[0-9]+(?:-[0-9]+)+$').hasMatch(sid) ||
      !RegExp(r'^[0-9a-f]{16}$').hasMatch(logon) ||
      session is! int ||
      session < 0 ||
      session > 2147483647 ||
      !p.isWithin(profile.toLowerCase(), low.toLowerCase())) {
    throw const FormatException('Recorded native identity is invalid.');
  }
  return WindowsAcceptanceIdentity(
    userSid: sid,
    logonId: logon,
    sessionId: session,
    userProfile: profile,
    localAppDataLow: low,
  );
}

void _sameRoots(Map<String, Object?> actual, Map<String, Object?> expected) {
  requireAcceptanceKeys(actual, expected.keys.toSet());
  for (final key in expected.keys) {
    if (!sameAcceptancePath(
      acceptancePath(actual, key),
      expected[key]! as String,
    )) {
      throw const FormatException(
        'Private runtime roots differ from provisioning.',
      );
    }
  }
}

String _canonical(Object? item) {
  Object? sort(Object? value) {
    if (value is Map<String, Object?>) {
      final keys = value.keys.toList()..sort();
      return {for (final key in keys) key: sort(value[key])};
    }
    if (value is List) return value.map(sort).toList();
    return value;
  }

  return jsonEncode(sort(item));
}

void _evidenceShape(Map<String, Object?> value) {
  requireAcceptanceKeys(value, const {
    'schemaVersion',
    'isolation',
    'startedAtUtc',
    'completedAtUtc',
    'gameDirectory',
    'package',
    'requiredCases',
    'passedCases',
    'missingCases',
    'failures',
    'acceptanceChallenge',
    'lastRunSessionId',
    'acceptancePackageStatus',
    'acceptancePackageReceipt',
    'releaseJourneyEnabled',
    'releaseJourneyAuthoringCommandCount',
    'releaseJourneyCli',
    'releaseJourneyProject',
    'requiredLoadedPackageId',
    'requiredLoadedPackageStatus',
    'requiredLoadedPackageReceipt',
    'requiredLogMarker',
    'requiredLogMarkerObserved',
    'succeeded',
  });
  if (value['schemaVersion'] is! int ||
      value['schemaVersion'] != 3 ||
      value['succeeded'] != true ||
      value['releaseJourneyEnabled'] is! bool ||
      value['requiredLogMarkerObserved'] is! bool ||
      value['releaseJourneyAuthoringCommandCount'] is! int ||
      value['releaseJourneyAuthoringCommandCount'] !=
          (value['releaseJourneyEnabled'] == true ? 2 : 0)) {
    throw const FormatException(
      'Successful private evidence schema3 is required.',
    );
  }
  if (_date(value, 'completedAtUtc').isBefore(_date(value, 'startedAtUtc'))) {
    throw const FormatException('Private evidence time ordering is invalid.');
  }
  _digest(value, 'acceptanceChallenge');
  for (final key in [
    'lastRunSessionId',
    'acceptancePackageStatus',
    'requiredLoadedPackageStatus',
  ]) {
    acceptanceText(value, key, 256);
  }
  for (final key in [
    'package',
    'releaseJourneyCli',
    'releaseJourneyProject',
    'requiredLoadedPackageId',
    'requiredLogMarker',
  ]) {
    if (value[key] is! String || (value[key]! as String).length > 32767) {
      throw const FormatException('Invalid private evidence text.');
    }
  }
  for (final key in [
    'requiredCases',
    'passedCases',
    'missingCases',
    'failures',
  ]) {
    final list = value[key];
    if (list is! List ||
        list.length > 512 ||
        list.any(
          (item) => item is! String || item.isEmpty || item.length > 4096,
        ) ||
        list.toSet().length != list.length) {
      throw const FormatException('Invalid private case list.');
    }
  }
  final required = value['requiredCases']! as List;
  final passed = value['passedCases']! as List;
  if (required.isEmpty ||
      required.length != passed.length ||
      !passed.every(required.contains) ||
      (value['missingCases']! as List).isNotEmpty ||
      (value['failures']! as List).isNotEmpty ||
      value['acceptancePackageStatus'] != 'loaded') {
    throw const FormatException('Private acceptance did not pass.');
  }
  for (final key in [
    'acceptancePackageReceipt',
    if (value['releaseJourneyEnabled'] == true) 'requiredLoadedPackageReceipt',
  ]) {
    final receipt = _object(value[key]);
    requireAcceptanceKeys(receipt, const {'sourceSha256', 'criticalFiles'});
    final files = receipt['criticalFiles'];
    if (files is! List) {
      throw const FormatException('Private critical files must be a list.');
    }
    for (final file in files) {
      requireAcceptanceKeys(_object(file), const {'path', 'sha256'});
    }
    final outcome = LiveAcceptancePackageOutcome.fromJson({
      'id': 'proof',
      'status': 'loaded',
      'valid': true,
      ...receipt,
    });
    if (!outcome.receiptValid) {
      throw const FormatException('Invalid private package receipt.');
    }
  }
  if (value['releaseJourneyEnabled'] == true &&
      (value['requiredLoadedPackageStatus'] != 'loaded' ||
          value['requiredLogMarkerObserved'] != true)) {
    throw const FormatException('Private release journey did not pass.');
  }
}
