import 'native_annex.dart';
import 'native_audio_oracle.dart';
import 'native_screen_baseline.dart';
import 'native_screen_oracle.dart';
import 'native_transcript_facts.dart';
import 'native_transcript_protocol.dart';

/// Per-step evidence recomputation for the transcript oracle: screenshots and
/// their reviewed baselines, measured motion-atom outcomes/bounds, loopback
/// audio, and the synthetic persistence before/during/after snapshots.
///
/// Every check derives from the bound transcript bytes; a `NativeFactUnavailable`
/// marks a genuinely missing observation (mapped to `unavailable`), while a
/// `StateError` marks an actual failure of that step.
void checkNativeScreens(
  String id,
  int cycle,
  String step,
  List<Map<String, Object?>> stepEvents,
  NativeTranscriptObservation accepted,
  Map<String, NativeScreenMeasurement> screenMeasurements,
  NativeScreenBaselineInputs? screenBaselines,
) {
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
  final entry = screenBaselines?.baselines.entryFor(id, cycle, step);
  if (entry == null) {
    throw NativeFactUnavailable(
      'Reviewed screen baseline for $id cycle $cycle step $step is unavailable.',
    );
  }
  for (final screen in screens) {
    verifyNativeScreenBaseline(
      screenBaselines!,
      entry,
      objectMap(screen['data']),
    );
  }
}

/// Recomputes the pass/fail outcome and bounds of every measured motion-atom
/// event in a step (spec section 2). A violated bound or a non-converged/
/// unreleased/still-clipped outcome fails the step independently.
void checkNativeMeasuredEvents(
  List<Map<String, Object?>> stepEvents,
  Map<String, Object?> before,
) {
  for (final event in stepEvents) {
    final data = objectMap(event['data']);
    switch (event['kind']) {
      case 'scroll':
        final attempts = number(data, 'attempts');
        if (attempts > 20 ||
            number(data, 'ticksPerAttempt') != 3 ||
            data['visible'] != true) {
          throw StateError(
            'Native scroll-into-view did not reveal its target.',
          );
        }
        _checkScrollProbes(data, before);
      case 'mouse-move':
        if (number(data, 'dx').abs() > 400 || number(data, 'dy').abs() > 400) {
          throw StateError('Native mouse-move exceeded its relative bound.');
        }
      case 'key-hold':
        final held = data['heldMilliseconds'];
        if (data['released'] != true ||
            held is! int ||
            held < 50 ||
            held > 1000 ||
            number(data, 'requestedMilliseconds') < 50 ||
            number(data, 'requestedMilliseconds') > 1000) {
          throw StateError('Native key-hold was aborted or out of bounds.');
        }
      case 'aim':
        final gain = number(data, 'gain');
        final maxIterations = number(data, 'maxIterations');
        if (data['converged'] != true ||
            gain < 1 ||
            gain > 20 ||
            maxIterations > 40 ||
            number(data, 'iterations') > maxIterations) {
          throw StateError('Native aim loop did not converge in bounds.');
        }
    }
  }
}

/// Every recorded wheel-burst probe must land inside the scroll container's
/// measured client rect; a probe outside it fails the step.
void _checkScrollProbes(
  Map<String, Object?> data,
  Map<String, Object?> before,
) {
  final container = widgetAt(
    before,
    nativeSurface,
    data['containerId']! as String,
  );
  if (container == null) {
    throw StateError('Native scroll container is absent.');
  }
  for (final key in ['x', 'y', 'width', 'height']) {
    if (container[key] is! num) {
      throw StateError('Native scroll container lacks a measured rect.');
    }
  }
  final left = container['x']! as num, top = container['y']! as num;
  final right = left + (container['width']! as num);
  final bottom = top + (container['height']! as num);
  void check(Object? px, Object? py) {
    if (px == null && py == null) return;
    if (px is! num ||
        py is! num ||
        px < left ||
        px > right ||
        py < top ||
        py > bottom) {
      throw StateError('Native scroll probe fell outside its container rect.');
    }
  }

  check(data['probeX'], data['probeY']);
  for (final sample in mapRows(data, 'samples')) {
    check(sample['probeX'], sample['probeY']);
  }
}

NativeAudioMeasurement checkNativeAudio(
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

void checkNativePersistence(List<Map<String, Object?>> events) {
  final snapshots = events
      .where((e) => e['kind'] == 'persistence')
      .map((e) => objectMap(e['data']))
      .toList();
  const phases = ['before', 'during', 'after'];
  if (snapshots.length != phases.length ||
      !List.generate(
        phases.length,
        (i) => snapshots[i]['phase'] == phases[i],
      ).every((v) => v)) {
    throw StateError(
      'Persistence before/during/after observations are incomplete.',
    );
  }
  for (final sample in snapshots) {
    if (sample['overflow'] != false ||
        sample['unexpectedWrite'] != false ||
        (sample['changedPaths'] as List?)?.isNotEmpty != false) {
      throw StateError('Persistent write or monitoring overflow occurred.');
    }
  }
  if ((snapshots.first['files'] as List?)?.isEmpty != false ||
      snapshots.any((s) => !nativeSame(s['files'], snapshots.first['files']))) {
    throw StateError(
      'Synthetic save/checkpoint bytes changed or no paths were monitored.',
    );
  }
}

/// The graph fixture's cue id; a graph audio host is "named after the cue"
/// while its object name still contains it.
const _graphCueId = 'sandbox-acceptance-graph';

/// Confirms native destruction crossed the cleanup frame barrier (two frames
/// after cleanup) and applies the per-cycle resource rules there: the previewed
/// personality asset is destroyed literally, and graph audio hosts are released.
void checkNativeCleanupBarrier(
  List<NativeTranscriptObservation> observations,
  NativeTranscriptObservation previous,
  int? previewedPersonalityId,
  List<int> graphAudioIds, {
  required bool ranGraph,
}) {
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
            o.frame >= cleanup.frame + 2 &&
            o.facts['prepared'] == false &&
            number(o.facts, 'ownedObjectCount') == 0 &&
            number(o.facts, 'nativeCleanupPendingObjects') == 0 &&
            mapRows(o.facts, 'nativeProps').isEmpty &&
            (o.facts['cleanupErrors'] as List?)?.isEmpty == true,
      )
      .toList();
  if (barriers.isEmpty) {
    throw StateError(
      'Actual native destruction did not cross the cleanup frame barrier.',
    );
  }
  final barrier = barriers.first;
  _requirePersonalityDestroyed(barrier.facts, previewedPersonalityId);
  if (ranGraph) _requireGraphAudioReleased(barrier.facts, graphAudioIds);
}

/// Graph audio host ids the verifier itself saw playing at run-graph.
List<int> nativeGraphAudioIds(Map<String, Object?> facts) {
  final value = facts['graphAudioSources'];
  if (value is! List || value.length > 512) {
    throw NativeFactUnavailable(
      'Native fact graphAudioSources is unavailable.',
    );
  }
  return [
    for (final row in value)
      if (row is Map<String, Object?> && row['instanceId'] is int)
        row['instanceId']! as int
      else
        throw NativeFactUnavailable(
          'Native fact graphAudioSources is unavailable.',
        ),
  ];
}

/// Literal barrier: the id seen at edit-personality and the id the observer
/// serves as `previewedPersonalityId` must both be absent from
/// `personalityAssetIds`.
void _requirePersonalityDestroyed(Map<String, Object?> facts, int? previewed) {
  if (previewed == null) return;
  final ids = {previewed};
  final served = factInt(facts, 'previewedPersonalityId');
  if (served != 0) ids.add(served);
  final live = factIntList(facts, 'personalityAssetIds');
  if (ids.any(live.contains)) {
    throw StateError(
      'The previewed personality asset was not destroyed after cleanup.',
    );
  }
}

/// Release, not destruction: the shipping OwnerAudioService pools stopped
/// sources (renamed "TopiaForge.Audio.Pooled", at most 24 retained), so a graph
/// host may legitimately stay alive. Each id the verifier saw or the observer
/// serves in `graphAudioIds` must be absent from `audioSourceIds` or described
/// in `audioSources` as not playing and not named after the cue, and no playing
/// source may still carry the cue name.
void _requireGraphAudioReleased(Map<String, Object?> facts, List<int> seen) {
  final ids = {...seen, ...factIntList(facts, 'graphAudioIds')};
  final live = factIntList(facts, 'audioSourceIds');
  final sources = _audioSources(facts);
  for (final id in ids) {
    if (!live.contains(id)) continue;
    final rows = sources.where((s) => s['instanceId'] == id).toList();
    if (rows.length != 1) {
      throw StateError(
        'A live graph audio host is not described in audioSources.',
      );
    }
    final row = rows.single;
    if (row['playing'] != false ||
        (row['name']! as String).contains(_graphCueId)) {
      throw StateError('A graph audio host was not released after cleanup.');
    }
  }
  if (sources.any(
    (s) => s['playing'] == true && (s['name']! as String).contains(_graphCueId),
  )) {
    throw StateError(
      'A playing audio source is still named after the graph cue.',
    );
  }
}

List<Map<String, Object?>> _audioSources(Map<String, Object?> facts) {
  final value = facts['audioSources'];
  if (value is! List || value.length > 4096) {
    throw NativeFactUnavailable('Native fact audioSources is unavailable.');
  }
  final rows = <Map<String, Object?>>[];
  for (final row in value) {
    if (row is! Map<String, Object?> ||
        row['instanceId'] is! int ||
        row['name'] is! String ||
        row['playing'] is! bool) {
      throw NativeFactUnavailable('Native fact audioSources is unavailable.');
    }
    rows.add(row);
  }
  return rows;
}
