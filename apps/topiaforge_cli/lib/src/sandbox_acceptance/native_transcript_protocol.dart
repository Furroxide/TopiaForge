import 'dart:convert';
import 'native_audio_oracle.dart';
import 'package:crypto/crypto.dart';
import '../release_strict_json.dart';
import 'native_annex.dart';
import 'sandbox_json.dart';
import 'sandbox_specification.dart';

const nativeTarget = 'io.github.furroxide.topiaforge.sandbox.creator.menu';
const nativeOwner = 'io.github.furroxide.topiaforge.sandbox';
const nativeSurface = 'sandbox-creator-window';
const nativeOperations = {
  'prepare',
  'begin',
  'capture',
  'advance',
  'unregister-source',
  'request-session-stop',
  'cleanup',
};

final class NativeTranscriptData {
  NativeTranscriptData(
    this.document,
    this.driver,
    this.events,
    this.observations,
  );
  final Map<String, Object?> document, driver;
  final List<Map<String, Object?>> events;
  final List<NativeTranscriptObservation> observations;
}

final class NativeTranscriptObservation {
  NativeTranscriptObservation(this.event, this.request, this.response);
  final Map<String, Object?> event, request, response;
  String get scenario => request['scenarioId']! as String;
  int get cycle => request['cycle']! as int;
  int get frame => response['frame']! as int;
  int get order => event['sequence']! as int;
  int get milliseconds => event['elapsedMilliseconds']! as int;
  String get operation => request['operation']! as String;
  Map<String, Object?> get facts => objectMap(response['facts']);
}

NativeTranscriptData readNativeTranscript(
  SandboxNativeAnnex annex,
  List<int> bytes,
  List<int> driverBytes,
) {
  if (sha256.convert(bytes).toString() !=
          objectMap(annex.document['transcript'])['sha256'] ||
      sha256.convert(driverBytes).toString() !=
          annex.document['driverManifestSha256']) {
    throw StateError('Transcript/driver byte binding differs.');
  }
  final document = decodeReleaseObject(
    bytes,
    maximumBytes: 128 * 1024 * 1024,
    label: 'Sandbox broker transcript',
  );
  sandboxFields(document, {
    'schemaVersion',
    'kind',
    'runId',
    'challenge',
    'managerSessionId',
    'process',
    'identity',
    'events',
    'inputReleased',
    'fixtureReleased',
    'failures',
  }, 'Sandbox broker transcript');
  final ack = objectMap(annex.isolation['acknowledgement']);
  if (document['schemaVersion'] != 1 ||
      document['kind'] != 'sandbox-broker-transcript-v1' ||
      document['runId'] != annex.runId ||
      document['challenge'] != annex.challenge ||
      !nativeSame(document['process'], ack['process']) ||
      !nativeSame(document['identity'], ack['observedOsIdentity']) ||
      document['inputReleased'] is! bool ||
      document['fixtureReleased'] is! bool) {
    throw StateError('Broker transcript identity/correlation differs.');
  }
  nativeHex(document['managerSessionId'], 32);
  nativeReasons(document['failures']);
  final driver = sandboxDocument(driverBytes, 'Sandbox native driver');
  if (driver['schemaVersion'] != 1 ||
      driver['kind'] != 'sandbox-native-driver-actions-v1' ||
      driver['targetId'] != nativeTarget ||
      driver['geometryOrigin'] != 'bottom-left') {
    throw StateError('Unexpected native driver identity.');
  }
  final scenarios = nativeRows(driver['scenarios'], 9, allowEmpty: false);
  if (!nativeSame(scenarios.map((s) => s['id']).toList(), sandboxScenarioIds)) {
    throw StateError('Missing, duplicate or reordered driver scenario.');
  }
  final events = nativeRows(document['events'], 12000);
  final observations = <NativeTranscriptObservation>[];
  var milliseconds = -1, frame = -1, wireSequence = 0;
  for (var index = 0; index < events.length; index++) {
    final event = events[index];
    sandboxFields(event, {
      'sequence',
      'scenarioId',
      'cycle',
      'stepIndex',
      'step',
      'kind',
      'elapsedMilliseconds',
      'data',
    }, 'broker event');
    if (event['sequence'] != index + 1) {
      throw StateError('Broker events are missing or replayed.');
    }
    final time = nativeInteger(event['elapsedMilliseconds'], 0, 14400000);
    if (time < milliseconds) throw StateError('Broker time moved backwards.');
    milliseconds = time;
    final scenario = event['scenarioId'];
    if (scenario != '' && !sandboxScenarioIds.contains(scenario)) {
      throw StateError('Unknown event scenario.');
    }
    nativeInteger(event['cycle'], 0, 10);
    nativeInteger(event['stepIndex'], -1, 2048);
    if (event['step'] is! String || (event['step']! as String).length > 128) {
      throw StateError('Invalid broker step.');
    }
    final data = objectMap(event['data']);
    switch (event['kind']) {
      case 'observation':
        sandboxFields(data, {'request', 'response'}, 'native exchange');
        final request = objectMap(data['request']),
            response = objectMap(data['response']);
        sandboxFields(request, {
          'schemaVersion',
          'challenge',
          'sequence',
          'operation',
          'scenarioId',
          'cycle',
        }, 'native request');
        sandboxFields(response, {
          'schemaVersion',
          'challenge',
          'sequence',
          'operation',
          'scenarioId',
          'cycle',
          'frame',
          'managerSessionId',
          'facts',
          'unavailableReasons',
        }, 'native response');
        wireSequence++;
        if (request['schemaVersion'] != 1 ||
            request['challenge'] != annex.challenge ||
            request['sequence'] != wireSequence ||
            request['scenarioId'] != scenario ||
            request['cycle'] != event['cycle'] ||
            !nativeOperations.contains(request['operation'])) {
          throw StateError('Native request correlation/replay differs.');
        }
        for (final key in request.keys) {
          if (response[key] != request[key]) {
            throw StateError('Native response correlation differs.');
          }
        }
        if (response['managerSessionId'] != document['managerSessionId']) {
          throw StateError('Native manager generation differs.');
        }
        final current = nativeInteger(response['frame'], 0, 2147483647);
        if (current < frame) throw StateError('Native frame moved backwards.');
        frame = current;
        nativeReasons(response['unavailableReasons']);
        _facts(response['facts']);
        if (utf8.encode(jsonEncode(response)).length > 262144) {
          throw StateError('Native reply exceeds frame bound.');
        }
        observations.add(NativeTranscriptObservation(event, request, response));
      case 'input':
        sandboxFields(data, {
          'action',
          'kind',
          'surfaceId',
          'nodeId',
          'observedFrame',
          'sentEvents',
        }, 'native input');
        nativeInteger(data['observedFrame'], 0, 2147483647);
        nativeInteger(data['sentEvents'], 0, 32768);
      case 'capture':
        sandboxFields(data, {
          'path',
          'width',
          'height',
          'distinctSampleColors',
          'sha256',
          'length',
        }, 'screen observation');
        _artifact(annex, data);
        nativeInteger(data['width'], 1, 16384);
        nativeInteger(data['height'], 1, 16384);
        nativeInteger(data['distinctSampleColors'], 0, 16777216);
      case 'audio':
        sandboxFields(data, {
          'path',
          'sha256',
          'length',
          'endpointId',
          'sampleRate',
          'channels',
          'bitsPerSample',
          'encoding',
          'frames',
          'requestedMilliseconds',
          'capturedMilliseconds',
          'rms',
          'peak',
          'discontinuities',
          'initialDiscontinuity',
          'timestampErrors',
          'silentFrames',
          'startedUtc',
          'completedUtc',
        }, 'audio observation');
        _artifact(annex, data);
        validateNativeAudioMetadata(data);
      case 'persistence':
        sandboxFields(data, {
          'phase',
          'files',
          'changedPaths',
          'overflow',
          'unexpectedWrite',
        }, 'persistence observation');
        if (!['before', 'after'].contains(data['phase']) ||
            data['overflow'] is! bool ||
            data['unexpectedWrite'] is! bool) {
          throw StateError('Invalid persistence observation.');
        }
        for (final file in nativeRows(data['files'], 128)) {
          sandboxFields(file, {
            'path',
            'exists',
            'sha256',
            'length',
          }, 'persistence file');
          nativeRelativePath(file['path']);
          if (file['exists'] is! bool) {
            throw StateError('Invalid persisted file state.');
          }
          if (file['sha256'] != null) nativeHex(file['sha256'], 64);
          nativeInteger(file['length'], 0, 268435456);
        }
      case 'failure':
        sandboxFields(data, {'code'}, 'broker failure');
      case 'cleanup':
        sandboxFields(data, {
          'inputReleased',
          'fixtureReleased',
        }, 'broker cleanup');
        if (data.values.any((v) => v is! bool)) {
          throw StateError('Invalid cleanup observation.');
        }
      default:
        throw StateError('Unknown native transcript event kind.');
    }
  }
  return NativeTranscriptData(document, driver, events, observations);
}

void _artifact(SandboxNativeAnnex annex, Map<String, Object?> value) {
  final path = nativeRelativePath(value['path']);
  nativeHex(value['sha256'], 64);
  nativeInteger(value['length'], 1, 268435456);
  if (!annex.artifacts.any(
    (a) =>
        a['path'] == path &&
        a['sha256'] == value['sha256'] &&
        a['length'] == value['length'],
  )) {
    throw StateError(
      'Observed artifact has no matching byte-bound annex entry.',
    );
  }
}

void _facts(Object? input) {
  final root = objectMap(input);
  var nodes = 0;
  void visit(Object? value, int depth) {
    if (++nodes > 24000 || depth > 12) {
      throw StateError('Native facts exceed structural bounds.');
    }
    if (value is Map<String, Object?>) {
      if (value.length > 128) throw StateError('Oversized native object.');
      for (final item in value.values) {
        visit(item, depth + 1);
      }
    } else if (value is List) {
      if (value.length > 4096) throw StateError('Oversized native array.');
      for (final item in value) {
        visit(item, depth + 1);
      }
    } else if (value is String && value.length > 4096 ||
        value is num && !value.isFinite) {
      throw StateError('Invalid native fact value.');
    }
  }

  visit(root, 0);
}

Map<String, Object?> objectMap(Object? value) =>
    sandboxObject(value, 'native observation');
List<Map<String, Object?>> mapRows(Map<String, Object?> map, String key) =>
    nativeRows(map[key], 4096);
int number(Map<String, Object?> map, String key) =>
    nativeInteger(map[key], 0, 2147483647);
String text(Map<String, Object?> map, String key) {
  final value = map[key];
  if (value is! String || value.length > 4096) {
    throw StateError('Native string $key unavailable.');
  }
  return value;
}
