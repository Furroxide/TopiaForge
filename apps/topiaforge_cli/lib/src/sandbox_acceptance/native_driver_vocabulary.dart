/// Independent mirror of the broker's closed protocol-v2 actuation vocabulary
/// (`tools/TopiaForge.Acceptance.Windows/DriverVocabulary.cs`). Every driver
/// atom must satisfy exactly the per-kind closed field set, value vocabularies
/// and bounds the broker enforces, so a manifest the broker refuses can never
/// pass verification. `sandbox_native_vocabulary_test.dart` reads the C#
/// source, so changing either side alone fails a test.
library;

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
const nativeDriverAimFacts = {'aimToGraphProp'};
const nativeDriverDynamicTexts = {'actionCatalogDisplayName'};
const nativeDriverDynamicRows = {
  'borrowedRosterId',
  'projectId',
  'actionCatalogRowId',
};

/// The complete action inventory. A manifest's `actions` map declares exactly
/// these names, each with 1 to [nativeDriverMaximumAtoms] atoms.
const nativeDriverActions = {
  'open',
  'reopen',
  'hide-f5',
  'hide-close',
  'duplicate-toggle',
  'focus-next',
  'move-player',
  'move-while-visible',
  'camera-hidden',
  'text-focus',
  'spawn-prop',
  'spawn-character',
  'spawn-catalog',
  'search-nonmatching',
  'filter-robots',
  'filter-all',
  'duplicate',
  'undo',
  'remove',
  'edit-transform',
  'edit-rotation',
  'edit-scale',
  'select-borrowed',
  'edit-personality',
  'edit-brain',
  'run-graph',
  'stop-graph',
  'stop-before-start',
  'aim-graph-prop',
  'interact',
  'end-session',
  'unregister-source',
  'stop-world-session',
  'toggle-during-transition',
  'toggle-in-menu',
  'observe-refusal',
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
const nativeDriverMaximumAtoms = 12;

/// One atom kind's closed field set, mirroring a `ValidateStep` case. Every
/// atom also carries `kind` and may carry `surfaceId`; when [oneOf] is
/// nonempty, exactly one of its fields must appear.
final class NativeDriverAtomSchema {
  const NativeDriverAtomSchema(
    this.required, {
    this.oneOf = const {},
    this.optional = const {},
  });
  final Set<String> required, oneOf, optional;
}

const nativeDriverAtomSchemas = <String, NativeDriverAtomSchema>{
  'key': NativeDriverAtomSchema({'key'}),
  'click': NativeDriverAtomSchema({'nodeId'}),
  'replace-text': NativeDriverAtomSchema(
    {'nodeId'},
    oneOf: {'text', 'textFromFact'},
  ),
  'select-list-item': NativeDriverAtomSchema(
    {'nodeId'},
    oneOf: {'itemId', 'itemIdFromFact'},
  ),
  'barrier': NativeDriverAtomSchema({'minimumFrames'}),
  'request': NativeDriverAtomSchema({'operation'}),
  'capture': NativeDriverAtomSchema(<String>{}),
  'scroll-into-view': NativeDriverAtomSchema(
    {'nodeId', 'containerId'},
    optional: {'itemIdFromFact'},
  ),
  'mouse-move': NativeDriverAtomSchema({'dx', 'dy'}),
  'key-hold': NativeDriverAtomSchema({'key', 'milliseconds'}),
  'aim': NativeDriverAtomSchema({'fact', 'maxIterations', 'gain'}),
};

/// Closed value set per field name; a field means the same in every kind.
const nativeDriverFieldVocabularies = <String, Set<String>>{
  'surfaceId': nativeDriverSurfaces,
  'nodeId': nativeDriverNodes,
  'key': nativeDriverKeys,
  'operation': nativeDriverRequestOperations,
  'fact': nativeDriverAimFacts,
  'textFromFact': nativeDriverDynamicTexts,
  'itemIdFromFact': nativeDriverDynamicRows,
  'containerId': nativeScrollContainers,
};

/// Inclusive bounds of JSON integer fields; a fraction or exponent form is not
/// an integer, as in the broker's digit-only integer grammar.
const nativeDriverIntegerFields = <String, (int, int)>{
  'minimumFrames': (2, 10),
  'milliseconds': (50, 1000),
  'maxIterations': (1, 40),
  'gain': (1, 20),
  'dx': (-400, 400),
  'dy': (-400, 400),
};

/// Literal text fields as inclusive UTF-16 length bounds. Control characters
/// (C0, DEL and C1, as .NET `char.IsControl`) are refused; `text: ""` clears.
const nativeDriverTextFields = <String, (int, int)>{
  'text': (0, 1024),
  'itemId': (1, 512),
};

/// Validates one driver atom exactly as `DriverVocabulary.ValidateStep` does:
/// a declared kind, its closed field set, and a declared value for every field.
void validateNativeDriverAtom(Map<String, Object?> atom) {
  final kind = atom['kind'];
  final schema = nativeDriverAtomSchemas[kind];
  if (schema == null) throw StateError('Unknown native driver atom kind.');
  final chosen = schema.oneOf.where(atom.containsKey).toSet();
  if (schema.oneOf.isNotEmpty && chosen.length != 1) {
    throw StateError(
      'Native driver $kind atom must carry exactly one of '
      '${schema.oneOf.join(', ')}.',
    );
  }
  final required = {...schema.required, ...chosen};
  final allowed = {'kind', 'surfaceId', ...required, ...schema.optional};
  if (!atom.keys.every(allowed.contains)) {
    throw StateError('Native driver $kind atom carries an undeclared field.');
  }
  for (final name in required) {
    if (!atom.containsKey(name)) {
      throw StateError('Native driver $kind atom lacks $name.');
    }
  }
  for (final entry in atom.entries) {
    if (entry.key != 'kind' && !_validField(entry.key, entry.value)) {
      throw StateError('Native driver $kind atom ${entry.key} is invalid.');
    }
  }
  if (kind == 'scroll-into-view' && atom['containerId'] == atom['nodeId']) {
    throw StateError('A native scroll container cannot be its own target.');
  }
}

bool _validField(String name, Object? value) {
  final vocabulary = nativeDriverFieldVocabularies[name];
  if (vocabulary != null) return vocabulary.contains(value);
  final integer = nativeDriverIntegerFields[name];
  if (integer != null) {
    return value is int && value >= integer.$1 && value <= integer.$2;
  }
  final text = nativeDriverTextFields[name];
  return text != null &&
      value is String &&
      value.length >= text.$1 &&
      value.length <= text.$2 &&
      !value.codeUnits.any(_isControl);
}

bool _isControl(int unit) => unit < 0x20 || (unit >= 0x7f && unit <= 0x9f);
