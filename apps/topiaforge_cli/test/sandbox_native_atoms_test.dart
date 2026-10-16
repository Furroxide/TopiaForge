import 'dart:convert';
import 'dart:io';
import 'package:test/test.dart';
import 'package:topiaforge/src/sandbox_acceptance/native_driver_manifest.dart';
import 'package:topiaforge/src/sandbox_acceptance/native_driver_vocabulary.dart';

/// Every atom kind against the broker's closed per-kind schema: missing,
/// undeclared, cross-kind, wrongly typed and out-of-vocabulary fields are
/// refused exactly where `DriverVocabulary.ValidateStep` refuses them.
void main() {
  void accepted(Map<String, Object?> atom) =>
      expect(() => validateNativeDriverAtom(atom), returnsNormally);
  void refused(Map<String, Object?> atom, [String? message]) => expect(
    () => validateNativeDriverAtom(atom),
    throwsA(
      isA<StateError>().having(
        (e) => e.message,
        'message',
        contains(message ?? ''),
      ),
    ),
    reason: jsonEncode(atom),
  );

  test('every atom kind has valid samples and all pass', () {
    expect(_valid.keys.toSet(), nativeDriverAtomSchemas.keys.toSet());
    _valid.values.expand((samples) => samples).forEach(accepted);
  });

  test('every schema field has exactly one value rule', () {
    for (final schema in nativeDriverAtomSchemas.values) {
      for (final field in {
        ...schema.required,
        ...schema.oneOf,
        ...schema.optional,
        'surfaceId',
      }) {
        final rules = [
          nativeDriverFieldVocabularies,
          nativeDriverIntegerFields,
          nativeDriverTextFields,
        ].where((rules) => rules.containsKey(field));
        expect(rules, hasLength(1), reason: field);
      }
    }
  });

  for (final MapEntry(key: kind, value: samples) in _valid.entries) {
    final schema = nativeDriverAtomSchemas[kind]!;
    group('$kind atom', () {
      test('refuses each missing required field', () {
        for (final sample in samples) {
          for (final field in sample.keys.where((k) => k != 'kind')) {
            final without = {...sample}..remove(field);
            if (field == 'surfaceId' || schema.optional.contains(field)) {
              accepted(without);
            } else {
              refused(without, kind);
            }
          }
        }
      });
      test('refuses an undeclared field and every cross-kind field', () {
        for (final sample in samples) {
          refused({...sample, 'extra': true}, 'undeclared field');
          final allowed = {
            ...sample.keys,
            ...schema.optional,
            // A one-of alternative is refused as "both", not as undeclared.
            ...schema.oneOf,
          };
          for (final field in _fieldValues.keys.where(
            (f) => !allowed.contains(f),
          )) {
            refused({...sample, field: _fieldValues[field]}, 'undeclared');
          }
        }
      });
      test('refuses a wrongly typed value in every field', () {
        for (final sample in samples) {
          for (final field in sample.keys.where((k) => k != 'kind')) {
            for (final wrong in _wrongTypes(sample[field])) {
              refused({...sample, field: wrong}, 'is invalid');
            }
          }
        }
      });
    });
  }

  test('integer fields accept their bounds and refuse one beyond', () {
    for (final MapEntry(key: field, value: (min, max))
        in nativeDriverIntegerFields.entries) {
      final sample = _valid.values
          .expand((s) => s)
          .firstWhere((s) => s.containsKey(field));
      accepted({...sample, field: min});
      accepted({...sample, field: max});
      refused({...sample, field: min - 1}, 'is invalid');
      refused({...sample, field: max + 1}, 'is invalid');
    }
  });

  test('closed vocabularies refuse undeclared values', () {
    refused({'kind': 'key', 'key': 'F13'}, 'key is invalid');
    refused({'kind': 'click', 'nodeId': 'launch-personal-game'});
    refused({'kind': 'click', 'nodeId': 'confirm', 'surfaceId': r'$toast'});
    refused({'kind': 'click', 'nodeId': 'confirm', 'surfaceId': null});
    refused({'kind': 'request', 'operation': 'prepare'});
    refused({
      'kind': 'aim',
      'fact': 'playerAim',
      'maxIterations': 40,
      'gain': 5,
    });
    refused({
      'kind': 'replace-text',
      'nodeId': 'persona-name',
      'textFromFact': 'secret',
    });
    refused({
      'kind': 'select-list-item',
      'nodeId': 'roster-list',
      'itemIdFromFact': 'worldSessionId',
    });
    refused({
      'kind': 'scroll-into-view',
      'nodeId': 'nudge-up',
      'containerId': r'$scroll-9',
    });
  });

  test('literal text is bounded and free of control characters', () {
    Map<String, Object?> text(String value) => {
      'kind': 'replace-text',
      'nodeId': 'persona-name',
      'text': value,
    };
    Map<String, Object?> row(String value) => {
      'kind': 'select-list-item',
      'nodeId': 'catalog-list',
      'itemId': value,
    };
    accepted(text('x' * 1024));
    accepted(row('r' * 512));
    for (final invalid in ['x' * 1025, 'a\nb', 'a\u007fb', 'a\u0085b']) {
      refused(text(invalid), 'text is invalid');
    }
    for (final invalid in ['', 'r' * 513, 'row\u0000']) {
      refused(row(invalid), 'itemId is invalid');
    }
  });

  test('one-of fields refuse both and neither alternatives', () {
    refused({
      'kind': 'replace-text',
      'nodeId': 'catalog-search',
      'text': 'x',
      'textFromFact': 'actionCatalogDisplayName',
    }, 'exactly one');
    refused({
      'kind': 'select-list-item',
      'nodeId': 'roster-list',
      'itemId': 'row',
      'itemIdFromFact': 'borrowedRosterId',
    }, 'exactly one');
    refused({'kind': 'replace-text', 'nodeId': 'catalog-search'}, 'one of');
    refused({'kind': 'select-list-item', 'nodeId': 'roster-list'}, 'one of');
  });

  test('a scroll container cannot be its own target', () {
    refused({
      'kind': 'scroll-into-view',
      'nodeId': r'$scroll-0',
      'containerId': r'$scroll-0',
    }, 'own target');
  });

  test('missing, unknown or non-text kinds are refused', () {
    refused({'key': 'F5'}, 'Unknown');
    refused({'kind': 'shell', 'command': 'anything'}, 'Unknown');
    refused({'kind': 7}, 'Unknown');
  });

  group('manifest action inventory', () {
    Map<String, Object?> actions() =>
        (jsonDecode(
                  File(
                    '../../tests/TopiaForge.SandboxAcceptanceNative/'
                    'driver-actions-v2.json',
                  ).readAsStringSync(),
                )
                as Map<String, Object?>)['actions']!
            as Map<String, Object?>;
    void refusedActions(Map<String, Object?> value, String message) => expect(
      () => validateNativeDriverActions(value),
      throwsA(
        isA<StateError>().having(
          (e) => e.message,
          'message',
          contains(message),
        ),
      ),
    );

    test('the checked-in manifest declares exactly the inventory', () {
      expect(actions().keys.toSet(), nativeDriverActions);
      validateNativeDriverActions(actions());
    });
    test('an undeclared action is refused', () {
      refusedActions(
        actions()
          ..['open-shell'] = [
            {'kind': 'capture'},
          ],
        'undeclared action',
      );
    });
    test('every missing action is refused by name', () {
      for (final name in nativeDriverActions) {
        refusedActions(actions()..remove(name), 'lacks $name');
      }
    });
    test('recipes hold 1 to 12 atom objects', () {
      final twelve = List.filled(12, <String, Object?>{'kind': 'capture'});
      validateNativeDriverActions(actions()..['interact'] = twelve);
      refusedActions(
        actions()..['interact'] = [...twelve, twelve.first],
        'needs 1 to 12',
      );
      refusedActions(actions()..['interact'] = <Object?>[], 'needs 1 to 12');
      refusedActions(actions()..['interact'] = {'kind': 'key'}, 'needs 1');
      expect(
        () => validateNativeDriverActions(actions()..['interact'] = ['key']),
        throwsStateError,
      );
      expect(() => validateNativeDriverActions([]), throwsStateError);
    });
  });
}

const _valid = <String, List<Map<String, Object?>>>{
  'key': [
    {'kind': 'key', 'key': 'F5'},
  ],
  'click': [
    {'kind': 'click', 'nodeId': 'confirm', 'surfaceId': r'$modal'},
    {'kind': 'click', 'nodeId': r'$close'},
  ],
  'replace-text': [
    {'kind': 'replace-text', 'nodeId': 'catalog-search', 'text': 'Prop'},
    {'kind': 'replace-text', 'nodeId': 'catalog-search', 'text': ''},
    {
      'kind': 'replace-text',
      'nodeId': 'catalog-search',
      'textFromFact': 'actionCatalogDisplayName',
    },
  ],
  'select-list-item': [
    {'kind': 'select-list-item', 'nodeId': 'catalog-list', 'itemId': 'row-1'},
    {
      'kind': 'select-list-item',
      'nodeId': 'roster-list',
      'itemIdFromFact': 'borrowedRosterId',
    },
  ],
  'barrier': [
    {'kind': 'barrier', 'minimumFrames': 2},
  ],
  'request': [
    {'kind': 'request', 'operation': 'control-cue'},
  ],
  'capture': [
    {'kind': 'capture'},
    {'kind': 'capture', 'surfaceId': 'sandbox-creator-window'},
  ],
  'scroll-into-view': [
    {
      'kind': 'scroll-into-view',
      'nodeId': 'nudge-up',
      'containerId': r'$scroll-2',
    },
    {
      'kind': 'scroll-into-view',
      'nodeId': 'project-list',
      'itemIdFromFact': 'projectId',
      'containerId': r'$scroll-1',
    },
  ],
  'mouse-move': [
    {'kind': 'mouse-move', 'dx': 120, 'dy': 0},
    {'kind': 'mouse-move', 'dx': -400, 'dy': 400},
  ],
  'key-hold': [
    {'kind': 'key-hold', 'key': 'W', 'milliseconds': 300},
  ],
  'aim': [
    {'kind': 'aim', 'fact': 'aimToGraphProp', 'maxIterations': 40, 'gain': 5},
  ],
};

/// A valid value for every field any kind declares.
const _fieldValues = <String, Object?>{
  'key': 'F5',
  'nodeId': 'confirm',
  'text': 'x',
  'textFromFact': 'actionCatalogDisplayName',
  'itemId': 'row-1',
  'itemIdFromFact': 'projectId',
  'minimumFrames': 2,
  'operation': 'control-cue',
  'containerId': r'$scroll-0',
  'dx': 1,
  'dy': 1,
  'milliseconds': 300,
  'fact': 'aimToGraphProp',
  'maxIterations': 1,
  'gain': 1,
};

List<Object?> _wrongTypes(Object? valid) => [
  null,
  true,
  <Object?>[valid],
  <String, Object?>{'value': valid},
  // A JSON integer written as text, `2.0` or a fraction is not an integer.
  if (valid is int) ...[
    '$valid',
    valid.toDouble(),
    valid + 0.5,
  ] else ...[
    7,
    7.5,
  ],
];
