import 'native_annex.dart';
import 'native_transcript_protocol.dart';

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
bool visible(Map<String, Object?> facts) => mapRows(
  ui(facts),
  'widgets',
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

bool checkNativePostcondition(
  String action,
  int cycle,
  Map<String, Object?> initial,
  Map<String, Object?> before,
  Map<String, Object?> after, {
  Map<String, Object?>? graphBaseline,
  required bool borrowedSelected,
  Map<String, Object?>? openedUi,
}) {
  if ((after['cleanupErrors'] as List?)?.isNotEmpty != false) return false;
  switch (action) {
    case 'open':
    case 'reopen':
      return visible(after) &&
          after['activeHostId'] == nativeOwner &&
          after['worldSessionId'] == initial['worldSessionId'] &&
          number(after, 'creatorSessionCount') ==
              number(initial, 'creatorSessionCount') + 1;
    case 'hide':
      return !visible(after) &&
          hud(after, "SESSION ACTIVE") &&
          sameEntities(before, after) &&
          after['worldSessionId'] == initial['worldSessionId'] &&
          number(ui(after), 'cursorLeaseCount') ==
              number(ui(initial), 'cursorLeaseCount');
    case 'move-player':
      final oldPosition = before['playerPosition'],
          newPosition = after['playerPosition'];
      if (oldPosition is! List ||
          newPosition is! List ||
          oldPosition.length != 3 ||
          newPosition.length != 3 ||
          [
            ...oldPosition,
            ...newPosition,
          ].any((v) => v is! num || !v.isFinite)) {
        throw StateError('Actual player position is unavailable.');
      }
      return !visible(after) &&
          after['worldSessionId'] == initial['worldSessionId'] &&
          sameEntities(before, after) &&
          resourcesEqual(before, after) &&
          ((oldPosition[0] - newPosition[0]).abs() > 0.005 ||
              (oldPosition[2] - newPosition[2]).abs() > 0.005);
    case 'spawn-prop':
    case 'spawn-catalog':
    case 'duplicate':
      final old = entities(before), now = entities(after);
      return now.length == old.length + 1 &&
          now.keys.toSet().containsAll(old.keys);
    case 'remove':
      final old = entities(before), now = entities(after);
      return old.length == now.length + 1 &&
          old.keys.toSet().containsAll(now.keys);
    case 'select-borrowed':
      final id = text(after, 'borrowedRosterId');
      return id.isNotEmpty &&
          mapRows(ui(after), 'widgets').any(
            (w) => w['nodeId'] == 'roster-list/$id' && w['focused'] == true,
          );
    case 'edit-transform':
    case 'edit-rotation':
    case 'edit-scale':
      final old = borrowedSelected
          ? [objectMap(before['borrowedRobot'])]
          : entities(before).values;
      final now = borrowedSelected
          ? [objectMap(after['borrowedRobot'])]
          : entities(after).values;
      final offset = action == 'edit-transform'
          ? 0
          : action == 'edit-rotation'
          ? 3
          : 7;
      final count = action == 'edit-rotation' ? 4 : 3;
      return old.any(
        (a) => now.any(
          (b) =>
              a['instanceId'] == b['instanceId'] &&
              List.generate(
                count,
                (i) =>
                    (transform(a)[i + offset] - transform(b)[i + offset]).abs(),
              ).any((v) => v > 0.001),
        ),
      );
    case 'edit-personality':
      final a = objectMap(objectMap(before['borrowedRobot'])['brain']);
      final b = objectMap(objectMap(after['borrowedRobot'])['brain']);
      return b['hackedPersonalityId'] is int &&
          b['hackedPersonalityId'] != 0 &&
          b['hackedPersonalityFingerprint'] is String &&
          b['hackedPersonalityFingerprint'] != 'unavailable' &&
          b['hackedPersonalityFingerprint'] !=
              a['hackedPersonalityFingerprint'];
    case 'edit-brain':
      final a = objectMap(objectMap(before['borrowedRobot'])['brain']);
      final b = objectMap(objectMap(after['borrowedRobot'])['brain']);
      return [
        'state',
        'initialState',
        'llmDisabled',
        'behaviorTrees',
      ].any((k) => a[k] != null && b[k] != null && !nativeSame(a[k], b[k]));
    case 'run-graph':
      return entities(after).length >= entities(before).length + 2 &&
          number(after, 'graphPlayingAudioCount') > 0 &&
          number(after, 'interactionCount') >
              number(before, 'interactionCount') &&
          number(after, 'conversationCount') >
              number(before, 'conversationCount');
    case 'stop-graph':
      return graphBaseline != null &&
          sameEntities(graphBaseline, after) &&
          resourcesEqual(graphBaseline, after) &&
          number(after, 'graphPlayingAudioCount') ==
              number(graphBaseline, 'graphPlayingAudioCount');
    case 'unregister-source':
      final catalog = after['catalogIds'];
      return catalog is List &&
          catalog.every(
            (id) =>
                id is String &&
                !id.startsWith('dev.topiaforge.sandbox-acceptance:'),
          ) &&
          mapRows(after, 'nativeProps').isEmpty &&
          number(after, 'ownedObjectCount') == 0;
    case 'end-session':
      return !visible(after) &&
          sameEntities(initial, after) &&
          resourcesEqual(initial, after) &&
          cachedUiReleased(initial, openedUi, after) &&
          hud(after, "NO ACTIVE CREATOR SESSION") &&
          restoredBorrowed(initial, after);
    case 'stop-world-session':
      final transition = cycle == 2
          ? after['sessionPhase'] == 'Running' &&
                after['targetId'] == nativeTarget &&
                after['worldSessionId'] != initial['worldSessionId']
          : after['sessionPhase'] == 'Idle' && after['worldSessionId'] == '';
      return transition &&
          !visible(after) &&
          number(after, 'ownedObjectCount') == 0 &&
          mapRows(after, 'nativeProps').isEmpty &&
          number(after, 'graphPlayingAudioCount') == 0 &&
          number(after, 'creatorSessionCount') ==
              number(initial, 'creatorSessionCount');
    case 'observe-refusal':
      return after['mutationSafetyState'] == 'Unavailable' &&
          after['persistenceIsolationAvailable'] == false;
    default:
      throw StateError('Unknown semantic native action.');
  }
}

bool hud(Map<String, Object?> facts, String marker) =>
    mapRows(ui(facts), 'widgets').any(
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
