import 'package:test/test.dart';
import 'package:topiaforge/src/sandbox_acceptance/native_annex_verifier.dart';
import 'package:topiaforge/src/sandbox_acceptance/sandbox_specification.dart';
import 'sandbox_native_transcript_fixture.dart';

/// Protocol-v2 transcript evaluation: every scenario resolves independently
/// from the bound exchanges, and single-fact/single-event mutations flip the
/// affected scenario without trusting any driver claim.
void main() {
  test('every scenario resolves passed from a valid v2 transcript', () {
    // completedSamples is the number of observed cycles, except lifecycle-routes
    // which reports 1 only when all three route cycles complete.
    const expectedSamples = {
      'routing': 2,
      'catalog-editing': 1,
      'borrowed-robot': 3,
      'source-unload': 2,
      'hide-reopen': 1,
      'persistence-refusal': 1,
      'graph-rollback': 2,
      'lifecycle-routes': 1,
      'ten-cycles': 10,
    };
    for (final id in sandboxScenarioIds) {
      final result = NativeScenarioTranscript(id).result();
      expect(
        result.status,
        SandboxNativeStatus.passed,
        reason: '$id: ${result.reason}',
      );
      expect(result.completedSamples, expectedSamples[id], reason: id);
    }
  });

  test(
    'the deliberately false driver scenarioState never leaks into a result',
    () {
      // The fixture facts carry scenarioState/completedCycles claims that must be
      // ignored; a genuine failure still surfaces.
      final fixture = NativeScenarioTranscript('borrowed-robot')
        ..atStep(1, 'edit-brain', (s) {
          (s.borrowedRobot!['brain']! as Map)['state'] = 'Standby';
          (s.borrowedRobot!['brain']! as Map)['initialState'] = 'Standby';
          (s.borrowedRobot!['brain']! as Map)['llmDisabled'] = true;
          (s.borrowedRobot!['brain']! as Map)['behaviorTrees'] = <Object?>[];
        });
      expect(fixture.result().status, SandboxNativeStatus.failed);
    },
  );

  group('global cleanup faults fail every scenario that has records', () {
    for (final mutation in <String, void Function(NativeScenarioTranscript)>{
      'broker failure recorded': (f) =>
          f.documentOverrides['failures'] = ['Synthetic broker failure.'],
      'input release unconfirmed': (f) =>
          f.documentOverrides['inputReleased'] = false,
      'fixture release unconfirmed': (f) =>
          f.documentOverrides['fixtureReleased'] = false,
    }.entries) {
      test(mutation.key, () {
        final f = NativeScenarioTranscript('borrowed-robot');
        mutation.value(f);
        expect(f.result().status, SandboxNativeStatus.failed);
      });
    }
    test('a missing cycle persistence during-snapshot fails the scenario', () {
      final f = NativeScenarioTranscript('persistence-refusal')
        ..editEvent('persistence', 1, (d) => d['phase'] = 'before');
      expect(f.result().status, SandboxNativeStatus.failed);
    });
  });

  // Measured motion-atom event faults (spec section 2) — hide-reopen carries
  // scroll/key-hold/mouse-move; graph-rollback carries aim.
  group('measured event faults flip the step to failed', () {
    void expectFail(NativeScenarioTranscript f) =>
        expect(f.result().status, SandboxNativeStatus.failed);
    test('scroll target never became visible', () {
      expectFail(
        NativeScenarioTranscript('hide-reopen')
          ..editEvent('scroll', 0, (d) => d['visible'] = false),
      );
    });
    test('scroll probe fell outside the container rect', () {
      expectFail(
        NativeScenarioTranscript('hide-reopen')
          ..editEvent('scroll', 0, (d) => d['probeX'] = 99999),
      );
    });
    test('a per-sample scroll probe outside the container rect fails', () {
      expectFail(
        NativeScenarioTranscript('hide-reopen')..editEvent('scroll', 0, (d) {
          final sample = (d['samples']! as List).first as Map<String, Object?>;
          sample['probeY'] = -5;
        }),
      );
    });
    test('null scroll probes (no wheel burst sent) are accepted', () {
      final f = NativeScenarioTranscript('hide-reopen')
        ..editEvent('scroll', 0, (d) {
          d['probeX'] = null;
          d['probeY'] = null;
          final sample = (d['samples']! as List).first as Map<String, Object?>;
          sample['probeX'] = null;
          sample['probeY'] = null;
        });
      expect(f.result().status, SandboxNativeStatus.passed);
    });
    test('mouse-move exceeded the relative bound', () {
      expectFail(
        NativeScenarioTranscript('hide-reopen')
          ..editEvent('mouse-move', 0, (d) => d['dx'] = 500),
      );
    });
    test('key-hold was not released', () {
      expectFail(
        NativeScenarioTranscript('hide-reopen')
          ..editEvent('key-hold', 0, (d) => d['released'] = false),
      );
    });
    test('key-hold duration out of bounds', () {
      expectFail(
        NativeScenarioTranscript('hide-reopen')
          ..editEvent('key-hold', 0, (d) => d['heldMilliseconds'] = 5000),
      );
    });
    test('aim never converged', () {
      expectFail(
        NativeScenarioTranscript('graph-rollback')
          ..editEvent('aim', 0, (d) => d['converged'] = false),
      );
    });
    test('aim gain out of bounds', () {
      expectFail(
        NativeScenarioTranscript('graph-rollback')
          ..editEvent('aim', 0, (d) => d['gain'] = 99),
      );
    });
  });

  // Screen baseline gate (spec section 2 / outcome 5); the recomputation
  // faults live in sandbox_native_baseline_test.dart.
  group('screen baseline gate', () {
    test('a beyond-tolerance record over matching pixels fails the step', () {
      final f = NativeScenarioTranscript('routing')
        ..editEvent('capture', 0, (d) {
          (d['baseline']! as Map)['withinTolerance'] = false;
        });
      expect(f.result().status, SandboxNativeStatus.failed);
    });
    test('a null baseline with an entry present fails the step', () {
      final f = NativeScenarioTranscript('routing')
        ..editEvent('capture', 0, (d) => d['baseline'] = null);
      expect(f.result().status, SandboxNativeStatus.failed);
    });
    test(
      'no device-profile baseline entry makes the visual check unavailable',
      () {
        final f = NativeScenarioTranscript('routing');
        // Build first so the admitted entries exist, then withdraw them.
        f.evaluate();
        f.screens.entries.clear();
        expect(f.result().status, SandboxNativeStatus.unavailable);
        expect(f.result().reason, contains('screen baseline'));
      },
    );
  });

  // Runtime-aligned rules: pooled (released) graph audio, refused fixture
  // operations, observer-named unavailable reasons and accepted shapes.
  group('runtime-aligned audio release and observer reasons', () {
    for (final fault in ['playing', 'named', 'undescribed']) {
      test('a graph audio host $fault after cleanup fails', () {
        final f = NativeScenarioTranscript('graph-rollback')
          ..scene.audioFault = fault;
        expect(f.result().status, SandboxNativeStatus.failed);
      });
    }
    test('missing audioSources is unavailable, naming the fact', () {
      final f = NativeScenarioTranscript('ten-cycles');
      f.scene.dropFacts.add('audioSources');
      final result = f.result();
      expect(result.status, SandboxNativeStatus.unavailable);
      expect(result.reason, contains('audioSources'));
    });
    test('a refused fixture operation is unavailable with its code', () {
      final f = NativeScenarioTranscript(
        'borrowed-robot',
      )..atStep(2, 'external-write', (s) => s.failedOperationCode = 'conflict');
      final result = f.result();
      expect(result.status, SandboxNativeStatus.unavailable);
      expect(result.reason, contains('fixture-operation-failed:conflict'));
    });
    test('missing toast diagnostics surface the observer reason', () {
      final f = NativeScenarioTranscript('graph-rollback');
      f.scene.dropFacts.add('toasts');
      f.scene.extraUnavailableReasons = ['toast-diagnostics-unavailable'];
      final result = f.result();
      expect(result.status, SandboxNativeStatus.unavailable);
      expect(result.reason, contains('toast-diagnostics-unavailable'));
    });
    test(
      'the unavailable aimToGraphProp form is unavailable, not malformed',
      () {
        final f = NativeScenarioTranscript('graph-rollback')
          ..scene.aimUnavailable = true;
        final result = f.result();
        expect(result.status, SandboxNativeStatus.unavailable);
        expect(result.reason, contains('available:false'));
      },
    );
    test('interaction rows with a null prompt are accepted', () {
      final f = NativeScenarioTranscript('hide-reopen')
        ..atStep(1, 'run-graph', (s) => s.interactions.last['prompt'] = null);
      expect(f.result().status, SandboxNativeStatus.passed);
    });
  });

  // Missing/malformed v2 facts become unavailable naming the fact (outcome 2).
  group('missing v2 facts report unavailable, never a default', () {
    for (final entry in <String, (String, String)>{
      'accessibility': ('routing', 'accessibility'),
      'competingHost': ('routing', 'competingHost'),
      'controlCuePlaying': ('graph-rollback', 'controlCuePlaying'),
      'aimToGraphProp': ('graph-rollback', 'aimToGraphProp'),
      'toasts': ('graph-rollback', 'toasts'),
      'catalogSources': ('source-unload', 'catalogSources'),
      'controllerInstanceId': ('ten-cycles', 'controllerInstanceId'),
      'personalityAssetIds': ('borrowed-robot', 'personalityAssetIds'),
      'audioSourceIds': ('ten-cycles', 'audioSourceIds'),
    }.entries) {
      test('missing ${entry.key}', () {
        final (scenario, fact) = entry.value;
        final f = NativeScenarioTranscript(scenario);
        f.scene.dropFacts.add(fact);
        final result = f.result();
        expect(result.status, SandboxNativeStatus.unavailable);
        expect(result.reason, contains(fact));
      });
    }
  });
}
