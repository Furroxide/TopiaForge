import 'dart:convert';
import 'package:test/test.dart';
import 'package:topiaforge/src/sandbox_acceptance/native_annex_verifier.dart';
import 'package:topiaforge/src/sandbox_acceptance/native_transcript_facts.dart';
import 'sandbox_native_fixture.dart';
import 'sandbox_native_transcript_fixture.dart';

void main() {
  test(
    'independent refusal branch ignores driver claims and leaves unrun cases missing',
    () {
      final results = NativeTranscriptFixture().evaluate();
      expect(
        results
            .singleWhere((r) => r.scenarioId == 'persistence-refusal')
            .status,
        SandboxNativeStatus.passed,
      );
      expect(
        results.singleWhere((r) => r.scenarioId == 'ten-cycles').status,
        SandboxNativeStatus.missing,
      );
      expect(
        results
            .singleWhere((r) => r.scenarioId == 'ten-cycles')
            .completedSamples,
        0,
      );
    },
  );
  for (final mutation in <String, void Function(NativeTranscriptFixture)>{
    'replayed wire sequence': (f) => f.responses[1]['sequence'] = 1,
    'foreign manager': (f) => f.responses[2]['managerSessionId'] = 'd' * 32,
    'foreign challenge': (f) => f.responses[2]['challenge'] = 'e' * 64,
    'foreign OS identity': (f) =>
        nativeMap(f.transcript['identity'])['sessionId'] = 99,
    'response unknown key': (f) => f.responses[2]['pass'] = true,
    'backwards frame': (f) => f.responses[2]['frame'] = 1,
    'missing event': (f) => f.events[3]['sequence'] = 99,
    'nonfinite native fact': (f) =>
        nativeMap(f.responses[2]['facts'])['x'] = double.nan,
  }.entries) {
    test('rejects ${mutation.key}', () {
      final fixture = NativeTranscriptFixture();
      mutation.value(fixture);
      expect(
        fixture.evaluate,
        throwsA(
          anyOf(
            isA<StateError>(),
            isA<ArgumentError>(),
            isA<JsonUnsupportedObjectError>(),
          ),
        ),
      );
    });
  }
  for (final mutation in <String, void Function(NativeTranscriptFixture)>{
    'native refusal actually ready': (f) =>
        nativeMap(f.responses[2]['facts'])['mutationSafetyState'] = 'Ready',
    'missing actual persistence availability': (f) => nativeMap(
      f.responses[2]['facts'],
    ).remove('persistenceIsolationAvailable'),
    'no destruction barrier': (f) => f.responses.last['frame'] = 7,
    'native objects still alive': (f) =>
        nativeMap(f.responses.last['facts'])['nativeCleanupPendingObjects'] = 1,
    'input release unconfirmed': (f) => f.transcript['inputReleased'] = false,
    'no actual input': (f) => nativeMap(
      f.events.singleWhere((e) => e['kind'] == 'input')['data'],
    )['kind'] = 'key',
    'persistence overflow': (f) => nativeMap(
      f.events.lastWhere((e) => e['kind'] == 'persistence')['data'],
    )['overflow'] = true,
    'changed save bytes': (f) => nativeMap(
      nativeList(
        nativeMap(
          f.events.lastWhere((e) => e['kind'] == 'persistence')['data'],
        )['files'],
      ).single,
    )['sha256'] = 'f' * 64,
    'broker failure': (f) =>
        f.transcript['failures'] = ['Synthetic injected failure.'],
  }.entries) {
    test('fails ${mutation.key} despite positive progress claims', () {
      final fixture = NativeTranscriptFixture();
      mutation.value(fixture);
      expect(
        fixture
            .evaluate()
            .singleWhere((r) => r.scenarioId == 'persistence-refusal')
            .status,
        SandboxNativeStatus.failed,
      );
    });
  }
  test('native object replacement cannot satisfy preservation', () {
    Map<String, Object?> facts(int id) => {
      'sandboxContent': [
        {
          'instanceId': id,
          'sceneHandle': 3,
          'alive': true,
          'transform': [0, 0, 0, 0, 0, 0, 1, 1, 1, 1],
        },
      ],
      'nativeRobots': <Object?>[],
    };
    expect(sameEntities(facts(17), facts(18)), isFalse);
  });
  test('intermediate resource leak fails exact restoration baseline', () {
    final a = <String, Object?>{for (final key in resourceCounters) key: 0};
    final b = {...a, 'interactionCount': 1};
    expect(resourcesEqual(a, b), isFalse);
  });
  test('missing counter never becomes zero', () {
    final a = <String, Object?>{for (final key in resourceCounters) key: 0};
    final b = {...a}..remove('conversationCount');
    expect(() => resourcesEqual(a, b), throwsStateError);
  });
  test('borrowed full transform and identity must restore', () {
    Map<String, Object?> facts(int id, num scale) => {
      'borrowedRobot': {
        'instanceId': id,
        'sceneHandle': 1,
        'transform': [0, 0, 0, 0, 0, 0, 1, scale, 1, 1],
        'brain': {'state': 'Standby'},
      },
    };
    expect(restoredBorrowed(facts(8, 1), facts(8, 1.25)), isFalse);
    expect(restoredBorrowed(facts(8, 1), facts(9, 1)), isFalse);
    expect(restoredBorrowed(facts(8, 1), facts(8, 1)), isTrue);
  });
}
