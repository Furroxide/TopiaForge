import 'native_annex.dart';
import 'native_contrast.dart';
import 'native_transcript_facts.dart';
import 'native_transcript_protocol.dart';

/// Independently recomputed context for a single step postcondition. Every
/// value comes from bound transcript facts; driver claims are never consulted.
final class NativePostcondition {
  const NativePostcondition({
    required this.scenario,
    required this.action,
    required this.cycle,
    required this.initial,
    required this.before,
    this.graphBaseline,
    this.borrowedSelected = false,
    this.openedUi,
    this.externalWrite,
  });
  final String scenario, action;
  final int cycle;
  final Map<String, Object?> initial, before;
  final Map<String, Object?>? graphBaseline, openedUi, externalWrite;
  final bool borrowedSelected;
}

const _tolerance = 0.001;
const _looseTolerance = 0.01;

/// Computes one step postcondition from the actual before/after facts. Returns
/// false when the observed state does not satisfy the recipe (a hard failure);
/// throws [NativeFactUnavailable] when a required v2 fact is absent (mapped to
/// an `unavailable` scenario). Preserves every v1 postcondition the spec keeps.
bool checkNativePostcondition(
  NativePostcondition step,
  Map<String, Object?> after,
) {
  if ((after['cleanupErrors'] as List?)?.isNotEmpty != false) return false;
  final initial = step.initial, before = step.before;
  switch (step.action) {
    case 'open':
    case 'reopen':
      final base =
          visible(after) &&
          after['activeHostId'] == nativeOwner &&
          after['worldSessionId'] == initial['worldSessionId'] &&
          number(after, 'creatorSessionCount') ==
              number(initial, 'creatorSessionCount') + 1;
      if (!base) return false;
      // A reopen while a graph is running must show the graph still alive.
      if (step.action == 'reopen' && step.graphBaseline != null) {
        return factBool(after, 'projectRunning') &&
            number(after, 'graphPlayingAudioCount') > 0;
      }
      return true;
    case 'hide':
    case 'hide-f5':
    case 'hide-close':
      return !visible(after) &&
          hud(after, 'SESSION ACTIVE') &&
          sameEntities(before, after) &&
          after['worldSessionId'] == initial['worldSessionId'] &&
          number(ui(after), 'cursorLeaseCount') ==
              number(ui(initial), 'cursorLeaseCount');
    case 'duplicate-toggle':
      return visible(after) &&
          after['worldSessionId'] == initial['worldSessionId'] &&
          number(after, 'creatorSessionCount') ==
              number(initial, 'creatorSessionCount') + 1;
    case 'focus-next':
      final wasFocused = _focusedNode(before);
      final nowFocused = _focusedNode(after);
      return nowFocused != null && nowFocused != wasFocused;
    case 'move-player':
      // Compare the session to the previous step, not to begin, so a move that
      // follows a lifecycle restart (a fresh session) still verifies.
      return !visible(after) &&
          after['worldSessionId'] == before['worldSessionId'] &&
          sameEntities(before, after) &&
          resourcesEqual(before, after) &&
          _positionMoved(before, after, moved: true);
    case 'move-while-visible':
      return visible(after) &&
          after['worldSessionId'] == initial['worldSessionId'] &&
          sameEntities(before, after) &&
          _positionMoved(before, after, moved: false);
    case 'camera-hidden':
      final oldAim = playerAim(before), newAim = playerAim(after);
      return !visible(after) &&
          List.generate(
            3,
            (i) => (oldAim[i] - newAim[i]).abs(),
          ).any((v) => v > _tolerance);
    case 'text-focus':
      final field = widgetAt(after, nativeSurface, 'catalog-search');
      return field != null &&
          field['value'] == 'W' &&
          _positionMoved(before, after, moved: false);
    case 'spawn-prop':
    case 'spawn-catalog':
    case 'spawn-character':
    case 'duplicate':
      final old = entities(before), now = entities(after);
      return now.length == old.length + 1 &&
          now.keys.toSet().containsAll(old.keys);
    case 'spawn-control-robot':
      final id = factInt(after, 'controlRobotInstanceId');
      final old = entities(before), now = entities(after);
      return id != 0 &&
          now.containsKey(id) &&
          !old.containsKey(id) &&
          now.length == old.length + 1;
    case 'despawn-control-robot':
      return factInt(after, 'controlRobotInstanceId') == 0;
    case 'remove':
      final old = entities(before), now = entities(after);
      return old.length == now.length + 1 &&
          old.keys.toSet().containsAll(now.keys);
    case 'undo':
      final old = entities(before), now = entities(after);
      return now.length == old.length - 1 &&
          old.keys.toSet().containsAll(now.keys) &&
          factInt(after, 'undoDepth') == factInt(before, 'undoDepth') - 1;
    case 'select-borrowed':
      final id = text(after, 'borrowedRosterId');
      return id.isNotEmpty &&
          widgets(after).any(
            (w) => w['nodeId'] == 'roster-list/$id' && w['focused'] == true,
          );
    case 'edit-transform':
      return _transformDelta(step, after, [0, 1, 0], 0);
    case 'edit-rotation':
      return _transformAbsolute(step, after, [0, 0.7071068, 0, 0.7071068], 3);
    case 'edit-scale':
      return _transformAbsolute(step, after, [1.25, 1.25, 1.25], 7);
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
    case 'external-write':
      final a = objectMap(before['borrowedRobot']);
      final b = objectMap(after['borrowedRobot']);
      return a['instanceId'] == b['instanceId'] &&
          (transform(b)[0] - transform(a)[0] - 2).abs() < _looseTolerance;
    case 'destroy-borrowed':
      return after['borrowedRobot'] == null &&
          factBool(after, 'borrowedRobotDestroyed');
    case 'run-graph':
      return entities(after).length >= entities(before).length + 2 &&
          number(after, 'graphPlayingAudioCount') > 0 &&
          number(after, 'interactionCount') >
              number(before, 'interactionCount') &&
          number(after, 'conversationCount') >
              number(before, 'conversationCount');
    case 'stop-graph':
      return step.graphBaseline != null &&
          sameEntities(step.graphBaseline!, after) &&
          resourcesEqual(step.graphBaseline!, after) &&
          number(after, 'graphPlayingAudioCount') ==
              number(step.graphBaseline!, 'graphPlayingAudioCount');
    case 'stop-before-start':
      // Observe-only: with no run in progress the Stop control is disabled and
      // the scene is unchanged from the previous step. No click is required.
      final stop = widgetAt(after, nativeSurface, 'stop-project');
      return stop != null &&
          stop['enabled'] == false &&
          !factBool(after, 'projectRunning') &&
          sameEntities(before, after) &&
          resourcesEqual(before, after);
    case 'control-cue':
      return factBool(after, 'controlCuePlaying');
    case 'stop-control-cue':
      return !factBool(after, 'controlCuePlaying');
    case 'aim-graph-prop':
      final aim = aimToGraphProp(after);
      return aim['available'] == true && aim['focused'] == true;
    case 'interact':
      return number(after, 'interactionCount') ==
              number(before, 'interactionCount') &&
          visibleToastMatches(
            after,
            (t) => (t['text']! as String).contains(
              'Sandbox acceptance interaction',
            ),
          ) &&
          !toasts(
            after,
          ).any((t) => (t['text']! as String).contains('WRONG BRANCH'));
    case 'register-competing-host':
      final host = competingHost(after);
      return host['registered'] == true && host['openCalls'] == 0;
    case 'unregister-competing-host':
      return competingHost(after)['registered'] == false;
    case 'accessibility-high-contrast':
      return _highContrast(after);
    case 'accessibility-scale-150':
      return _scale150(step, after);
    case 'accessibility-reduced-motion':
      final access = accessibility(after);
      return access['reducedMotion'] == true &&
          (access['motionIntensity']! as num) == 0;
    case 'accessibility-reset':
      final access = accessibility(after);
      return access['highContrast'] == false &&
          access['reducedMotion'] == false &&
          ((access['uiScale']! as num) - 1.0).abs() < _tolerance;
    case 'search-nonmatching':
      final spawn = widgetAt(after, nativeSurface, 'spawn-selected');
      return spawn != null &&
          spawn['enabled'] == false &&
          !_hasCatalogRows(after);
    case 'filter-robots':
      final kind = widgetAt(after, nativeSurface, 'catalog-kind');
      return kind != null &&
          kind['value'] == 'Robots' &&
          _catalogRows(after).every((id) => id.startsWith('robotkit:'));
    case 'filter-all':
      final kind = widgetAt(after, nativeSurface, 'catalog-kind');
      return kind != null && kind['value'] == 'All content';
    case 'unregister-source':
      return _unregisterSource(step, after);
    case 'end-session':
      return _endSession(step, after);
    case 'stop-world-session':
      return _stopWorldSession(step, after);
    case 'toggle-during-transition':
      // The restart completed underneath the F5: Running again with a new
      // session and controller identity, while the toggle opened no creator
      // host or session.
      final controller = factNullableInt(after, 'controllerInstanceId');
      return after['sessionPhase'] == 'Running' &&
          text(after, 'worldSessionId').isNotEmpty &&
          after['worldSessionId'] != initial['worldSessionId'] &&
          controller != null &&
          controller != factNullableInt(initial, 'controllerInstanceId') &&
          after['activeHostId'] == '' &&
          number(after, 'creatorSessionCount') ==
              number(initial, 'creatorSessionCount');
    case 'toggle-in-menu':
      return after['activeHostId'] == '' &&
          after['sessionPhase'] == 'Idle' &&
          number(after, 'creatorSessionCount') ==
              number(initial, 'creatorSessionCount');
    case 'observe-refusal':
      return after['mutationSafetyState'] == 'Unavailable' &&
          after['persistenceIsolationAvailable'] == false &&
          sandboxWidgets(after).any(
            (w) =>
                w['visible'] == true &&
                w['text'] is String &&
                (w['text']! as String).contains('Sandbox isolation active.'),
          );
    default:
      throw StateError('Unknown semantic native action.');
  }
}

String? _focusedNode(Map<String, Object?> facts) {
  final focused = sandboxWidgets(
    facts,
  ).where((w) => w['focused'] == true).toList();
  return focused.length == 1 ? focused.single['nodeId'] as String? : null;
}

bool _positionMoved(
  Map<String, Object?> before,
  Map<String, Object?> after, {
  required bool moved,
}) {
  final oldPosition = before['playerPosition'],
      newPosition = after['playerPosition'];
  if (oldPosition is! List ||
      newPosition is! List ||
      oldPosition.length != 3 ||
      newPosition.length != 3 ||
      [...oldPosition, ...newPosition].any((v) => v is! num || !v.isFinite)) {
    throw StateError('Actual player position is unavailable.');
  }
  final delta =
      (oldPosition[0] - newPosition[0]).abs() > 0.005 ||
      (oldPosition[2] - newPosition[2]).abs() > 0.005;
  return moved ? delta : !delta;
}

List<Map<String, Object?>> _selected(
  NativePostcondition step,
  Map<String, Object?> facts,
) {
  if (step.borrowedSelected) return [objectMap(facts['borrowedRobot'])];
  return entities(facts).values.toList();
}

bool _transformDelta(
  NativePostcondition step,
  Map<String, Object?> after,
  List<num> delta,
  int offset,
) {
  final old = _selected(step, step.before);
  final now = _selected(step, after);
  return old.any(
    (a) => now.any(
      (b) =>
          a['instanceId'] == b['instanceId'] &&
          List.generate(
            delta.length,
            (i) =>
                (transform(b)[i + offset] - transform(a)[i + offset] - delta[i])
                    .abs(),
          ).every((v) => v < _tolerance),
    ),
  );
}

bool _transformAbsolute(
  NativePostcondition step,
  Map<String, Object?> after,
  List<num> expected,
  int offset,
) {
  final old = _selected(step, step.before);
  final now = _selected(step, after);
  return old.any(
    (a) => now.any(
      (b) =>
          a['instanceId'] == b['instanceId'] &&
          List.generate(
            expected.length,
            (i) => (transform(b)[i + offset] - expected[i]).abs(),
          ).every((v) => v < _tolerance),
    ),
  );
}

bool _highContrast(Map<String, Object?> after) {
  if (accessibility(after)['highContrast'] != true) return false;
  for (final widget in sandboxWidgets(after)) {
    if (widget['highContrast'] != true) return false;
    if (nativeHasBothColours(widget['foreground'], widget['background']) &&
        !nativeContrastMeetsAA(widget['foreground'], widget['background'])) {
      return false;
    }
  }
  return true;
}

bool _scale150(NativePostcondition step, Map<String, Object?> after) {
  if (((accessibility(after)['uiScale']! as num) - 1.5).abs() >= _tolerance) {
    return false;
  }
  final opened = step.openedUi;
  if (opened == null) return false;
  final base = widgetAt(opened, nativeSurface, 'hide-workbench');
  final scaled = widgetAt(after, nativeSurface, 'hide-workbench');
  if (base == null ||
      scaled == null ||
      base['height'] is! num ||
      scaled['height'] is! num) {
    return false;
  }
  return (scaled['height']! as num) >= (base['height']! as num) * 1.4;
}

bool _hasCatalogRows(Map<String, Object?> facts) => widgets(facts).any(
  (w) =>
      w['surfaceId'] == nativeSurface &&
      w['nodeId'] is String &&
      (w['nodeId']! as String).startsWith('catalog-list/'),
);

List<String> _catalogRows(Map<String, Object?> facts) => [
  for (final w in widgets(facts))
    if (w['surfaceId'] == nativeSurface &&
        w['nodeId'] is String &&
        (w['nodeId']! as String).startsWith('catalog-list/'))
      (w['nodeId']! as String).substring('catalog-list/'.length),
];

bool _unregisterSource(NativePostcondition step, Map<String, Object?> after) {
  final catalog = after['catalogIds'];
  final base =
      catalog is List &&
      catalog.every(
        (id) =>
            id is String &&
            !id.startsWith('dev.topiaforge.sandbox-acceptance:'),
      );
  if (!base) return false;
  // The unavailable native vehicle source stays visible as a product limit.
  if (!catalogSourceHasState(after, 'robotopia.vehicles', 'Unavailable')) {
    return false;
  }
  if (step.scenario == 'source-unload' && step.cycle == 2) {
    // Disposal while a graph runs must leave the run stopped cleanly.
    return !factBool(after, 'projectRunning');
  }
  return mapRows(after, 'nativeProps').isEmpty &&
      number(after, 'ownedObjectCount') == 0 &&
      factInt(after, 'controlRobotInstanceId') != 0;
}

bool _endSession(NativePostcondition step, Map<String, Object?> after) {
  final initial = step.initial;
  final visibleAndHud =
      !visible(after) && hud(after, 'NO ACTIVE CREATOR SESSION');
  if (!visibleAndHud) return false;
  if (step.scenario == 'source-unload' && step.cycle == 1) {
    // The unrelated fixture control robot outlives the source teardown.
    final control = factInt(after, 'controlRobotInstanceId');
    return control != 0 &&
        entities(after).keys.toSet().difference({control}).isEmpty &&
        resourcesEqual(initial, after) &&
        cachedUiReleased(initial, step.openedUi, after);
  }
  if (step.scenario == 'borrowed-robot' && step.cycle == 2) {
    // The externally moved robot keeps its external position; personality is
    // restored, and a restoration warning toast is surfaced.
    final external = step.externalWrite;
    final after0 = after['borrowedRobot'];
    if (external == null || after0 is! Map<String, Object?>) return false;
    final expected = objectMap(external['borrowedRobot']);
    final positionsMatch = List.generate(
      10,
      (i) => (transform(after0)[i] - transform(expected)[i]).abs(),
    ).every((v) => v < _looseTolerance);
    final movedFromOriginal =
        (transform(after0)[0] -
                transform(objectMap(initial['borrowedRobot']))[0])
            .abs() >
        _looseTolerance;
    return positionsMatch &&
        movedFromOriginal &&
        nativeSame(
          after0['brain'],
          objectMap(initial['borrowedRobot'])['brain'],
        ) &&
        visibleToastMatches(
          after,
          (t) =>
              t['style'] == 'Warning' &&
              ((t['text']! as String).contains('outside Creator Tools') ||
                  (t['text']! as String).contains('restoration warnings')),
        );
  }
  if (step.scenario == 'borrowed-robot' && step.cycle == 3) {
    // The destroyed robot is not resurrected; its edit lease is released.
    return after['borrowedRobot'] == null &&
        number(after, 'robotEditLeaseCount') ==
            number(initial, 'robotEditLeaseCount');
  }
  // Borrowed restoration is only required when a borrowed robot was selected;
  // scenarios without one (routing, catalog-editing, graph-rollback) restore
  // only their owned entities, resources and cached UI.
  if (step.borrowedSelected && !restoredBorrowed(initial, after)) return false;
  return sameEntities(initial, after) &&
      resourcesEqual(initial, after) &&
      cachedUiReleased(initial, step.openedUi, after);
}

bool _stopWorldSession(NativePostcondition step, Map<String, Object?> after) {
  final initial = step.initial;
  if (step.cycle == 2) {
    // RestartAsync: the observer advances as soon as the original session has
    // left Running (phase or identity) with the workbench gone; the fresh
    // session is verified by the following toggle-during-transition step.
    return (after['sessionPhase'] != 'Running' ||
            after['worldSessionId'] != initial['worldSessionId']) &&
        !visible(after);
  }
  // StopAsync / ReturnToMainMenuAsync: Idle with no controller.
  final controller = factNullableInt(after, 'controllerInstanceId');
  final transition =
      after['sessionPhase'] == 'Idle' &&
      after['worldSessionId'] == '' &&
      controller == null;
  return transition &&
      !visible(after) &&
      number(after, 'ownedObjectCount') == 0 &&
      mapRows(after, 'nativeProps').isEmpty &&
      number(after, 'graphPlayingAudioCount') == 0 &&
      number(after, 'creatorSessionCount') ==
          number(initial, 'creatorSessionCount');
}
