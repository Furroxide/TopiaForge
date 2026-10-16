import 'native_annex.dart';
import 'native_transcript_protocol.dart';

/// Raised when a required protocol-v2 fact is missing or malformed. The
/// evaluator maps this to an `unavailable` scenario result naming the fact,
/// never a substitute default or zero (spec section 3).
final class NativeFactUnavailable implements Exception {
  NativeFactUnavailable(this.reason);
  final String reason;
  @override
  String toString() => reason;
}

const resourceCounters = [
  'creatorSessionCount',
  'creatorEditLeaseCount',
  'robotEditLeaseCount',
  'conversationCount',
  'interactionCount',
  'playerControlLeaseCount',
  'routingHostCount',
];
const uiCounters = [
  'hostCount',
  'ownerCanvasCount',
  'totalCanvasCount',
  'themeSubscriberCount',
  'cursorLeaseCount',
  'dismissScopeCount',
];
Map<String, Object?> ui(Map<String, Object?> facts) => objectMap(facts['ui']);
List<Map<String, Object?>> widgets(Map<String, Object?> facts) =>
    mapRows(ui(facts), 'widgets');
bool visible(Map<String, Object?> facts) => widgets(
  facts,
).any((w) => w['surfaceId'] == nativeSurface && w['visible'] == true);
Map<int, Map<String, Object?>> entities(Map<String, Object?> facts) {
  final result = <int, Map<String, Object?>>{};
  for (final item in [
    ...mapRows(facts, 'sandboxContent'),
    ...mapRows(facts, 'nativeRobots'),
  ]) {
    if (item['alive'] is! bool) {
      throw StateError('Missing actual entity liveness.');
    }
    if (item['alive'] == true) {
      final id = item['instanceId'];
      if (id is! int ||
          id == 0 ||
          item['sceneHandle'] is! int ||
          item['sceneHandle'] == 0) {
        throw StateError('Live entity lacks an actual native identity.');
      }
      transform(item);
      result[id] = item;
    }
  }
  return result;
}

List<num> transform(Map<String, Object?> value) {
  final items = value['transform'];
  if (items is! List ||
      items.length != 10 ||
      items.any((v) => v is! num || !v.isFinite)) {
    throw StateError('Missing complete native transform.');
  }
  return items.cast<num>();
}

bool sameEntities(Map<String, Object?> a, Map<String, Object?> b) {
  final before = entities(a).keys.toSet(), after = entities(b).keys.toSet();
  return before.length == after.length && before.containsAll(after);
}

bool resourcesEqual(
  Map<String, Object?> a,
  Map<String, Object?> b, {
  bool includeUi = false,
}) =>
    resourceCounters.every((k) => number(a, k) == number(b, k)) &&
    (!includeUi ||
        uiCounters.every((k) => number(ui(a), k) == number(ui(b), k)));
bool restoredBorrowed(Map<String, Object?> a, Map<String, Object?> b) {
  if (a['borrowedRobot'] == null || b['borrowedRobot'] == null) return false;
  final before = objectMap(a['borrowedRobot']),
      after = objectMap(b['borrowedRobot']);
  return before['instanceId'] == after['instanceId'] &&
      before['sceneHandle'] == after['sceneHandle'] &&
      nativeSame(before['brain'], after['brain']) &&
      List.generate(
        10,
        (i) => (transform(before)[i] - transform(after)[i]).abs(),
      ).every((v) => v < 0.0001);
}

bool hud(Map<String, Object?> facts, String marker) => widgets(facts).any(
  (w) =>
      w['surfaceId'] == 'sandbox-creator-hud' &&
      w['visible'] == true &&
      w['clipped'] == false &&
      w['text'] is String &&
      (w['text']! as String).contains(marker),
);
bool cachedUiReleased(
  Map<String, Object?> initial,
  Map<String, Object?>? opened,
  Map<String, Object?> after,
) {
  if (opened == null) return false;
  return [
        'hostCount',
        'ownerCanvasCount',
        'totalCanvasCount',
        'themeSubscriberCount',
      ].every((key) => number(ui(opened), key) == number(ui(after), key)) &&
      [
        'cursorLeaseCount',
        'dismissScopeCount',
      ].every((key) => number(ui(initial), key) == number(ui(after), key));
}

// --- Protocol-v2 facts (spec section 3). Missing/malformed -> unavailable. ---

Never _unavailable(String fact) =>
    throw NativeFactUnavailable('Native fact $fact is unavailable.');

int factInt(Map<String, Object?> facts, String key) {
  final value = facts[key];
  if (value is! int) _unavailable(key);
  return value;
}

bool factBool(Map<String, Object?> facts, String key) {
  final value = facts[key];
  if (value is! bool) _unavailable(key);
  return value;
}

num factNum(Map<String, Object?> facts, String key) {
  final value = facts[key];
  if (value is! num || !value.isFinite) _unavailable(key);
  return value;
}

Map<String, Object?> factObject(Map<String, Object?> facts, String key) {
  final value = facts[key];
  if (value is! Map<String, Object?>) _unavailable(key);
  return value;
}

/// A `controllerInstanceId` of null is a legitimate observation (no live
/// controller), but a non-integer, non-null value is malformed.
int? factNullableInt(Map<String, Object?> facts, String key) {
  final value = facts[key];
  if (value != null && value is! int) _unavailable(key);
  return value as int?;
}

List<int> factIntList(Map<String, Object?> facts, String key) {
  final value = facts[key];
  if (value is! List || value.length > 512 || value.any((v) => v is! int)) {
    _unavailable(key);
  }
  return value.cast<int>();
}

List<num> playerAim(Map<String, Object?> facts) {
  final value = facts['playerAim'];
  if (value is! List ||
      value.length != 3 ||
      value.any((v) => v is! num || !v.isFinite)) {
    _unavailable('playerAim');
  }
  return value.cast<num>();
}

Map<String, Object?> accessibility(Map<String, Object?> facts) {
  final value = factObject(facts, 'accessibility');
  if (value['highContrast'] is! bool ||
      value['reducedMotion'] is! bool ||
      value['uiScale'] is! num ||
      !(value['uiScale']! as num).isFinite ||
      value['motionIntensity'] is! num ||
      !(value['motionIntensity']! as num).isFinite) {
    _unavailable('accessibility');
  }
  return value;
}

Map<String, Object?> competingHost(Map<String, Object?> facts) {
  final value = factObject(facts, 'competingHost');
  if (value['registered'] is! bool ||
      value['canOpenCalls'] is! int ||
      value['openCalls'] is! int ||
      value['closeCalls'] is! int) {
    _unavailable('competingHost');
  }
  return value;
}

Map<String, Object?> aimToGraphProp(Map<String, Object?> facts) {
  final value = factObject(facts, 'aimToGraphProp');
  // The observer's unavailable form (no graph prop registered) is a legitimate
  // shape, reported as unavailable rather than malformed.
  if (value['available'] == false &&
      value['focused'] == false &&
      value['yawDegrees'] == null &&
      value['pitchDegrees'] == null &&
      value['distance'] == null) {
    throw NativeFactUnavailable(
      'Native fact aimToGraphProp reports available:false.',
    );
  }
  if (value['available'] is! bool ||
      value['focused'] is! bool ||
      value['yawDegrees'] is! num ||
      value['pitchDegrees'] is! num ||
      value['distance'] is! num) {
    _unavailable('aimToGraphProp');
  }
  return value;
}

Map<String, Object?> focusedInteraction(Map<String, Object?> facts) {
  final value = factObject(facts, 'focusedInteraction');
  if (value['available'] is! bool ||
      (value['entityInstanceId'] != null &&
          value['entityInstanceId'] is! int)) {
    _unavailable('focusedInteraction');
  }
  return value;
}

/// Toast rows serialised on the toast host owner (spec section 1).
List<Map<String, Object?>> toasts(Map<String, Object?> facts) {
  final value = facts['toasts'];
  if (value is! List || value.length > 512) _unavailable('toasts');
  final rows = <Map<String, Object?>>[];
  for (final row in value) {
    if (row is! Map<String, Object?> ||
        row['nodeId'] is! String ||
        row['text'] is! String ||
        row['style'] is! String ||
        row['visible'] is! bool) {
      _unavailable('toasts');
    }
    rows.add(row);
  }
  return rows;
}

bool visibleToastMatches(
  Map<String, Object?> facts,
  bool Function(Map<String, Object?>) predicate,
) => toasts(facts).any((t) => t['visible'] == true && predicate(t));

/// Catalog source rows from `ICreatorContentService.Catalog.Sources`.
List<Map<String, Object?>> catalogSources(Map<String, Object?> facts) {
  final value = facts['catalogSources'];
  if (value is! List || value.length > 512) _unavailable('catalogSources');
  final rows = <Map<String, Object?>>[];
  for (final row in value) {
    if (row is! Map<String, Object?> ||
        row['id'] is! String ||
        row['displayName'] is! String ||
        row['state'] is! String ||
        row['entryCount'] is! int) {
      _unavailable('catalogSources');
    }
    rows.add(row);
  }
  return rows;
}

/// True when [id] appears exactly once in `catalogSources` with the given
/// [state]. Missing rows are a postcondition failure, not a default.
bool catalogSourceHasState(
  Map<String, Object?> facts,
  String id,
  String state,
) {
  final rows = catalogSources(facts).where((s) => s['id'] == id).toList();
  return rows.length == 1 && rows.single['state'] == state;
}

/// Single widget matching [surfaceId]/[nodeId], or null when absent.
Map<String, Object?>? widgetAt(
  Map<String, Object?> facts,
  String surfaceId,
  String nodeId,
) {
  final rows = widgets(
    facts,
  ).where((w) => w['surfaceId'] == surfaceId && w['nodeId'] == nodeId).toList();
  return rows.length == 1 ? rows.single : null;
}

/// Widgets rendered on any Sandbox creator surface (window or HUD).
Iterable<Map<String, Object?>> sandboxWidgets(Map<String, Object?> facts) =>
    widgets(facts).where(
      (w) =>
          w['surfaceId'] is String &&
          (w['surfaceId']! as String).startsWith('sandbox-creator'),
    );
