import 'dart:convert';
import 'package:test/test.dart';
import 'package:topiaforge/src/sandbox_acceptance/native_driver_vocabulary.dart';
import 'sandbox_native_transcript_fixture.dart';

/// Driver manifest v2 acceptance, precise v1 rejection, and the closed
/// vocabularies/atom schemas/action inventory of spec section 2 (outcome 1),
/// refused by the transcript verifier before any oracle runs.
void main() {
  List<int> mutatedDriver(void Function(Map<String, Object?>) mutate) {
    final json =
        jsonDecode(
              utf8.decode(
                NativeScenarioTranscript('persistence-refusal').driverBytes,
              ),
            )
            as Map<String, Object?>;
    mutate(json);
    return utf8.encode(jsonEncode(json));
  }

  void expectRefused(List<int> driver, {String? message}) {
    final fixture = NativeScenarioTranscript('persistence-refusal')
      ..driverOverride = driver;
    Object? caught;
    try {
      fixture.evaluate();
    } on Object catch (error) {
      caught = error;
    }
    expect(caught, isA<StateError>());
    if (message != null) {
      expect((caught! as StateError).message, contains(message));
    }
  }

  test('a valid v2 manifest is accepted', () {
    expect(
      NativeScenarioTranscript('persistence-refusal').result().reason,
      isEmpty,
    );
  });

  test('v1 is refused with the exact retirement message', () {
    expectRefused(
      mutatedDriver((j) {
        j['kind'] = 'sandbox-native-driver-actions-v1';
        j['schemaVersion'] = 1;
      }),
      message:
          'sandbox-native-driver-actions-v1 is retired; '
          'sandbox-native-driver-actions-v2 (schemaVersion 2) is required.',
    );
  });

  test('schemaVersion 1 with the v2 kind is refused', () {
    expectRefused(mutatedDriver((j) => j['schemaVersion'] = 1));
  });

  for (final mutation in <String, void Function(Map<String, Object?>)>{
    'unknown atom kind': (j) =>
        ((j['actions']! as Map)['open']! as List)[0] = {'kind': 'teleport'},
    'unknown key': (j) => ((j['actions']! as Map)['open']! as List)[0] = {
      'kind': 'key',
      'key': 'F13',
    },
    'unknown node': (j) => ((j['actions']! as Map)['hide-close']! as List)[0] =
        {'kind': 'click', 'nodeId': 'secret-button'},
    'unknown request operation': (j) =>
        ((j['actions']! as Map)['unregister-source']! as List)[0] = {
          'kind': 'request',
          'operation': 'format-disk',
        },
    'mouse-move out of bounds': (j) =>
        ((j['actions']! as Map)['camera-hidden']! as List)[0] = {
          'kind': 'mouse-move',
          'dx': 900,
          'dy': 0,
        },
    'key-hold out of bounds': (j) =>
        ((j['actions']! as Map)['move-while-visible']! as List)[0] = {
          'kind': 'key-hold',
          'key': 'W',
          'milliseconds': 5000,
        },
    'aim gain out of bounds': (j) =>
        ((j['actions']! as Map)['aim-graph-prop']! as List)[0] = {
          'kind': 'aim',
          'fact': 'aimToGraphProp',
          'maxIterations': 40,
          'gain': 99,
        },
    'aim unknown fact': (j) =>
        ((j['actions']! as Map)['aim-graph-prop']! as List)[0] = {
          'kind': 'aim',
          'fact': 'somethingElse',
          'maxIterations': 40,
          'gain': 5,
        },
    'scroll unknown container': (j) =>
        ((j['actions']! as Map)['edit-transform']! as List)[0] = {
          'kind': 'scroll-into-view',
          'nodeId': 'nudge-up',
          'containerId': r'$scroll-9',
        },
  }.entries) {
    test('vocabulary refuses ${mutation.key}', () {
      expectRefused(mutatedDriver(mutation.value));
    });
  }

  test('an undeclared action is refused before evaluation', () {
    expectRefused(
      mutatedDriver(
        (j) => (j['actions']! as Map)['open-shell'] = [
          {'kind': 'capture'},
        ],
      ),
      message: 'undeclared action',
    );
  });

  test('a missing action is refused even when no scenario uses it', () {
    // persistence-refusal never interacts; the inventory must still be whole.
    expectRefused(
      mutatedDriver((j) => (j['actions']! as Map).remove('interact')),
      message: 'lacks interact',
    );
  });

  test('a recipe beyond twelve atoms is refused', () {
    expectRefused(
      mutatedDriver(
        (j) => (j['actions']! as Map)['observe-refusal'] = List.filled(13, {
          'kind': 'capture',
        }),
      ),
      message: 'needs 1 to 12',
    );
  });

  // The first recipe atom of each kind, faulted in place in the real manifest.
  for (final kind in nativeDriverAtomSchemas.keys) {
    Map<String, Object?> firstAtom(Map<String, Object?> json) =>
        (json['actions']! as Map).values
            .expand((atoms) => atoms as List)
            .cast<Map<String, Object?>>()
            .firstWhere((atom) => atom['kind'] == kind);
    String field(Map<String, Object?> atom) => atom.keys.lastWhere(
      (k) => k != 'kind' && k != 'surfaceId',
      orElse: () => 'kind',
    );
    for (final fault in <String, void Function(Map<String, Object?>)>{
      'an undeclared field': (atom) => atom['extra'] = true,
      'a missing field': (atom) => atom.remove(field(atom)),
      'a wrongly typed field': (atom) =>
          atom[field(atom)] = atom[field(atom)] is String ? 7 : '7',
    }.entries) {
      test('$kind atom with ${fault.key} is refused', () {
        expectRefused(mutatedDriver((j) => fault.value(firstAtom(j))));
      });
    }
  }
}
