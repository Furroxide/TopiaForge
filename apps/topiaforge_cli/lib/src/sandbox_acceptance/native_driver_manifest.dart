import 'native_annex.dart';
import 'native_driver_vocabulary.dart';
import 'sandbox_json.dart';

/// Protocol-v2 driver manifest and transcript structural validators (spec
/// section 2). The `actions` map must declare exactly the closed action
/// inventory with bounded recipes whose atoms satisfy the broker's closed
/// per-kind schema; anything else is refused before an oracle runs.
void validateNativeDriverActions(Object? input) {
  final actions = sandboxObject(input, 'native driver actions');
  if (!actions.keys.every(nativeDriverActions.contains)) {
    throw StateError('Native driver manifest declares an undeclared action.');
  }
  for (final name in nativeDriverActions) {
    if (!actions.containsKey(name)) {
      throw StateError('Native driver action inventory lacks $name.');
    }
    final atoms = actions[name];
    if (atoms is! List ||
        atoms.isEmpty ||
        atoms.length > nativeDriverMaximumAtoms) {
      throw StateError(
        'Native driver action $name needs 1 to $nativeDriverMaximumAtoms '
        'atoms.',
      );
    }
    for (final atom in atoms) {
      validateNativeDriverAtom(sandboxObject(atom, 'native driver atom'));
    }
  }
}

/// Structural parse of the measured motion-atom events. Field presence and
/// bounded arrays are enforced here; numeric ranges and pass/fail outcomes
/// (`visible`, `released`, `converged`) are recomputed per step so a violation
/// fails that step independently rather than the whole transcript.
void validateNativeMeasuredEvent(String kind, Map<String, Object?> data) {
  switch (kind) {
    case 'scroll':
      sandboxFields(data, {
        'nodeId',
        'containerId',
        'attempts',
        'ticksPerAttempt',
        'ticks',
        'probeX',
        'probeY',
        'samples',
        'visible',
      }, 'scroll observation');
      if (data['visible'] is! bool ||
          !_finiteOrNull(data['probeX']) ||
          !_finiteOrNull(data['probeY'])) {
        throw StateError('Invalid scroll observation.');
      }
      for (final sample in nativeRows(data['samples'], 256)) {
        sandboxFields(sample, {
          'observedFrame',
          'clipped',
          'direction',
          'probeX',
          'probeY',
        }, 'scroll sample');
        if (sample['clipped'] is! bool ||
            !['down', 'up', ''].contains(sample['direction']) ||
            !_finiteOrNull(sample['probeX']) ||
            !_finiteOrNull(sample['probeY'])) {
          throw StateError('Invalid scroll sample.');
        }
      }
    case 'mouse-move':
      sandboxFields(data, {'dx', 'dy'}, 'mouse-move observation');
      if (data['dx'] is! int || data['dy'] is! int) {
        throw StateError('Invalid mouse-move observation.');
      }
    case 'key-hold':
      sandboxFields(data, {
        'key',
        'requestedMilliseconds',
        'heldMilliseconds',
        'released',
      }, 'key-hold observation');
      if (data['released'] is! bool ||
          (data['heldMilliseconds'] != null &&
              data['heldMilliseconds'] is! int)) {
        throw StateError('Invalid key-hold observation.');
      }
    case 'aim':
      sandboxFields(data, {
        'fact',
        'gain',
        'maxIterations',
        'iterations',
        'converged',
        'samples',
      }, 'aim observation');
      if (data['converged'] is! bool) {
        throw StateError('Invalid aim observation.');
      }
      for (final sample in nativeRows(data['samples'], 64)) {
        sandboxFields(sample, {
          'iteration',
          'observedFrame',
          'yawDegrees',
          'pitchDegrees',
          'distance',
          'focused',
          'dx',
          'dy',
          'clamped',
        }, 'aim sample');
      }
  }
}

bool _finiteOrNull(Object? value) =>
    value == null || (value is num && value.isFinite);

/// Validates the optional screen `baseline` block attached to a capture event.
void validateNativeScreenBaseline(Object? value) {
  if (value == null) return;
  final baseline = sandboxObject(value, 'screen baseline');
  sandboxFields(baseline, {
    'path',
    'sha256',
    'mismatchFraction',
    'withinTolerance',
  }, 'screen baseline');
  nativeRelativePath(baseline['path']);
  nativeHex(baseline['sha256'], 64);
  final fraction = baseline['mismatchFraction'];
  if (fraction is! num ||
      !fraction.isFinite ||
      fraction < 0 ||
      fraction > 1 ||
      baseline['withinTolerance'] is! bool) {
    throw StateError('Invalid native screen baseline record.');
  }
}
