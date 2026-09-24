import 'dart:convert';
import 'package:crypto/crypto.dart';
import 'package:topiaforge/src/sandbox_acceptance/sandbox_specification.dart';

String nativeTestDigest(Object value) =>
    sha256.convert(nativeTestBytes(value)).toString();
List<int> nativeTestBytes(Object value) => utf8.encode(jsonEncode(value));
Map<String, Object?> nativeMap(Object? value) => value! as Map<String, Object?>;
List<Object?> nativeList(Object? value) => value! as List<Object?>;

/// Deliberately fabricated parser input, never measured execution evidence.
Map<String, Object?> nativeAnnexFixture() =>
    nativeMap(jsonDecode(jsonEncode(_nativeAnnexFixture())));
Map<String, Object?> _nativeAnnexFixture() => {
  'schemaVersion': 1,
  'kind': 'sandbox-workbench-automation-v1',
  'scope': 'supplementary-native-development',
  'runId': 'test-only-native-run',
  'sourceRevision': 'b' * 40,
  'sourceWorkspaceSha256': '8' * 64,
  'specSha256': 'c' * 64,
  'driverManifestSha256': 'd' * 64,
  'challenge': 'a' * 64,
  'startedAtUtc': '2026-09-09T10:00:00.000Z',
  'completedAtUtc': '2026-09-09T10:01:00.000Z',
  'deviceProfileSha256': 'e' * 64,
  'isolation': {
    'kind': 'windows-user',
    'provisioningRecordSha256': 'f' * 64,
    'acknowledgementSha256': '1' * 64,
    'acknowledgement': {
      'schemaVersion': 1,
      'requestId': 'test-request',
      'challenge': 'a' * 64,
      'requestSha256': '2' * 64,
      'status': 'admitted',
      'process': {
        'pid': 1234,
        'nativeStartToken': 'windows:987654321',
        'executablePath': r'D:\QA\game\Robotopia.exe',
      },
      'observedOsIdentity': {
        'userSid': 'S-1-5-21-123-1001',
        'logonId': '1234567890abcdef',
        'sessionId': 2,
        'userProfile': r'D:\QA\profile',
        'localAppDataLow': r'D:\QA\profile\AppData\LocalLow',
      },
      'observedRoots': {
        'gameRoot': r'D:\QA\game',
        'bepInExRoot': r'D:\QA\game\BepInEx',
        'managerRoot': r'D:\QA\game\BepInEx\TopiaForge',
        'persistentDataRoot': r'D:\QA\profile\AppData\LocalLow\TestGame',
      },
      'reasons': [],
    },
    'processExitConfirmed': false,
  },
  'packages': [
    {
      'id': 'dev.topiaforge.sandbox',
      'sourceSha256': '3' * 64,
      'criticalFiles': [
        {'path': 'plugin.dll', 'sha256': '4' * 64},
      ],
    },
  ],
  'broker': {'binarySha256': '5' * 64, 'processId': 2345},
  'transcript': {'path': 'broker-transcript.json', 'sha256': '6' * 64},
  'artifacts': [
    {'path': 'screens/before.png', 'sha256': '7' * 64, 'length': 32},
  ],
  'scenarioResults': [
    for (final id in sandboxScenarioIds)
      {
        'scenarioId': id,
        'status': 'unavailable',
        'reason': 'Test-only fixture.',
      },
  ],
  'cleanup': {
    'inputReleased': false,
    'fixtureReleased': false,
    'originalProcessExitConfirmed': false,
  },
  'failures': ['Test-only fixture; never measured evidence.'],
};
