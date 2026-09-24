import 'dart:convert';

import 'package:topiaforge/src/sandbox_acceptance/native_expected_catalog.dart';
import 'package:topiaforge/src/sandbox_acceptance/sandbox_game_build.dart';
import 'sandbox_native_scene.dart';

/// Mutates [scene] so its next snapshot satisfies exactly the postcondition for
/// [action] in the given scenario/cycle, mirroring what a real broker would
/// observe. Test-only; never actual execution.
void applyNativeStep(
  SandboxScene scene,
  String scenario,
  int cycle,
  dynamic step,
) {
  final action = step.action as String;
  switch (action) {
    case 'open':
    case 'reopen':
      scene.windowVisible = true;
      scene.activeHostId = nativeSceneOwner;
      scene.creatorSessionCount = 1;
      scene.hudText = 'SESSION ACTIVE';
    case 'hide-f5':
    case 'hide-close':
      scene.windowVisible = false;
    case 'duplicate-toggle':
      scene.windowVisible = true;
    case 'focus-next':
      scene.focusedNode = 'catalog-search';
    case 'move-player':
      scene.windowVisible = false;
      scene.playerPosition = [scene.playerPosition[0] + 1, 0, 0];
    case 'move-while-visible':
      scene.windowVisible = true;
    case 'camera-hidden':
      scene.windowVisible = false;
      scene.playerAim = [scene.playerAim[0] + 0.3, 0, 1];
    case 'text-focus':
      scene.windowVisible = true;
      scene.catalogSearchValue = 'W';
    case 'spawn-prop':
    case 'spawn-catalog':
      scene.selectedId = scene.addEntity(scene.sandboxContent);
      scene.undoDepth++;
    case 'spawn-character':
      scene.selectedId = scene.addEntity(scene.sandboxContent);
    case 'duplicate':
      scene.selectedId = scene.addEntity(scene.sandboxContent);
      scene.undoDepth++;
    case 'undo':
      scene.sandboxContent.removeLast();
      scene.undoDepth--;
    case 'remove':
      scene.sandboxContent.removeLast();
    case 'spawn-control-robot':
      scene.controlRobotInstanceId = scene.addEntity(scene.nativeRobots);
    case 'despawn-control-robot':
      scene.nativeRobots.removeWhere(
        (e) => e['instanceId'] == scene.controlRobotInstanceId,
      );
      scene.controlRobotInstanceId = 0;
    case 'select-borrowed':
      scene.borrowedSelected = true;
      scene.rosterFocused = true;
      scene.focusedNode = 'roster-list/${scene.borrowedRosterId}';
      scene.robotEditLeaseCount = 1;
    case 'edit-transform':
      _editTransform(scene);
    case 'edit-rotation':
      _setTransform(scene, 3, [0, 0.7071068, 0, 0.7071068]);
    case 'edit-scale':
      _setTransform(scene, 7, [1.25, 1.25, 1.25]);
    case 'edit-personality':
      scene.editBorrowedPersonality();
    case 'edit-brain':
      (scene.borrowedRobot!['brain']! as Map)['state'] = 'Dormant';
    case 'external-write':
      final t = (scene.borrowedRobot!['transform']! as List).cast<num>();
      t[0] = t[0] + 2;
    case 'destroy-borrowed':
      scene.borrowedRobot = null;
      scene.borrowedRobotDestroyed = true;
      scene.robotEditLeaseCount = 0;
    case 'run-graph':
      _runGraph(scene);
    case 'stop-graph':
      _stopGraph(scene);
    case 'stop-before-start':
      break;
    case 'control-cue':
      scene.controlCuePlaying = true;
    case 'stop-control-cue':
      scene.controlCuePlaying = false;
    case 'aim-graph-prop':
      scene.aimFocused = true;
    case 'interact':
      scene.toasts = [
        ...scene.toasts,
        {
          'nodeId': 'toast-1',
          'text': 'Sandbox acceptance interaction fired.',
          'style': 'Info',
          'visible': true,
        },
      ];
    case 'register-competing-host':
      scene.competingRegistered = true;
    case 'unregister-competing-host':
      scene.competingRegistered = false;
    case 'accessibility-high-contrast':
      scene.highContrast = true;
    case 'accessibility-scale-150':
      scene.uiScale = 1.5;
      scene.hideWorkbenchHeight = 45;
    case 'accessibility-reduced-motion':
      scene.reducedMotion = true;
      scene.motionIntensity = 0;
    case 'accessibility-reset':
      scene.highContrast = false;
      scene.uiScale = 1.0;
      scene.reducedMotion = false;
      scene.motionIntensity = 1.0;
      scene.hideWorkbenchHeight = 30;
    case 'search-nonmatching':
      scene.catalogListRows = [];
      scene.spawnEnabled = false;
      scene.catalogSearchValue = 'zz-no-such-acceptance-entry';
    case 'filter-robots':
      scene.catalogListRows = scene.robotCatalog
          .map((e) => e['rowId']! as String)
          .toList();
      scene.catalogKindValue = 'Robots';
      scene.catalogSearchValue = '';
      scene.spawnEnabled = true;
    case 'filter-all':
      scene.catalogListRows = scene.allRowIds();
      scene.catalogKindValue = 'All content';
      scene.spawnEnabled = true;
    case 'unregister-source':
      scene.sandboxContent.clear();
      scene.catalogIds.removeWhere(
        (id) => id.startsWith('dev.topiaforge.sandbox-acceptance:'),
      );
      if (cycle == 2) {
        // Disposing the source while the graph runs tears the graph down.
        scene.projectRunning = false;
        scene.graphPlayingAudioCount = 0;
        scene.audioSourceIds.removeWhere(scene.graphAudioIds.contains);
        scene.graphAudioIds = [];
        scene.interactionCount = 0;
        scene.conversationCount = 0;
        for (final row in scene.interactions) {
          row['active'] = false;
        }
      }
    case 'toggle-during-transition':
      // The restart completed underneath the F5: Running again with a new
      // session and controller identity; the F5 opened no creator host.
      scene.sessionPhase = 'Running';
      scene.worldSessionId = 'world-session-restarted';
      scene.controllerInstanceId = 8888;
      scene.activeHostId = '';
      scene.windowVisible = false;
    case 'toggle-in-menu':
      scene.activeHostId = '';
    case 'stop-world-session':
      _stopWorldSession(scene, cycle);
    case 'end-session':
      _endSession(scene, scenario, cycle);
    case 'observe-refusal':
      break;
    default:
      throw StateError('Unhandled fixture action $action.');
  }
}

const nativeSceneOwner = 'io.github.furroxide.topiaforge.sandbox';

void _editTransform(SandboxScene scene) {
  if (scene.borrowedSelected) {
    final t = (scene.borrowedRobot!['transform']! as List).cast<num>();
    t[1] = t[1] + 1;
  } else {
    final entity = scene.sandboxContent.firstWhere(
      (e) => e['instanceId'] == scene.selectedId,
    );
    final t = (entity['transform']! as List).cast<num>();
    t[1] = t[1] + 1;
    scene.undoDepth++;
  }
}

void _setTransform(SandboxScene scene, int offset, List<num> values) {
  final entity = scene.sandboxContent.firstWhere(
    (e) => e['instanceId'] == scene.selectedId,
  );
  final t = (entity['transform']! as List).cast<num>();
  for (var i = 0; i < values.length; i++) {
    t[offset + i] = values[i];
  }
  scene.undoDepth++;
}

void _runGraph(SandboxScene scene) {
  final a = scene.addEntity(scene.sandboxContent);
  final b = scene.addEntity(scene.sandboxContent);
  scene.graphAudioIds = [a + 100, b + 100];
  scene.audioSourceIds = [...scene.audioSourceIds, ...scene.graphAudioIds];
  scene.cycleGraphAudioIds = [
    ...scene.cycleGraphAudioIds,
    ...scene.graphAudioIds,
  ];
  scene.graphPlayingAudioCount = 1;
  scene.interactionCount++;
  scene.conversationCount++;
  scene.projectRunning = true;
  scene.interactions = [
    ...scene.interactions,
    {
      'instanceId': a + 200,
      'entityInstanceId': a,
      'active': true,
      'prompt': 'ACCEPTANCE',
      'scope': 'global-runtime',
    },
  ];
}

void _stopGraph(SandboxScene scene) {
  scene.sandboxContent.removeRange(
    scene.sandboxContent.length - 2,
    scene.sandboxContent.length,
  );
  // The shipping audio service pools stopped sources rather than destroying
  // them: they stay alive in audioSourceIds, renamed and not playing.
  scene.pooledAudioIds.addAll(scene.graphAudioIds);
  scene.graphAudioIds = [];
  scene.graphPlayingAudioCount = 0;
  scene.interactionCount--;
  scene.conversationCount--;
  scene.projectRunning = false;
  for (final row in scene.interactions) {
    row['active'] = false;
  }
}

void _stopWorldSession(SandboxScene scene, int cycle) {
  scene.windowVisible = false;
  scene.activeHostId = '';
  scene.sandboxContent.clear();
  scene.graphPlayingAudioCount = 0;
  scene.creatorSessionCount = 0;
  if (cycle == 2) {
    // RestartAsync: the original session has left Running; the fresh session
    // is observed at the following toggle-during-transition step.
    scene.sessionPhase = 'Stopping';
  } else {
    scene.sessionPhase = 'Idle';
    scene.worldSessionId = '';
    scene.controllerInstanceId = null;
  }
}

void _endSession(SandboxScene scene, String scenario, int cycle) {
  scene.windowVisible = false;
  scene.hudText = 'NO ACTIVE CREATOR SESSION';
  scene.creatorSessionCount = 0;
  scene.robotEditLeaseCount = 0;
  if (scenario == 'source-unload' && cycle == 1) {
    // Content is already gone; the fixture control robot survives.
    return;
  }
  scene.sandboxContent.clear();
  if (scenario == 'borrowed-robot' && cycle == 2) {
    // Keep the externally written position; restore the brain only.
    final brain = SandboxScene.cloneMap(
      (scene.originalBorrowed!['brain']! as Map).cast<String, Object?>(),
    );
    (scene.borrowedRobot!)['brain'] = brain;
    scene.toasts = [
      ...scene.toasts,
      {
        'nodeId': 'toast-warn',
        'text': 'Robot moved outside Creator Tools; restoration warnings.',
        'style': 'Warning',
        'visible': true,
      },
    ];
    return;
  }
  if (scenario == 'borrowed-robot' && cycle == 3) {
    // Already destroyed; nothing to restore.
    return;
  }
  if (scene.originalBorrowed != null) {
    scene.borrowedRobot = SandboxScene.cloneMap(scene.originalBorrowed!);
  }
}

/// A reviewed expected inventory matching the synthetic scene's catalog.
SandboxExpectedCatalog sceneExpectedCatalog({
  bool reviewed = true,
  List<Map<String, Object?>>? entries,
  List<Map<String, Object?>>? sources,
}) => SandboxExpectedCatalog.parse(
  utf8.encode(
    jsonEncode({
      'schemaVersion': 1,
      'kind': 'sandbox-native-expected-catalog-v1',
      'gameBuild': sandboxGameBuild,
      'reviewed': reviewed,
      'sources': sources ?? const [],
      'entries':
          entries ??
          const [
            {
              'rowId': 'content:dev.topiaforge.sandbox-acceptance:prop',
              'kind': 'Prop',
              'transformCapabilities': 7,
            },
            {
              'rowId': 'content:dev.topiaforge.sandbox-acceptance:character',
              'kind': 'Character',
              'transformCapabilities': 1,
            },
            {
              'rowId': 'robotkit:acceptance',
              'kind': 'Robot',
              'transformCapabilities': 7,
            },
          ],
    }),
  ),
);

/// Resets per-cycle state (fresh session, cleared entities/edits) so each
/// cycle of a multi-cycle scenario begins from the same baseline.
void resetSceneForCycle(
  SandboxScene scene,
  int cycle, {
  required bool preparedBorrowed,
}) {
  scene
    ..worldSessionId = 'world-session-$cycle'
    ..sessionPhase = 'Running'
    ..activeHostId = ''
    ..windowVisible = false
    ..hudText = 'SESSION ACTIVE'
    ..creatorSessionCount = 0
    ..robotEditLeaseCount = 0
    ..conversationCount = 0
    ..interactionCount = 0
    ..selectedId = null
    ..borrowedSelected = false
    ..controlRobotInstanceId = 0
    ..controlCuePlaying = false
    ..projectRunning = false
    ..graphPlayingAudioCount = 0
    ..graphAudioIds = []
    ..cycleGraphAudioIds = []
    ..pooledAudioIds = {}
    ..audioSourceIds = []
    ..personalityAssetIds = []
    ..interactions = []
    ..borrowedRobotDestroyed = false
    ..rosterFocused = false
    ..focusedNode = ''
    ..highContrast = false
    ..uiScale = 1.0
    ..reducedMotion = false
    ..motionIntensity = 1.0
    ..hideWorkbenchHeight = 30
    ..competingRegistered = false
    ..competingOpenCalls = 0
    ..controllerInstanceId = 7777
    ..playerPosition = [0, 0, 0]
    ..playerAim = [0, 0, 1]
    ..aimFocused = false
    ..toasts = []
    ..catalogListRows = scene.allRowIds()
    ..spawnEnabled = true
    ..catalogKindValue = 'All content'
    ..catalogSearchValue = ''
    ..undoDepth = 0
    ..prepared = true
    ..borrowedRobot = null
    ..originalBorrowed = null;
  scene.sandboxContent.clear();
  scene.nativeRobots.clear();
  if (preparedBorrowed) {
    scene.prepareBorrowed();
    scene.originalBorrowed = SandboxScene.cloneMap(scene.borrowedRobot!);
  }
}

/// Applies fixture cleanup: owned content and the previewed personality are
/// destroyed, while stopped graph audio hosts are pooled (released, alive).
void applySceneCleanup(SandboxScene scene) {
  scene.sandboxContent.clear();
  scene.personalityAssetIds = [];
  scene.pooledAudioIds.addAll(scene.graphAudioIds);
  scene.graphAudioIds = [];
  scene.graphPlayingAudioCount = 0;
  scene.prepared = false;
}
