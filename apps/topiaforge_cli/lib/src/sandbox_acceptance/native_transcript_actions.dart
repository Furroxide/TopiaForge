import 'native_annex.dart';
import 'native_expected_catalog.dart';
import 'native_transcript_protocol.dart';

final class NativeOracleStep {
  const NativeOracleStep(this.action, {this.row = '', this.label = ''});
  final String action, row, label;
}

/// Fixed per-scenario cycle counts (spec section 4).
const nativeCycleCounts = <String, int>{
  'routing': 2,
  'catalog-editing': 1,
  'borrowed-robot': 3,
  'source-unload': 2,
  'hide-reopen': 1,
  'persistence-refusal': 1,
  'graph-rollback': 2,
  'lifecycle-routes': 3,
  'ten-cycles': 10,
};
int nativeCycleCount(String id) => nativeCycleCounts[id]!;

/// The verifier's own authoritative per-cycle recipes. The driver manifest is
/// cross-checked against these; it never defines outcomes. `$catalog` is the
/// reviewed per-entry catalog-editing expansion placeholder.
List<String> _recipe(String id, int cycle) {
  switch (id) {
    case 'routing':
      return cycle == 1
          ? const [
              'open',
              'accessibility-high-contrast',
              'accessibility-scale-150',
              'accessibility-reduced-motion',
              'accessibility-reset',
              'focus-next',
              'hide-f5',
              'reopen',
              'duplicate-toggle',
              'end-session',
            ]
          : const [
              'register-competing-host',
              'open',
              'hide-f5',
              'reopen',
              'end-session',
              'unregister-competing-host',
            ];
    case 'catalog-editing':
      return const [
        'open',
        'search-nonmatching',
        'filter-robots',
        'filter-all',
        r'$catalog',
        'end-session',
      ];
    case 'borrowed-robot':
      switch (cycle) {
        case 1:
          return const [
            'open',
            'select-borrowed',
            'edit-transform',
            'edit-personality',
            'edit-brain',
            'end-session',
          ];
        case 2:
          return const [
            'open',
            'select-borrowed',
            'edit-transform',
            'edit-personality',
            'external-write',
            'end-session',
          ];
        default:
          return const [
            'open',
            'select-borrowed',
            'edit-transform',
            'destroy-borrowed',
            'end-session',
          ];
      }
    case 'source-unload':
      return cycle == 1
          ? const [
              'open',
              'spawn-prop',
              'duplicate',
              'spawn-character',
              'spawn-control-robot',
              'unregister-source',
              'end-session',
              'despawn-control-robot',
            ]
          : const [
              'open',
              'spawn-prop',
              'run-graph',
              'unregister-source',
              'end-session',
            ];
    case 'hide-reopen':
      return const [
        'open',
        'spawn-prop',
        'select-borrowed',
        'edit-transform',
        'run-graph',
        'move-while-visible',
        'hide-close',
        'move-player',
        'camera-hidden',
        'reopen',
        'text-focus',
        'hide-f5',
        'reopen',
        'stop-graph',
        'end-session',
      ];
    case 'persistence-refusal':
      return const ['observe-refusal'];
    case 'graph-rollback':
      return cycle == 1
          ? const [
              'open',
              'spawn-prop',
              'control-cue',
              'run-graph',
              'hide-f5',
              'aim-graph-prop',
              'interact',
              'reopen',
              'stop-graph',
              'stop-control-cue',
              'end-session',
            ]
          : const [
              'open',
              'spawn-prop',
              'stop-before-start',
              'run-graph',
              'stop-graph',
              'run-graph',
              'stop-graph',
              'end-session',
            ];
    case 'lifecycle-routes':
      switch (cycle) {
        case 1:
          return const ['open', 'spawn-prop', 'stop-world-session'];
        case 2:
          return const [
            'open',
            'spawn-prop',
            'stop-world-session',
            'toggle-during-transition',
            'move-player',
          ];
        default:
          return const [
            'open',
            'spawn-prop',
            'stop-world-session',
            'toggle-in-menu',
          ];
      }
    case 'ten-cycles':
      return const [
        'open',
        'spawn-prop',
        'select-borrowed',
        'edit-transform',
        'edit-personality',
        'edit-brain',
        'hide-f5',
        'move-player',
        'camera-hidden',
        'reopen',
        'run-graph',
        'stop-graph',
        'end-session',
      ];
    default:
      throw StateError('Unknown native scenario recipe.');
  }
}

/// Extracts the declared step list for [cycle] from a v2 manifest scenario:
/// `cycleSteps: [{cycle, steps}]` when cycles differ, else a plain `steps`.
Object? _declaredSteps(Map<String, Object?> manifest, int cycle) {
  final cycleSteps = manifest['cycleSteps'];
  if (cycleSteps == null) {
    final steps = manifest['steps'];
    if (steps is! List) throw StateError('Driver omits native steps.');
    return steps;
  }
  if (cycleSteps is! List) {
    throw StateError('Driver misshapes per-cycle native steps.');
  }
  final matches = cycleSteps
      .whereType<Map<String, Object?>>()
      .where((entry) => entry['cycle'] == cycle)
      .toList();
  if (matches.length != 1) {
    throw StateError('Driver omits per-cycle steps for cycle $cycle.');
  }
  return matches.single['steps'];
}

List<NativeOracleStep> nativeExpectedSteps(
  String id,
  int cycle,
  Map<String, Object?> driver,
  Map<String, Object?> baseline,
  SandboxExpectedCatalog? expectedCatalog,
) {
  final manifest = mapRows(
    driver,
    'scenarios',
  ).singleWhere((s) => s['id'] == id);
  final declared = _declaredSteps(manifest, cycle);
  final recipe = _recipe(id, cycle);
  if (!nativeSame(declared, recipe)) {
    throw StateError('Driver omits/reorders required native actions.');
  }
  final result = <NativeOracleStep>[];
  for (final action in recipe) {
    if (action != r'$catalog') {
      result.add(NativeOracleStep(action));
      continue;
    }
    _expandCatalog(baseline, expectedCatalog, result);
  }
  return result;
}

void _expandCatalog(
  Map<String, Object?> baseline,
  SandboxExpectedCatalog? expectedCatalog,
  List<NativeOracleStep> result,
) {
  final entries = [
    ...mapRows(baseline, 'catalog'),
    ...mapRows(baseline, 'robotCatalog'),
  ];
  if (entries.isEmpty || entries.length > 256) {
    throw StateError('Native catalog inventory missing or oversized.');
  }
  if (expectedCatalog == null) {
    throw StateError('Reviewed expected catalog inventory is unavailable.');
  }
  // Section 5: a listed source/entry absent from the observed catalog fails.
  expectedCatalog.requirePresent(
    entries,
    baseline['catalogSources'] is List
        ? (baseline['catalogSources']! as List)
              .whereType<Map<String, Object?>>()
              .toList()
        : throw StateError('Observed catalog sources are unavailable.'),
  );
  final seen = <String>{};
  for (final entry in entries) {
    final row = text(entry, 'rowId'), label = text(entry, 'displayName');
    if (!seen.add(row) || row.isEmpty || label.isEmpty) {
      throw StateError('Native catalog identity repeats.');
    }
    void add(String action) =>
        result.add(NativeOracleStep(action, row: row, label: label));
    add('spawn-catalog');
    final capabilities = number(entry, 'transformCapabilities');
    if (capabilities > 7) throw StateError('Unknown transform capabilities.');
    if (capabilities & 1 != 0) add('edit-transform');
    if (capabilities & 2 != 0) add('edit-rotation');
    if (capabilities & 4 != 0) add('edit-scale');
    add('duplicate');
    add('undo');
    add('remove');
  }
}

const _sentEventKinds = {
  'click',
  'replace-text',
  'select-list-item',
  'key',
  'key-hold',
  'mouse-move',
  'aim',
  'scroll-into-view',
};
const _geometryKinds = {'click', 'replace-text', 'select-list-item'};

void checkNativeInputs(
  NativeTranscriptData transcript,
  List<Map<String, Object?>> events,
  NativeOracleStep step,
  Map<String, Object?> before,
  int afterOrder,
) {
  final definitions = objectMap(transcript.driver['actions'])[step.action];
  if (definitions is! List) throw StateError('Driver lacks an action recipe.');
  final inputs = events
      .where(
        (e) => e['kind'] == 'input' && (e['sequence']! as int) < afterOrder,
      )
      .toList();
  if (inputs.length != definitions.length) {
    throw StateError('Missing, duplicate or reordered native input atoms.');
  }
  for (var index = 0; index < definitions.length; index++) {
    final expected = objectMap(definitions[index]),
        input = objectMap(inputs[index]['data']);
    if (input['action'] != step.action ||
        input['kind'] != expected['kind'] ||
        input['surfaceId'] != (expected['surfaceId'] ?? nativeSurface) ||
        input['nodeId'] != (expected['nodeId'] ?? '')) {
      throw StateError('Native input does not match bound action.');
    }
    final kind = expected['kind'];
    final actualFrame = number(input, 'observedFrame');
    final preceding = transcript.observations
        .where(
          (o) =>
              o.order < (inputs[index]['sequence']! as int) &&
              o.frame == actualFrame,
        )
        .toList();
    if (preceding.isEmpty) {
      throw StateError('Input has no preceding actual UI frame.');
    }
    if (_sentEventKinds.contains(kind) && number(input, 'sentEvents') == 0) {
      throw StateError('OS input did not send events.');
    }
    if (!_geometryKinds.contains(kind)) continue;
    var node = text(input, 'nodeId');
    if (kind == 'select-list-item') {
      final dynamic = expected['itemIdFromFact'];
      final row = dynamic == 'actionCatalogRowId'
          ? step.row
          : dynamic != null
          ? before[dynamic]
          : expected['itemId'];
      if (row is! String || row.isEmpty) {
        throw StateError('Selected native row has no independent identity.');
      }
      node += '/$row';
    }
    final ui = objectMap(preceding.last.facts['ui']);
    final widgets = mapRows(ui, 'widgets')
        .where(
          (w) => w['surfaceId'] == input['surfaceId'] && w['nodeId'] == node,
        )
        .toList();
    if (widgets.length != 1) {
      throw StateError('Input target is absent or duplicated.');
    }
    final widget = widgets.single;
    if (widget['visible'] != true ||
        widget['enabled'] != true ||
        widget['clipped'] != false) {
      throw StateError('Input target is not visibly usable.');
    }
    for (final key in ['x', 'y', 'width', 'height']) {
      if (widget[key] is! num || !(widget[key]! as num).isFinite) {
        throw StateError('Native geometry unavailable.');
      }
    }
    final x = widget['x']! as num, y = widget['y']! as num;
    final width = widget['width']! as num, height = widget['height']! as num;
    if (x < 0 ||
        y < 0 ||
        width <= 0 ||
        height <= 0 ||
        x + width > number(ui, 'width') + 1 ||
        y + height > number(ui, 'height') + 1) {
      throw StateError('Input bounds leave the actual client viewport.');
    }
  }
}
