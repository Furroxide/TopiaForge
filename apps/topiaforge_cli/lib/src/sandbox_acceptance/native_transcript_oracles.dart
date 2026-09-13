import 'native_screen_oracle.dart';
import 'native_audio_oracle.dart';
import 'native_annex.dart';
import 'native_annex_verifier.dart';
import 'native_transcript_actions.dart';
import 'native_transcript_facts.dart';
import 'native_transcript_protocol.dart';
import 'sandbox_specification.dart';

/// Computes supplementary native results from the actual bound exchanges and OS
/// records. Driver scenarioState, completedCycles and annex claims are ignored.
List<SandboxNativeOracleResult> evaluateSandboxNativeTranscript({
  required SandboxNativeAnnex annex,
  required List<int> transcriptBytes,
  required List<int> driverManifestBytes,
  Map<String, NativeAudioMeasurement> audioMeasurements = const {},
  Map<String, NativeScreenMeasurement> screenMeasurements = const {},
  String audioEndpointId = '',
}) {
  final data = readNativeTranscript(
    annex,
    transcriptBytes,
    driverManifestBytes,
  );
  final globalFailure =
      data.document['inputReleased'] != true ||
      data.document['fixtureReleased'] != true ||
      (data.document['failures']! as List).isNotEmpty ||
      data.events.where((e) => e['kind'] == 'cleanup').isEmpty ||
      data.events
          .where((e) => e['kind'] == 'cleanup')
          .any((e) => objectMap(e['data']).values.any((v) => v != true));
  final results = <SandboxNativeOracleResult>[];
  for (final id in sandboxScenarioIds) {
    final records = data.observations.where((o) => o.scenario == id).toList();
    var completed = 0;
    var status = SandboxNativeStatus.missing;
    var reason = 'No independently observed native cycle.';
    try {
      if (globalFailure && records.isNotEmpty) {
        throw StateError(
          'Broker failure or actual input/fixture cleanup is unconfirmed.',
        );
      }
      if (records.isNotEmpty) {
        final cycles = id == 'ten-cycles'
            ? 10
            : id == 'lifecycle-routes'
            ? 3
            : 1;
        final actualCycles = records.map((r) => r.cycle).toSet();
        if (actualCycles.any((c) => c < 1 || c > cycles)) {
          throw StateError('Unknown native cycle.');
        }
        Map<String, Object?>? firstBaseline;
        for (var cycle = 1; cycle <= cycles; cycle++) {
          final observations = records.where((r) => r.cycle == cycle).toList();
          if (observations.isEmpty) {
            throw StateError('Missing native cycle $cycle of $cycles.');
          }
          final baseline = _cycle(
            data,
            id,
            cycle,
            observations,
            audioMeasurements,
            screenMeasurements,
            audioEndpointId,
          );
          if (firstBaseline != null &&
              id == 'ten-cycles' &&
              !resourcesEqual(firstBaseline, baseline, includeUi: true)) {
            throw StateError(
              'Intermediate lifecycle cycle leaked resources into its successor.',
            );
          }
          firstBaseline ??= baseline;
          completed++;
        }
        status = SandboxNativeStatus.passed;
        reason = '';
        // These implemented recipes establish their actual supported branches;
        // they cannot establish a native capability that production does not expose.
        const residuals = {
          'routing':
              'Global-mode host routing remains unavailable; Sandbox routing was observed.',
          'catalog-editing':
              'Built-in native vehicle adapter remains unavailable; admitted catalog entries were exercised.',
          'source-unload':
              'Source registrations were disposed; production package-specific live unload is unavailable.',
          'graph-rollback':
              'Graph resources were observed; native interaction actuation and asynchronous backend completion remain unavailable.',
          'lifecycle-routes':
              'Worlds Stop/Restart/ReturnToMainMenu were observed; package-specific live unload is unavailable.',
        };
        if (residuals.containsKey(id)) {
          status = SandboxNativeStatus.unavailable;
          reason = residuals[id]!;
        }
      }
    } on StateError catch (error) {
      status = SandboxNativeStatus.failed;
      reason = error.message;
    }
    results.add(
      SandboxNativeOracleResult(
        scenarioId: id,
        status: status,
        reason: reason,
        completedSamples: id == 'lifecycle-routes'
            ? (completed == 3 ? 1 : 0)
            : completed,
      ),
    );
  }
  return List.unmodifiable(results);
}

Map<String, Object?> _cycle(
  NativeTranscriptData data,
  String id,
  int cycle,
  List<NativeTranscriptObservation> observations,
  Map<String, NativeAudioMeasurement> audioMeasurements,
  Map<String, NativeScreenMeasurement> screenMeasurements,
  String audioEndpointId,
) {
  final begins = observations.where((o) => o.operation == 'begin').toList();
  if (begins.length != 1) {
    throw StateError('Each native cycle requires one unique begin.');
  }
  final begin = begins.single;
  if (begin.facts['targetId'] != nativeTarget ||
      begin.facts['sessionPhase'] != 'Running' ||
      text(begin.facts, 'worldSessionId').isEmpty ||
      begin.facts['operationAccepted'] != true) {
    throw StateError('Cycle is not bound to the real running Sandbox target.');
  }
  final preparation = observations
      .where((o) => o.operation == 'prepare' && o.order < begin.order)
      .toList();
  if (preparation.length != 1 ||
      preparation.single.facts['operationAccepted'] != true) {
    throw StateError('Missing/duplicate native preparation.');
  }
  final expected = nativeExpectedSteps(id, data.driver, begin.facts);
  final events = data.events
      .where((e) => e['scenarioId'] == id && e['cycle'] == cycle)
      .toList();
  NativeTranscriptObservation previous = begin;
  Map<String, Object?>? graphBaseline;
  Map<String, Object?>? openedUi;
  var borrowed = false;
  NativeAudioMeasurement? runningAudio;
  final usedAdvances = <int>{};
  for (var index = 0; index < expected.length; index++) {
    final action = expected[index];
    final candidates = observations
        .where(
          (o) =>
              o.operation == 'advance' &&
              o.event['stepIndex'] == index &&
              o.event['step'] == action.action &&
              o.order > previous.order,
        )
        .toList();
    if (candidates.isEmpty) {
      throw StateError(
        'No native postcondition for step $index ${action.action}.',
      );
    }
    NativeTranscriptObservation? accepted;
    for (final candidate in candidates) {
      if (candidate.frame <= previous.frame ||
          candidate.facts['operationAccepted'] != true) {
        continue;
      }
      if (candidate.milliseconds - previous.milliseconds > 10000) {
        throw StateError('Native step observation exceeded deadline.');
      }
      if (checkNativePostcondition(
        action.action,
        cycle,
        begin.facts,
        previous.facts,
        candidate.facts,
        graphBaseline: graphBaseline,
        borrowedSelected: borrowed,
        openedUi: openedUi,
      )) {
        accepted = candidate;
        break;
      }
    }
    if (accepted == null) {
      throw StateError('Actual native postcondition failed: ${action.action}.');
    }
    final stepEvents = events
        .where(
          (e) =>
              e['stepIndex'] == index &&
              e['step'] == action.action &&
              (e['sequence']! as int) > previous.order &&
              (e['sequence']! as int) < accepted!.order,
        )
        .toList();
    checkNativeInputs(data, stepEvents, action, previous.facts, accepted.order);
    if ([
      'open',
      'hide',
      'reopen',
      'end-session',
      'stop-world-session',
    ].contains(action.action)) {
      final screens = stepEvents.where((e) => e['kind'] == 'capture').toList();
      if (screens.isEmpty ||
          screens.any(
            (e) => number(objectMap(e['data']), 'distinctSampleColors') < 8,
          )) {
        throw StateError('Native visual observation is missing or blank.');
      }
      for (final screen in screens) {
        final sample = objectMap(screen['data']);
        final actual = screenMeasurements[sample['path']];
        if (actual == null) {
          throw StateError('Actual native BMP bytes are missing.');
        }
        verifyNativeScreenClaim(sample, actual);
        if (sample['width'] != ui(accepted.facts)['width'] ||
            sample['height'] != ui(accepted.facts)['height']) {
          throw StateError('Screenshot and actual client geometry differ.');
        }
      }
    }
    if (['run-graph', 'stop-graph'].contains(action.action)) {
      final actualAudio = _audio(
        stepEvents,
        action.action,
        audioMeasurements,
        audioEndpointId,
      );
      if (action.action == 'run-graph') {
        runningAudio = actualAudio;
      } else if (runningAudio == null ||
          actualAudio.cuePower >= runningAudio.cuePower / 8) {
        throw StateError(
          'The fixture cue did not disappear from actual loopback bytes after graph stop.',
        );
      }
    }
    if (action.action == 'open') openedUi = accepted.facts;
    if (action.action == 'run-graph') graphBaseline = previous.facts;
    if (action.action == 'select-borrowed') borrowed = true;
    for (final candidate in candidates.where(
      (c) => c.order <= accepted!.order,
    )) {
      usedAdvances.add(candidate.order);
    }
    previous = accepted;
  }
  if (observations.any(
    (o) => o.operation == 'advance' && !usedAdvances.contains(o.order),
  )) {
    throw StateError('Extra, repeated or reordered native action.');
  }
  final cleanups = observations
      .where((o) => o.operation == 'cleanup' && o.order > previous.order)
      .toList();
  if (cleanups.isEmpty) {
    throw StateError('Native fixture cleanup operation is missing.');
  }
  final cleanup = cleanups.last;
  final barriers = observations
      .where(
        (o) =>
            o.operation == 'capture' &&
            o.order > cleanup.order &&
            o.frame >= cleanup.frame + 2,
      )
      .toList();
  if (barriers.isEmpty ||
      !barriers.any(
        (o) =>
            o.facts['prepared'] == false &&
            number(o.facts, 'ownedObjectCount') == 0 &&
            number(o.facts, 'nativeCleanupPendingObjects') == 0 &&
            mapRows(o.facts, 'nativeProps').isEmpty &&
            (o.facts['cleanupErrors'] as List?)?.isEmpty == true,
      )) {
    throw StateError(
      'Actual native destruction did not cross the cleanup frame barrier.',
    );
  }
  _persistence(events);
  return begin.facts;
}

NativeAudioMeasurement _audio(
  List<Map<String, Object?>> events,
  String action,
  Map<String, NativeAudioMeasurement> measurements,
  String endpointId,
) {
  final samples = events.where((e) => e['kind'] == 'audio').toList();
  if (samples.length != 1) {
    throw StateError('Native loopback audio sample is missing or repeated.');
  }
  final audio = objectMap(samples.single['data']);
  final actual = measurements[audio['path']];
  if (actual == null ||
      endpointId.isEmpty ||
      audio['endpointId'] != endpointId) {
    throw StateError(
      'Actual WAV bytes or the admitted render endpoint are unconfirmed.',
    );
  }
  verifyNativeAudioClaim(audio, actual);
  for (final key in ['rms', 'peak', 'capturedMilliseconds']) {
    if (audio[key] is! num ||
        !(audio[key]! as num).isFinite ||
        (audio[key]! as num) < 0) {
      throw StateError('Invalid actual audio measurement.');
    }
  }
  if (number(audio, 'frames') == 0 ||
      number(audio, 'discontinuities') >
          (audio['initialDiscontinuity'] == true ? 1 : 0) ||
      number(audio, 'timestampErrors') != 0 ||
      (audio['capturedMilliseconds']! as num) < 500 ||
      number(audio, 'channels') < 1 ||
      number(audio, 'sampleRate') < 8000) {
    throw StateError('Native loopback audio coverage is insufficient.');
  }
  if (action == 'run-graph' && !actual.containsCue) {
    throw StateError('The fixture cue is absent from actual loopback bytes.');
  }
  return actual;
}

void _persistence(List<Map<String, Object?>> events) {
  final snapshots = events
      .where((e) => e['kind'] == 'persistence')
      .map((e) => objectMap(e['data']))
      .toList();
  if (snapshots.length != 2 ||
      snapshots[0]['phase'] != 'before' ||
      snapshots[1]['phase'] != 'after') {
    throw StateError('Persistence before/after observations are incomplete.');
  }
  for (final sample in snapshots) {
    if (sample['overflow'] != false ||
        sample['unexpectedWrite'] != false ||
        (sample['changedPaths'] as List?)?.isNotEmpty != false) {
      throw StateError('Persistent write or monitoring overflow occurred.');
    }
  }
  if ((snapshots[0]['files'] as List?)?.isEmpty != false ||
      !nativeSame(snapshots[0]['files'], snapshots[1]['files'])) {
    throw StateError(
      'Synthetic save/checkpoint bytes changed or no paths were monitored.',
    );
  }
}
