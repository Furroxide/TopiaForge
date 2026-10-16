import 'dart:convert';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'manifest_migration_fixture.dart';

// decodeJsonPreservingValues returns what decoding the whole document produced,
// so it has to check each number against that value. Inside a document, Dart's
// decoder turns a number with an exponent above 400 or below -400 into infinity
// or zero, whatever digits precede the exponent, while a number that ends the
// text, like a token decoded alone, goes through double.parse.
void main() {
  // Spellings of values a double holds, mapped to those values, which decoding
  // a document changes to zero or infinity.
  final changed = {
    '1${'0' * 40000}e-40000': 1.0,
    '0.${'0' * 39999}1e40000': 1.0,
    '1${'0' * 100}e-401': 1e-301,
    '-0.${'0' * 100}1e401': -1e300,
  };
  // One step short of the shortcut, so it decodes unchanged: 1e-300.
  final kept = '1${'0' * 100}e-400';

  Matcher refusalAt(int character) => throwsA(
    isA<FormatException>().having(
      (error) => error.message,
      'message',
      'Manifest contains a number at character $character that cannot be preserved without changing its value. No state was written. Repair or explicitly replace that value in the original file before retrying.',
    ),
  );

  test('refuses numbers that decoding the document changes', () {
    for (final MapEntry(key: token, value: value) in changed.entries) {
      final shown = '${token.length}-character $value';
      expect(
        jsonDecode('[$token]'),
        isNot([value]),
        reason:
            'Dart now decodes $shown unchanged inside a document, so it no '
            'longer tests this refusal.',
      );
      for (final (document, character) in [
        ('{"c": $token}', 7),
        ('[true, "1", $token]', 13),
        ('{"a": [{}, {"b": $token}]}', 18),
        ('$token\n', 1),
      ]) {
        expect(
          () => decodeJsonPreservingValues(document, label: 'Manifest'),
          refusalAt(character),
          reason: document.replaceFirst(token, '<$shown>'),
        );
      }
    }
  });

  test('accepts long spellings that decoding keeps', () {
    // A number that ends the text keeps its value, so it stays accepted.
    expect(
      decodeJsonPreservingValues('1${'0' * 40000}e-40000', label: 'Manifest'),
      1.0,
    );
    expect(decodeJsonPreservingValues('{"c": [$kept]}', label: 'Manifest'), {
      'c': [1e-300],
    });
  });

  test('checks each number against its own member', () {
    // Strings hold digits, brackets and escaped quotes, and names are escaped,
    // repeated in other objects or look like indexes.
    String document(String last) =>
        '{"1": "[2, {", "\\u0062": [$kept, "\\"]", {}, [[]]], '
        '"a": {"9": -0.5e1, "b": {"1": "}"}, "x": [0, [], [true, $last]]}}';
    final refused = document('1.0000000000000001');
    expect(
      () => decodeJsonPreservingValues(refused, label: 'Manifest'),
      refusalAt(refused.indexOf('1.0000000000000001') + 1),
    );
    final accepted = document('1.25e2');
    expect(
      decodeJsonPreservingValues(accepted, label: 'Manifest'),
      jsonDecode(accepted),
    );
  });

  test('refuses a manifest number that decoding changes', () {
    final raw = jsonEncode(migrationFixture(6));
    final weight = '0.${'0' * 100}1e401';
    final result = const ManifestMigrationPlanner().plan(
      '${raw.substring(0, raw.length - 1)},"x-weight":$weight}',
      sourceLabel: 'numbers.json',
    );
    expect(result.disposition, ManifestMigrationDisposition.refused);
    expect(result.outputJson, isNull);
    expect(
      result.diagnostics.single.message,
      contains('cannot be preserved without changing its value'),
    );
  });

  test('checks a 20,000-member document in linear time', () {
    final members = [
      for (var i = 0; i < 20000; i++)
        '"\\u006b$i": [$i, {"v": -$i.5e-1, "s": "$i"}]',
    ];
    final text = '{${members.join(', ')}}';
    final watch = Stopwatch()..start();
    final decoded = decodeJsonPreservingValues(text, label: 'Manifest');
    final elapsed = watch.elapsedMilliseconds;
    expect((decoded! as Map).length, 20000);
    expect(
      elapsed,
      lessThan(1000),
      reason: 'checking a ${text.length}-character document',
    );
  });
}
