import 'dart:convert';
import 'dart:io';
import 'package:crypto/crypto.dart';
import 'package:topiaforge/src/sandbox_acceptance/native_annex.dart';
import 'package:topiaforge/src/sandbox_acceptance/native_annex_verifier.dart';
import 'package:topiaforge/src/sandbox_acceptance/native_audio_oracle.dart';
import 'package:topiaforge/src/sandbox_acceptance/native_expected_catalog.dart';
import 'package:topiaforge/src/sandbox_acceptance/native_screen_oracle.dart';
import 'package:topiaforge/src/sandbox_acceptance/native_transcript_actions.dart';
import 'package:topiaforge/src/sandbox_acceptance/native_transcript_oracles.dart';
import 'sandbox_native_fixture.dart';
import 'sandbox_native_scene.dart';
import 'sandbox_native_steps.dart';

/// Builds a complete, valid protocol-v2 transcript for one scenario by walking
/// the real driver manifest, then lets tests mutate a single fact or event to
/// flip the scenario. Fabricated input, never actual native execution.
final class NativeScenarioTranscript {
  NativeScenarioTranscript(this.scenarioId, {SandboxExpectedCatalog? catalog})
    : expected = catalog ?? sceneExpectedCatalog() {
    final ack = nativeMap(nativeMap(annex['isolation'])['acknowledgement']);
    _process = nativeMap(ack['process']);
    _identity = jsonDecode(jsonEncode(ack['observedOsIdentity']));
  }
  final String scenarioId;
  final SandboxExpectedCatalog expected;
  final Map<String, Object?> annex = nativeAnnexFixture();
  final List<int> driverBytes = File(
    '../../tests/TopiaForge.SandboxAcceptanceNative/driver-actions-v2.json',
  ).readAsBytesSync();
  late final Map<String, Object?> driver =
      jsonDecode(utf8.decode(driverBytes)) as Map<String, Object?>;
  final List<Map<String, Object?>> events = [];
  final List<Map<String, Object?>> artifacts = [];
  final Map<String, NativeAudioMeasurement> audioMeasurements = {};
  final Map<String, NativeScreenMeasurement> screenMeasurements = {};
  final Set<String> screenBaselineSteps = {};
  final Map<String, void Function(SandboxScene)> _overrides = {};
  final Map<String, void Function(Map<String, Object?>)> _eventEdits = {};
  final scene = SandboxScene();
  late final Map<String, Object?> _process;
  late final Object? _identity;
  int _wire = 0, _frame = 0, _ms = 0, _artifact = 0, _cycle = 1;
  bool _built = false;

  int get cycleCount => nativeCycleCount(scenarioId);

  /// Runs [mutate] on the scene just before the advance for the given step.
  void beforeAdvance(
    int cycle,
    int index,
    void Function(SandboxScene) mutate,
  ) => _overrides['$cycle|$index'] = mutate;

  /// Registers a scene mutation just before the [occurrence]-th [action] step
  /// of [cycle], resolving the index from the verifier's own recipe.
  void atStep(
    int cycle,
    String action,
    void Function(SandboxScene) mutate, {
    int occurrence = 0,
  }) {
    final probe = SandboxScene();
    resetSceneForCycle(probe, cycle, preparedBorrowed: true);
    final actions = nativeExpectedSteps(
      scenarioId,
      cycle,
      driver,
      probe.snapshot(),
      expected,
    ).map((s) => s.action).toList();
    var seen = -1;
    for (var i = 0; i < actions.length; i++) {
      if (actions[i] == action && ++seen == occurrence) {
        beforeAdvance(cycle, i, mutate);
        return;
      }
    }
    throw StateError(
      'No step $action#$occurrence in $scenarioId cycle $cycle.',
    );
  }

  /// Edits an already-emitted event's `data`; keyed by the emission ordinal.
  void editEvent(
    String kind,
    int ordinal,
    void Function(Map<String, Object?>) fn,
  ) => _eventEdits['$kind|$ordinal'] = fn;

  void _build() {
    if (_built) return;
    _built = true;
    const borrowed = {'borrowed-robot', 'hide-reopen', 'ten-cycles'};
    for (var cycle = 1; cycle <= cycleCount; cycle++) {
      _cycle = cycle;
      resetSceneForCycle(
        scene,
        cycle,
        preparedBorrowed: borrowed.contains(scenarioId),
      );
      _observation('prepare');
      _persistence('before');
      _observation('begin');
      _persistence('during');
      final steps = nativeExpectedSteps(
        scenarioId,
        cycle,
        driver,
        scene.snapshot(),
        expected,
      );
      for (var index = 0; index < steps.length; index++) {
        _step(index, steps[index]);
      }
      _cleanupCycle();
      _persistence('after');
    }
    _event('cleanup', -1, '', {'inputReleased': true, 'fixtureReleased': true});
  }

  void _step(int index, NativeOracleStep step) {
    applyNativeStep(scene, scenarioId, _cycle, step);
    _overrides['$_cycle|$index']?.call(scene);
    final atoms = (nativeMap(driver['actions'])[step.action]! as List)
        .cast<Map<String, Object?>>();
    for (final atom in atoms) {
      _atomEvents(index, step, atom);
    }
    if (SandboxScene.screenSteps.contains(step.action)) {
      _screenshot(index, step);
    }
    if (step.action == 'run-graph') _audio(index, step, cue: true);
    if (step.action == 'stop-graph') _audio(index, step, cue: false);
    _observation('advance', stepIndex: index, step: step.action);
  }

  void _atomEvents(
    int index,
    NativeOracleStep step,
    Map<String, Object?> atom,
  ) {
    final kind = atom['kind']! as String;
    switch (kind) {
      case 'scroll-into-view':
        _event('scroll', index, step.action, {
          'nodeId': atom['nodeId'],
          'containerId': atom['containerId'],
          'attempts': 1,
          'ticksPerAttempt': 3,
          'ticks': 3,
          'probeX': 60,
          'probeY': 60,
          'samples': [
            // Nullable values: the broker records null probes when no burst
            // followed a sample, and tests exercise that shape.
            <String, Object?>{
              'observedFrame': _frame,
              'clipped': false,
              'direction': 'down',
              'probeX': 60,
              'probeY': 60,
            },
          ],
          'visible': true,
        });
      case 'mouse-move':
        _event('mouse-move', index, step.action, {
          'dx': atom['dx'],
          'dy': atom['dy'],
        });
      case 'key-hold':
        _event('key-hold', index, step.action, {
          'key': atom['key'],
          'requestedMilliseconds': atom['milliseconds'],
          'heldMilliseconds': atom['milliseconds'],
          'released': true,
        });
      case 'aim':
        _event('aim', index, step.action, {
          'fact': atom['fact'],
          'gain': atom['gain'],
          'maxIterations': atom['maxIterations'],
          'iterations': 3,
          'converged': true,
          'samples': [
            {
              'iteration': 0,
              'observedFrame': _frame,
              'yawDegrees': 12.0,
              'pitchDegrees': 6.0,
              'distance': 3.0,
              'focused': false,
              'dx': -60,
              'dy': -30,
              'clamped': false,
            },
          ],
        });
    }
    final sends = !const {'capture', 'barrier', 'request'}.contains(kind);
    _event('input', index, step.action, {
      'action': step.action,
      'kind': kind,
      'surfaceId': atom['surfaceId'] ?? 'sandbox-creator-window',
      'nodeId': atom['nodeId'] ?? '',
      'observedFrame': _frame,
      'sentEvents': sends ? 4 : 0,
    });
  }

  void _screenshot(int index, NativeOracleStep step) {
    final path = 'screens/$scenarioId-$_cycle-${++_artifact}.bmp';
    final sha256Hex = List.filled(64, '3').join();
    artifacts.add({'path': path, 'sha256': sha256Hex, 'length': 128});
    screenMeasurements[path] = const NativeScreenMeasurement(1920, 1080, 100);
    screenBaselineSteps.add('$scenarioId|$_cycle|${step.action}');
    _event('capture', index, step.action, {
      'path': path,
      'width': 1920,
      'height': 1080,
      'distinctSampleColors': 100,
      'sha256': sha256Hex,
      'length': 128,
      'baseline': {
        'path': 'baselines/$path',
        'sha256': sha256Hex,
        'mismatchFraction': 0.0,
        'withinTolerance': true,
      },
    });
  }

  void _audio(int index, NativeOracleStep step, {required bool cue}) {
    final path = 'audio/$scenarioId-$_cycle-${++_artifact}.wav';
    final sha256Hex = List.filled(64, '4').join();
    artifacts.add({'path': path, 'sha256': sha256Hex, 'length': 256});
    final m = cue
        ? const NativeAudioMeasurement(
            48000,
            2,
            32,
            'ieee-float',
            48000,
            0.1,
            0.2,
            1e-6,
            1e-9,
          )
        : const NativeAudioMeasurement(
            48000,
            2,
            32,
            'ieee-float',
            48000,
            0.1,
            0.2,
            1e-9,
            1e-9,
          );
    audioMeasurements[path] = m;
    _event('audio', index, step.action, {
      'path': path,
      'sha256': sha256Hex,
      'length': 256,
      'endpointId': 'synthetic-render-endpoint',
      'sampleRate': m.sampleRate,
      'channels': m.channels,
      'bitsPerSample': m.bits,
      'encoding': m.encoding,
      'frames': m.frames,
      'requestedMilliseconds': 1000,
      'capturedMilliseconds': m.milliseconds,
      'rms': m.rms,
      'peak': m.peak,
      'discontinuities': 0,
      'initialDiscontinuity': false,
      'timestampErrors': 0,
      'silentFrames': 0,
      'startedUtc': '2026-09-09T12:00:00.000Z',
      'completedUtc': '2026-09-09T12:00:01.000Z',
    });
  }

  void _cleanupCycle() {
    applySceneCleanup(scene);
    _observation('cleanup');
    _frame += 3;
    _observation('capture');
  }

  void _persistence(String phase) => _event('persistence', -1, '', {
    'phase': phase,
    'files': [
      {
        'path': 'saves/save.bin',
        'exists': true,
        'sha256': 'c' * 64,
        'length': 8,
      },
    ],
    'changedPaths': <Object?>[],
    'overflow': false,
    'unexpectedWrite': false,
  });

  void _observation(String operation, {int stepIndex = -1, String step = ''}) {
    _frame += 1;
    final request = {
      'schemaVersion': 1,
      'challenge': annex['challenge'],
      'sequence': ++_wire,
      'operation': operation,
      'scenarioId': scenarioId,
      'cycle': _cycle,
    };
    _event('observation', stepIndex, step, {
      'request': request,
      'response': {
        ...request,
        'frame': _frame,
        'managerSessionId': 'c' * 32,
        'facts': scene.snapshot(),
        'unavailableReasons': <Object?>[...scene.unavailableReasons],
      },
    });
  }

  final Map<String, int> _ordinals = {};
  void _event(
    String kind,
    int stepIndex,
    String step,
    Map<String, Object?> data,
  ) {
    _ms += 5;
    final ordinal = _ordinals.update(kind, (v) => v + 1, ifAbsent: () => 0);
    _eventEdits['$kind|$ordinal']?.call(data);
    events.add({
      'sequence': events.length + 1,
      'scenarioId': scenarioId,
      'cycle': _cycle,
      'stepIndex': stepIndex,
      'step': step,
      'kind': kind,
      'elapsedMilliseconds': _ms,
      'data': data,
    });
  }

  /// Top-level transcript document overrides (e.g. inputReleased/failures).
  final Map<String, Object?> documentOverrides = {};

  List<int> get transcriptBytes {
    _build();
    return utf8.encode(
      jsonEncode({
        'schemaVersion': 1,
        'kind': 'sandbox-broker-transcript-v1',
        'runId': annex['runId'],
        'challenge': annex['challenge'],
        'managerSessionId': 'c' * 32,
        'process': _process,
        'identity': _identity,
        'events': events,
        'inputReleased': true,
        'fixtureReleased': true,
        'failures': <Object?>[],
        ...documentOverrides,
      }),
    );
  }

  /// Driver bytes used only when evaluating (identity/vocabulary faults). The
  /// transcript is always built against the real manifest.
  List<int>? driverOverride;

  /// Expected catalog used only when evaluating (missing-entry/source faults);
  /// the transcript is always built against a matching, reviewed inventory.
  SandboxExpectedCatalog? catalogForEvaluate;

  List<SandboxNativeOracleResult> evaluate() {
    final bytes = transcriptBytes;
    final driverEvalBytes = driverOverride ?? driverBytes;
    annex['artifacts'] = artifacts;
    annex['driverManifestSha256'] = sha256.convert(driverEvalBytes).toString();
    nativeMap(annex['transcript'])['sha256'] = sha256.convert(bytes).toString();
    return evaluateSandboxNativeTranscript(
      annex: SandboxNativeAnnex.parse(nativeTestBytes(annex)),
      transcriptBytes: bytes,
      driverManifestBytes: driverEvalBytes,
      audioMeasurements: audioMeasurements,
      screenMeasurements: screenMeasurements,
      audioEndpointId: 'synthetic-render-endpoint',
      expectedCatalog: catalogForEvaluate ?? expected,
      screenBaselineSteps: screenBaselineSteps,
    );
  }

  SandboxNativeOracleResult result() =>
      evaluate().singleWhere((r) => r.scenarioId == scenarioId);
}
