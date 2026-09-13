import 'native_annex.dart';
import 'native_transcript_protocol.dart';

final class NativeOracleStep {
  const NativeOracleStep(this.action, {this.row = '', this.label = ''});
  final String action, row, label;
}

const _steps = <String, List<String>>{
  'routing': ['open', 'hide', 'reopen', 'end-session'],
  'catalog-editing': ['open', r'$catalog', 'end-session'],
  'borrowed-robot': [
    'open',
    'select-borrowed',
    'edit-transform',
    'edit-personality',
    'edit-brain',
    'end-session',
  ],
  'source-unload': [
    'open',
    'spawn-prop',
    'duplicate',
    'unregister-source',
    'end-session',
  ],
  'hide-reopen': [
    'open',
    'spawn-prop',
    'hide',
    'move-player',
    'reopen',
    'end-session',
  ],
  'persistence-refusal': ['observe-refusal'],
  'graph-rollback': [
    'open',
    'spawn-prop',
    'run-graph',
    'stop-graph',
    'end-session',
  ],
  'lifecycle-routes': ['open', 'spawn-prop', 'stop-world-session'],
  'ten-cycles': [
    'open',
    'spawn-prop',
    'select-borrowed',
    'edit-transform',
    'edit-personality',
    'edit-brain',
    'hide',
    'move-player',
    'reopen',
    'run-graph',
    'stop-graph',
    'end-session',
  ],
};
List<NativeOracleStep> nativeExpectedSteps(
  String id,
  Map<String, Object?> driver,
  Map<String, Object?> baseline,
) {
  final manifest = mapRows(
    driver,
    'scenarios',
  ).singleWhere((s) => s['id'] == id);
  if (!nativeSame(manifest['steps'], _steps[id])) {
    throw StateError('Driver omits/reorders required native actions.');
  }
  final result = <NativeOracleStep>[];
  for (final action in _steps[id]!) {
    if (action != r'$catalog') {
      result.add(NativeOracleStep(action));
      continue;
    }
    final entries = [
      ...mapRows(baseline, 'catalog'),
      ...mapRows(baseline, 'robotCatalog'),
    ];
    if (entries.isEmpty || entries.length > 256) {
      throw StateError('Native catalog inventory missing or oversized.');
    }
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
      add('remove');
      add('remove');
    }
  }
  return result;
}

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
    if (['click', 'replace-text', 'select-list-item', 'key'].contains(kind) &&
        number(input, 'sentEvents') == 0) {
      throw StateError('OS input did not send events.');
    }
    if (!['click', 'replace-text', 'select-list-item'].contains(kind)) continue;
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
