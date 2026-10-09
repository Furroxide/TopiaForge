import 'native_screen_baseline.dart';
import 'native_screen_oracle.dart';
import 'native_audio_oracle.dart';
import 'native_annex.dart';
import 'native_annex_verifier.dart';
import 'native_expected_catalog.dart';
import 'native_transcript_evidence.dart';
import 'native_postconditions.dart';
import 'native_transcript_actions.dart';
import 'native_transcript_facts.dart';
import 'native_transcript_protocol.dart';
import 'sandbox_specification.dart';

const _screenSteps = {
  'open',
  'reopen',
  'hide',
  'hide-f5',
  'hide-close',
  'end-session',
  'stop-world-session',
};

/// Computes supplementary native results from the actual bound exchanges and OS
/// records. Driver scenarioState, completedCycles and annex claims are ignored.
List<SandboxNativeOracleResult> evaluateSandboxNativeTranscript({
  required SandboxNativeAnnex annex,
  required List<int> transcriptBytes,
  required List<int> driverManifestBytes,
  Map<String, NativeAudioMeasurement> audioMeasurements = const {},
  Map<String, NativeScreenMeasurement> screenMeasurements = const {},
  String audioEndpointId = '',
  SandboxExpectedCatalog? expectedCatalog,
  NativeScreenBaselineInputs? screenBaselines,
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
        final cycles = nativeCycleCount(id);
        final actualCycles = records.map((r) => r.cycle).toSet();
        if (actualCycles.any((c) => c < 1 || c > cycles)) {
          throw StateError('Unknown native cycle.');
        }
        Map<String, Object?>? firstBaseline;
        _CycleOutcome? previous;
        for (var cycle = 1; cycle <= cycles; cycle++) {
          final observations = records.where((r) => r.cycle == cycle).toList();
          if (observations.isEmpty) {
            throw StateError('Missing native cycle $cycle of $cycles.');
          }
          final outcome = _cycle(
            data,
            id,
            cycle,
            observations,
            audioMeasurements,
            screenMeasurements,
            audioEndpointId,
            expectedCatalog,
            screenBaselines,
          );
          if (id == 'ten-cycles') {
            _tenCycleInvariants(firstBaseline, previous, outcome);
          }
          firstBaseline ??= outcome.begin;
          previous = outcome;
          completed++;
        }
        status = SandboxNativeStatus.passed;
        reason = '';
        if (id == 'catalog-editing' &&
            expectedCatalog != null &&
            !expectedCatalog.reviewed) {
          status = SandboxNativeStatus.unavailable;
          reason = SandboxExpectedCatalog.reviewedGateReason;
        }
      }
    } on NativeFactUnavailable catch (error) {
      status = SandboxNativeStatus.unavailable;
      reason = error.reason;
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

final class _CycleOutcome {
  _CycleOutcome(
    this.begin,
    this.previewedPersonalityId,
    this.graphAudioIds,
    this.controllerId,
    this.activeInteractionIds,
  );
  final Map<String, Object?> begin;
  final int? previewedPersonalityId, controllerId;
  final List<int> graphAudioIds;
  final Set<int> activeInteractionIds;
}

/// Cross-cycle ten-cycle invariants: no resource leak into a successor, and the
/// previous cycle's active interaction registrations must be inactive.
void _tenCycleInvariants(
  Map<String, Object?>? firstBaseline,
  _CycleOutcome? previous,
  _CycleOutcome outcome,
) {
  if (firstBaseline != null &&
      !resourcesEqual(firstBaseline, outcome.begin, includeUi: true)) {
    throw StateError(
      'Intermediate lifecycle cycle leaked resources into its successor.',
    );
  }
  if (previous != null) {
    final active = _activeInteractions(outcome.begin);
    if (previous.activeInteractionIds.any(active.contains)) {
      throw StateError(
        "The previous cycle's interaction registration is still active.",
      );
    }
  }
}

Set<int> _activeInteractions(Map<String, Object?> facts) {
  final value = facts['interactions'];
  if (value is! List || value.length > 4096) {
    throw NativeFactUnavailable('Native fact interactions is unavailable.');
  }
  final ids = <int>{};
  for (final row in value) {
    if (row is! Map<String, Object?> ||
        row['instanceId'] is! int ||
        row['active'] is! bool ||
        (row['prompt'] != null && row['prompt'] is! String)) {
      throw NativeFactUnavailable('Native fact interactions is unavailable.');
    }
    if (row['active'] == true) ids.add(row['instanceId']! as int);
  }
  return ids;
}

_CycleOutcome _cycle(
  NativeTranscriptData data,
  String id,
  int cycle,
  List<NativeTranscriptObservation> observations,
  Map<String, NativeAudioMeasurement> audioMeasurements,
  Map<String, NativeScreenMeasurement> screenMeasurements,
  String audioEndpointId,
  SandboxExpectedCatalog? expectedCatalog,
  NativeScreenBaselineInputs? screenBaselines,
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
  final expected = nativeExpectedSteps(
    id,
    cycle,
    data.driver,
    begin.facts,
    expectedCatalog,
  );
  final events = data.events
      .where((e) => e['scenarioId'] == id && e['cycle'] == cycle)
      .toList();
  NativeTranscriptObservation previous = begin;
  Map<String, Object?>? graphBaseline, openedUi, externalWrite;
  var borrowed = false;
  int? previewedPersonalityId;
  final graphAudioIds = <int>[];
  var ranGraph = false;
  final activeInteractionIds = <int>{};
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
      if (candidate.frame <= previous.frame) continue;
      if (candidate.facts['operationAccepted'] != true) {
        _requireNoFixtureFailure(candidate);
        continue;
      }
      if (candidate.milliseconds - previous.milliseconds > 10000) {
        throw StateError('Native step observation exceeded deadline.');
      }
      final bool satisfied;
      try {
        satisfied = checkNativePostcondition(
          NativePostcondition(
            scenario: id,
            action: action.action,
            cycle: cycle,
            initial: begin.facts,
            before: previous.facts,
            graphBaseline: graphBaseline,
            borrowedSelected: borrowed,
            openedUi: openedUi,
            externalWrite: externalWrite,
          ),
          candidate.facts,
        );
      } on NativeFactUnavailable catch (error) {
        throw NativeFactUnavailable(
          _withObserverReasons(error.reason, candidate),
        );
      }
      if (satisfied) {
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
    checkNativeMeasuredEvents(stepEvents, previous.facts);
    if (_screenSteps.contains(action.action)) {
      checkNativeScreens(
        id,
        cycle,
        action.action,
        stepEvents,
        accepted,
        screenMeasurements,
        screenBaselines,
      );
    }
    if (['run-graph', 'stop-graph'].contains(action.action)) {
      final actualAudio = checkNativeAudio(
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
    switch (action.action) {
      case 'open':
        openedUi = accepted.facts;
      case 'run-graph':
        graphBaseline = previous.facts;
        ranGraph = true;
        graphAudioIds.addAll(nativeGraphAudioIds(accepted.facts));
      case 'select-borrowed':
        borrowed = true;
      case 'edit-personality':
        previewedPersonalityId =
            objectMap(
                  objectMap(accepted.facts['borrowedRobot'])['brain'],
                )['hackedPersonalityId']
                as int?;
      case 'external-write':
        externalWrite = accepted.facts;
    }
    activeInteractionIds.addAll(_activeInteractions(accepted.facts));
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
  checkNativeCleanupBarrier(
    observations,
    previous,
    previewedPersonalityId,
    graphAudioIds,
    ranGraph: ranGraph,
  );
  checkNativePersistence(events);
  final controllerId = id == 'ten-cycles'
      ? _constantController(observations)
      : null;
  return _CycleOutcome(
    begin.facts,
    previewedPersonalityId,
    graphAudioIds,
    controllerId,
    activeInteractionIds,
  );
}

/// Observer-reported unavailable reasons the verifier surfaces verbatim: a
/// refused bounded fixture operation (`fixture-operation-failed:<code>`) or
/// missing toast diagnostics (`toast-diagnostics-unavailable`).
List<String> _observerReasons(NativeTranscriptObservation observation) => [
  for (final reason
      in observation.response['unavailableReasons'] as List? ?? const [])
    if (reason is String &&
        (reason == 'toast-diagnostics-unavailable' ||
            reason.startsWith('fixture-operation-failed:')))
      reason,
];

/// A reply that refused a bounded operation carries `operationErrorCode` and
/// the matching reason; that step is unavailable, never a substitute pass.
void _requireNoFixtureFailure(NativeTranscriptObservation observation) {
  final code = observation.facts['operationErrorCode'];
  if (code != null && code is! String) {
    throw NativeFactUnavailable(
      'Native fact operationErrorCode is unavailable.',
    );
  }
  final failed = _observerReasons(
    observation,
  ).where((r) => r.startsWith('fixture-operation-failed:')).toList();
  if (failed.isNotEmpty) {
    throw NativeFactUnavailable(
      'Native fixture operation failed: ${failed.join(', ')}.',
    );
  }
}

String _withObserverReasons(
  String reason,
  NativeTranscriptObservation observation,
) {
  final named = _observerReasons(observation);
  return named.isEmpty ? reason : '$reason (${named.join(', ')})';
}

int _constantController(List<NativeTranscriptObservation> observations) {
  int? controller;
  for (final observation in observations) {
    final value = observation.facts['controllerInstanceId'];
    if (value is! int) {
      throw NativeFactUnavailable(
        'Native fact controllerInstanceId is unavailable.',
      );
    }
    if (controller != null && value != controller) {
      throw StateError('The controller identity changed within the cycle.');
    }
    controller = value;
  }
  return controller!;
}
