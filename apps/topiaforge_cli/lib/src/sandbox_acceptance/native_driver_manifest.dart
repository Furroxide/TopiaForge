import 'native_annex.dart';
import 'sandbox_json.dart';

/// Closed protocol-v2 wire and driver vocabularies plus the manifest and
/// measured-event structural validators (spec section 2). The verifier accepts
/// only these keys, nodes, request operations and atoms; anything else is
/// refused before an oracle runs.
const nativeDriverRequestOperations = {
  'unregister-source',
  'request-session-stop',
  'external-write',
  'destroy-borrowed',
  'register-competing-host',
  'unregister-competing-host',
  'control-cue',
  'stop-control-cue',
  'spawn-control-robot',
  'despawn-control-robot',
  'accessibility-high-contrast',
  'accessibility-scale-150',
  'accessibility-reduced-motion',
  'accessibility-reset',
};
const nativeOperations = {
  'prepare',
  'begin',
  'capture',
  'advance',
  'cleanup',
  ...nativeDriverRequestOperations,
};
const nativeDriverKeys = {
  'F5',
  'Escape',
  'Tab',
  'W',
  'Down',
  'Up',
  'Return',
  'Space',
  'MouseLeft',
};
const nativeDriverNodes = {
  // v1 node set.
  'hide-workbench',
  'catalog-search',
  'catalog-list',
  'spawn-selected',
  'duplicate-selected',
  'nudge-up',
  'refresh-native',
  'roster-list',
  'persona-name',
  'persona-instructions',
  'apply-personality',
  'brain-dormant',
  'project-list',
  'load-project',
  'run-project',
  'stop-project',
  'end-session',
  'confirm',
  'rotation-y',
  'rotation-w',
  'apply-transform',
  'scale-x',
  'scale-y',
  'scale-z',
  'remove-selected',
  // v2 additions.
  'undo-last',
  'catalog-kind',
  r'$close',
  r'$scroll-0',
  r'$scroll-1',
  r'$scroll-2',
};
const nativeDriverSurfaces = {'sandbox-creator-window', r'$modal'};
const nativeScrollContainers = {r'$scroll-0', r'$scroll-1', r'$scroll-2'};
const nativeDriverAtomKinds = {
  'click',
  'replace-text',
  'select-list-item',
  'key',
  'capture',
  'barrier',
  'request',
  'scroll-into-view',
  'mouse-move',
  'key-hold',
  'aim',
};

/// Validates the driver manifest `actions` map against the closed protocol-v2
/// vocabularies and atom bounds. Unknown keys, nodes, request operations or
/// out-of-bounds atoms are refused before any oracle runs.
void validateNativeDriverActions(Object? input) {
  final actions = sandboxObject(input, 'native driver actions');
  if (actions.isEmpty || actions.length > 128) {
    throw StateError('Native driver action recipe count is invalid.');
  }
  for (final atoms in actions.values) {
    if (atoms is! List || atoms.isEmpty || atoms.length > 32) {
      throw StateError('Native driver action atom list is invalid.');
    }
    for (final entry in atoms) {
      _validateAtom(sandboxObject(entry, 'native driver atom'));
    }
  }
}

void _validateAtom(Map<String, Object?> atom) {
  final kind = atom['kind'];
  if (!nativeDriverAtomKinds.contains(kind)) {
    throw StateError('Unknown native driver atom kind.');
  }
  final surface = atom['surfaceId'];
  if (surface != null && !nativeDriverSurfaces.contains(surface)) {
    throw StateError('Unknown native driver surface.');
  }
  final node = atom['nodeId'];
  if (node != null && !nativeDriverNodes.contains(node)) {
    throw StateError('Unknown native driver node.');
  }
  switch (kind) {
    case 'key':
    case 'key-hold':
      if (!nativeDriverKeys.contains(atom['key'])) {
        throw StateError('Unknown native driver key.');
      }
      if (kind == 'key-hold') {
        nativeInteger(atom['milliseconds'], 50, 1000);
      }
    case 'request':
      if (!nativeDriverRequestOperations.contains(atom['operation'])) {
        throw StateError('Unknown native driver request operation.');
      }
    case 'mouse-move':
      nativeInteger(atom['dx'], -400, 400);
      nativeInteger(atom['dy'], -400, 400);
    case 'scroll-into-view':
      if (!nativeDriverNodes.contains(atom['nodeId']) ||
          !nativeScrollContainers.contains(atom['containerId'])) {
        throw StateError('Invalid native scroll-into-view target/container.');
      }
    case 'aim':
      if (atom['fact'] != 'aimToGraphProp') {
        throw StateError('Unknown native aim fact.');
      }
      nativeInteger(atom['maxIterations'], 1, 40);
      nativeInteger(atom['gain'], 1, 20);
    case 'barrier':
      nativeInteger(atom['minimumFrames'], 1, 600);
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
