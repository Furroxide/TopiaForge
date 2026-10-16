import 'package:test/test.dart';
import 'package:topiaforge/src/sandbox_acceptance/native_annex_verifier.dart';
import 'sandbox_native_scene.dart';
import 'sandbox_native_transcript_fixture.dart';

/// One synthetic transcript mutation per new v2 step postcondition; each must
/// flip its scenario away from `passed` (spec section 4, outcome 6).
void main() {
  void selectedTransform(SandboxScene s, int index, num value) {
    final entity = s.sandboxContent.firstWhere(
      (e) => e['instanceId'] == s.selectedId,
    );
    (entity['transform']! as List)[index] = value;
  }

  final cases = <String, _Case>{
    'accessibility-high-contrast flag off': _Case(
      'routing',
      1,
      'accessibility-high-contrast',
      (s) => s.highContrast = false,
    ),
    'accessibility-scale-150 height not scaled': _Case(
      'routing',
      1,
      'accessibility-scale-150',
      (s) => s.hideWorkbenchHeight = 30,
    ),
    'accessibility-reduced-motion intensity nonzero': _Case(
      'routing',
      1,
      'accessibility-reduced-motion',
      (s) => s.motionIntensity = 4,
    ),
    'accessibility-reset still high contrast': _Case(
      'routing',
      1,
      'accessibility-reset',
      (s) => s.highContrast = true,
    ),
    'focus-next did not move focus': _Case(
      'routing',
      1,
      'focus-next',
      (s) => s.focusedNode = '',
    ),
    'hide-f5 still visible': _Case(
      'routing',
      1,
      'hide-f5',
      (s) => s.windowVisible = true,
    ),
    'duplicate-toggle ended hidden': _Case(
      'routing',
      1,
      'duplicate-toggle',
      (s) => s.windowVisible = false,
    ),
    'register-competing-host not registered': _Case(
      'routing',
      2,
      'register-competing-host',
      (s) => s.competingRegistered = false,
    ),
    'unregister-competing-host still registered': _Case(
      'routing',
      2,
      'unregister-competing-host',
      (s) => s.competingRegistered = true,
    ),
    'search-nonmatching left spawn enabled': _Case(
      'catalog-editing',
      1,
      'search-nonmatching',
      (s) => s.spawnEnabled = true,
    ),
    'filter-robots wrong dropdown value': _Case(
      'catalog-editing',
      1,
      'filter-robots',
      (s) => s.catalogKindValue = 'All content',
    ),
    'filter-all wrong dropdown value': _Case(
      'catalog-editing',
      1,
      'filter-all',
      (s) => s.catalogKindValue = 'Robots',
    ),
    'edit-transform wrong delta': _Case(
      'catalog-editing',
      1,
      'edit-transform',
      (s) => selectedTransform(s, 1, 5),
    ),
    'edit-rotation wrong quaternion': _Case(
      'catalog-editing',
      1,
      'edit-rotation',
      (s) => selectedTransform(s, 4, 0.1),
    ),
    'edit-scale wrong scale': _Case(
      'catalog-editing',
      1,
      'edit-scale',
      (s) => selectedTransform(s, 7, 2.0),
    ),
    'undo did not decrease depth': _Case(
      'catalog-editing',
      1,
      'undo',
      (s) => s.undoDepth += 2,
    ),
    'spawn-control-robot absent': _Case(
      'source-unload',
      1,
      'spawn-control-robot',
      (s) => s.controlRobotInstanceId = 0,
    ),
    'despawn-control-robot still alive': _Case(
      'source-unload',
      1,
      'despawn-control-robot',
      (s) => s.controlRobotInstanceId = 42,
    ),
    'unregister-source lost the control robot': _Case(
      'source-unload',
      1,
      'unregister-source',
      (s) => s.controlRobotInstanceId = 0,
    ),
    'select-borrowed not focused': _Case(
      'borrowed-robot',
      1,
      'select-borrowed',
      (s) => s.focusedNode = '',
    ),
    'edit-personality no hacked id': _Case(
      'borrowed-robot',
      1,
      'edit-personality',
      (s) => (s.borrowedRobot!['brain']! as Map)['hackedPersonalityId'] = 0,
    ),
    'external-write did not move by two': _Case(
      'borrowed-robot',
      2,
      'external-write',
      (s) => (s.borrowedRobot!['transform']! as List)[0] = 0,
    ),
    'destroy-borrowed not marked': _Case(
      'borrowed-robot',
      3,
      'destroy-borrowed',
      (s) => s.borrowedRobotDestroyed = false,
    ),
    'end-session borrowed not restored': _Case(
      'borrowed-robot',
      1,
      'end-session',
      (s) => (s.borrowedRobot!['transform']! as List)[1] = 1,
    ),
    'end-session c2 missing warning toast': _Case(
      'borrowed-robot',
      2,
      'end-session',
      (s) => s.toasts = [],
    ),
    'end-session c3 robot resurrected': _Case(
      'borrowed-robot',
      3,
      'end-session',
      (s) => s.borrowedRobot = {
        'instanceId': 9001,
        'sceneHandle': 7,
        'transform': SandboxScene.identityTransform(),
        'brain': {'state': 'Standby'},
      },
    ),
    'run-graph no graph audio': _Case(
      'hide-reopen',
      1,
      'run-graph',
      (s) => s.graphPlayingAudioCount = 0,
    ),
    'stop-graph left audio playing': _Case(
      'hide-reopen',
      1,
      'stop-graph',
      (s) => s.graphPlayingAudioCount = 3,
    ),
    'reopen dropped the running graph': _Case(
      'hide-reopen',
      1,
      'reopen',
      (s) => s.projectRunning = false,
    ),
    'move-while-visible actually moved': _Case(
      'hide-reopen',
      1,
      'move-while-visible',
      (s) => s.playerPosition = [9, 0, 0],
    ),
    'camera-hidden aim unchanged': _Case(
      'hide-reopen',
      1,
      'camera-hidden',
      (s) => s.playerAim = [0, 0, 1],
    ),
    'text-focus wrong field text': _Case(
      'hide-reopen',
      1,
      'text-focus',
      (s) => s.catalogSearchValue = 'X',
    ),
    'control-cue not playing': _Case(
      'graph-rollback',
      1,
      'control-cue',
      (s) => s.controlCuePlaying = false,
    ),
    'stop-control-cue still playing': _Case(
      'graph-rollback',
      1,
      'stop-control-cue',
      (s) => s.controlCuePlaying = true,
    ),
    'aim-graph-prop not focused': _Case(
      'graph-rollback',
      1,
      'aim-graph-prop',
      (s) => s.aimFocused = false,
    ),
    'interact wrong branch toast': _Case(
      'graph-rollback',
      1,
      'interact',
      (s) => s.toasts = [
        {
          'nodeId': 'toast-x',
          'text': 'WRONG BRANCH executed.',
          'style': 'Error',
          'visible': true,
        },
      ],
    ),
    'stop-before-start changed the entity set': _Case(
      'graph-rollback',
      2,
      'stop-before-start',
      (s) => s.addEntity(s.sandboxContent),
    ),
    'stop-before-start while a project runs': _Case(
      'graph-rollback',
      2,
      'stop-before-start',
      (s) => s.projectRunning = true,
    ),
    'stop-before-start stop control enabled': _Case(
      'graph-rollback',
      2,
      'stop-before-start',
      (s) => s.stopProjectEnabled = true,
    ),
    'stop-world-session c1 not idle': _Case(
      'lifecycle-routes',
      1,
      'stop-world-session',
      (s) => s.sessionPhase = 'Running',
    ),
    'stop-world-session c2 never left running': _Case(
      'lifecycle-routes',
      2,
      'stop-world-session',
      (s) => s.sessionPhase = 'Running',
    ),
    'toggle-during-transition same controller': _Case(
      'lifecycle-routes',
      2,
      'toggle-during-transition',
      (s) => s.controllerInstanceId = 7777,
    ),
    'toggle-during-transition not running again': _Case(
      'lifecycle-routes',
      2,
      'toggle-during-transition',
      (s) => s.sessionPhase = 'Stopping',
    ),
    'toggle-during-transition opened a host': _Case(
      'lifecycle-routes',
      2,
      'toggle-during-transition',
      (s) => s.activeHostId = 'io.github.furroxide.topiaforge.sandbox',
    ),
    'toggle-in-menu created a session': _Case(
      'lifecycle-routes',
      3,
      'toggle-in-menu',
      (s) => s.creatorSessionCount = 5,
    ),
    'move-player did not move': _Case(
      'ten-cycles',
      1,
      'move-player',
      (s) => s.playerPosition = [0, 0, 0],
    ),
  };

  cases.forEach((name, testCase) {
    test('fault: $name', () {
      final fixture = NativeScenarioTranscript(testCase.scenario);
      fixture.atStep(testCase.cycle, testCase.action, testCase.mutate);
      expect(fixture.result().status, isNot(SandboxNativeStatus.passed));
    });
  });

  test('fault: low-contrast Sandbox widget fails the contrast check', () {
    final fixture = NativeScenarioTranscript('routing')
      ..scene.lowContrastWidget = true;
    expect(fixture.result().status, SandboxNativeStatus.failed);
  });

  test('fault: missing isolation-active caption fails observe-refusal', () {
    final fixture = NativeScenarioTranscript('persistence-refusal')
      ..scene.hideStatusCaption = true;
    expect(fixture.result().status, SandboxNativeStatus.failed);
  });
}

final class _Case {
  const _Case(this.scenario, this.cycle, this.action, this.mutate);
  final String scenario, action;
  final int cycle;
  final void Function(SandboxScene) mutate;
}
