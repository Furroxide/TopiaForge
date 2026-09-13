import 'dart:convert';
import 'dart:io';
import 'package:crypto/crypto.dart';
import 'package:topiaforge/src/sandbox_acceptance/native_annex.dart';
import 'package:topiaforge/src/sandbox_acceptance/native_annex_verifier.dart';
import 'package:topiaforge/src/sandbox_acceptance/native_transcript_oracles.dart';
import 'sandbox_native_fixture.dart';

/// Synthetic verifier inputs. These never represent actual native execution.
final class NativeTranscriptFixture {
  NativeTranscriptFixture() {
    value['artifacts'] = <Object?>[];
    final ack = nativeMap(nativeMap(value['isolation'])['acknowledgement']);
    transcript.addAll({
      'schemaVersion': 1,
      'kind': 'sandbox-broker-transcript-v1',
      'runId': value['runId'],
      'challenge': value['challenge'],
      'managerSessionId': 'c' * 32,
      'process': ack['process'],
      'identity': jsonDecode(jsonEncode(ack['observedOsIdentity'])),
      'events': events,
      'inputReleased': true,
      'fixtureReleased': true,
      'failures': <Object?>[],
    });
    observe('prepare', 1);
    persistence('before');
    observe('begin', 3);
    add('input', {
      'action': 'observe-refusal',
      'kind': 'capture',
      'surfaceId': 'sandbox-creator-window',
      'nodeId': '',
      'observedFrame': 3,
      'sentEvents': 0,
    });
    observe('advance', 5);
    observe('cleanup', 6, cleanup: true);
    observe('capture', 8, cleanup: true);
    persistence('after');
    add('cleanup', {'inputReleased': true, 'fixtureReleased': true});
  }
  final value = nativeAnnexFixture();
  final driverBytes = File(
    '../../tests/TopiaForge.SandboxAcceptanceNative/driver-actions-v1.json',
  ).readAsBytesSync();
  final transcript = <String, Object?>{};
  final events = <Map<String, Object?>>[];
  var wire = 0;
  String scenario = 'persistence-refusal';
  void add(String kind, Map<String, Object?> data) => events.add({
    'sequence': events.length + 1,
    'scenarioId': scenario,
    'cycle': 1,
    'stepIndex': 0,
    'step': 'observe-refusal',
    'kind': kind,
    'elapsedMilliseconds': events.length * 50,
    'data': data,
  });
  void observe(String operation, int frame, {bool cleanup = false}) {
    final request = {
      'schemaVersion': 1,
      'challenge': value['challenge'],
      'sequence': ++wire,
      'operation': operation,
      'scenarioId': scenario,
      'cycle': 1,
    };
    add('observation', {
      'request': request,
      'response': {
        ...request,
        'frame': frame,
        'managerSessionId': 'c' * 32,
        'facts': {
          'targetId': 'io.github.furroxide.topiaforge.sandbox.creator.menu',
          'worldSessionId': 'synthetic-native-session',
          'sessionPhase': 'Running',
          'operationAccepted': true, 'mutationSafetyState': 'Unavailable',
          'persistenceIsolationAvailable': false, 'cleanupErrors': <Object?>[],
          'prepared': !cleanup,
          'ownedObjectCount': 0,
          'nativeCleanupPendingObjects': 0,
          'nativeProps': <Object?>[],
          // These deliberately false claims must never change the computed oracle.
          'scenarioState': 'observed', 'completedCycles': 10,
        },
        'unavailableReasons': <Object?>[],
      },
    });
  }

  void persistence(String phase) => add('persistence', {
    'phase': phase,
    'files': [
      {
        'path': 'synthetic/save.bin',
        'exists': true,
        'sha256': 'b' * 64,
        'length': 12,
      },
    ],
    'changedPaths': <Object?>[],
    'overflow': false,
    'unexpectedWrite': false,
  });
  List<Map<String, Object?>> get responses => events
      .where((e) => e['kind'] == 'observation')
      .map((e) => nativeMap(nativeMap(e['data'])['response']))
      .toList();
  List<SandboxNativeOracleResult> evaluate() {
    final bytes = nativeTestBytes(transcript);
    value['driverManifestSha256'] = nativeTestDigest(
      jsonDecode(utf8.decode(driverBytes)),
    );
    // The evaluator binds exact bytes, including repository formatting.
    value['driverManifestSha256'] = digestBytes(driverBytes);
    nativeMap(value['transcript'])['sha256'] = digestBytes(bytes);
    return evaluateSandboxNativeTranscript(
      annex: SandboxNativeAnnex.parse(nativeTestBytes(value)),
      transcriptBytes: bytes,
      driverManifestBytes: driverBytes,
    );
  }
}

String digestBytes(List<int> bytes) => sha256.convert(bytes).toString();
